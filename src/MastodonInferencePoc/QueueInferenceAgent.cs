using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using MastodonInferencePoc.Models;

namespace MastodonInferencePoc;

/// <summary>
/// Coordinates the round trip: jobs queue -> Ollama -> results queue.
/// Owns the Azure Queue clients and the delete-after-success semantics.
/// </summary>
public sealed class QueueInferenceAgent
{
    // Ollama can be slow on first load; keep the input message hidden long enough
    // to finish inference and enqueue the result before it becomes visible again.
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(2);

    private readonly QueueClient _jobsQueue;
    private readonly QueueClient _resultsQueue;
    private readonly OllamaClient _ollamaClient;
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    public QueueInferenceAgent(
        QueueClient jobsQueue,
        QueueClient resultsQueue,
        OllamaClient ollamaClient,
        TextWriter? standardOut = null,
        TextWriter? standardError = null)
    {
        _jobsQueue = jobsQueue ?? throw new ArgumentNullException(nameof(jobsQueue));
        _resultsQueue = resultsQueue ?? throw new ArgumentNullException(nameof(resultsQueue));
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _out = standardOut ?? Console.Out;
        _error = standardError ?? Console.Error;
    }

    /// <summary>
    /// Builds a QueueClient with Base64 message encoding so payloads round-trip
    /// consistently regardless of content.
    /// </summary>
    public static QueueClient CreateQueueClient(string connectionString, string queueName) =>
        new(
            connectionString,
            queueName,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });

    /// <summary>
    /// Creates a synthetic test job and sends it to the jobs queue.
    /// Returns the generated job id.
    /// </summary>
    public async Task<string> EnqueueTestJobAsync(CancellationToken cancellationToken)
    {
        await _jobsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var job = new InferenceJob
        {
            JobId = Guid.NewGuid().ToString(),
            Type = "test",
            Prompt = "Reply with exactly: Hello from Ollama via Azure Queue",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var payload = JsonSerializer.Serialize(job, JsonDefaults.Options);
        await _jobsQueue.SendMessageAsync(payload, cancellationToken).ConfigureAwait(false);

        return job.JobId;
    }

    /// <summary>
    /// Receives at most one job, runs inference, writes a result, and only then
    /// deletes the original message. Returns true if a job was processed,
    /// false if the queue was empty.
    /// </summary>
    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await _jobsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await _resultsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Step 1: receive the input message. It becomes invisible for VisibilityTimeout.
        QueueMessage? message = await _jobsQueue
            .ReceiveMessageAsync(VisibilityTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            _out.WriteLine("No job available");
            return false;
        }

        // From here on the message has been received (and is invisible for the
        // visibility timeout). If anything fails, we make it visible again
        // immediately instead of waiting for the timeout to expire.
        try
        {
            var job = JsonSerializer.Deserialize<InferenceJob>(message.MessageText, JsonDefaults.Options)
                ?? throw new InvalidOperationException("Job message could not be deserialized.");

            // Step 2: call Ollama.
            var responseText = await _ollamaClient.GenerateAsync(job.Prompt, cancellationToken).ConfigureAwait(false);

            // Step 3: enqueue the result. If this throws, we do NOT delete the input.
            var result = new InferenceResult
            {
                JobId = job.JobId,
                Status = "complete",
                Result = responseText,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            var resultPayload = JsonSerializer.Serialize(result, JsonDefaults.Options);
            await _resultsQueue.SendMessageAsync(resultPayload, cancellationToken).ConfigureAwait(false);

            // Step 4: only now is it safe to remove the input message.
            await _jobsQueue
                .DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken)
                .ConfigureAwait(false);

            _out.WriteLine($"Processed job {job.JobId}");
            return true;
        }
        catch (Exception)
        {
            // Best-effort: make the message visible again immediately so an
            // immediate retry can pick it up rather than waiting out the
            // visibility timeout. The message is NOT deleted and its content is
            // left unchanged. Any failure here is logged but must not mask the
            // original processing failure, so we always rethrow it.
            await TryMakeVisibleAgainAsync(message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Best-effort reset of a message's visibility timeout to zero (immediately
    /// visible), leaving its content unchanged. Never throws: a secondary
    /// failure is logged so the caller can rethrow the original exception.
    /// </summary>
    private async Task TryMakeVisibleAgainAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _jobsQueue
                .UpdateMessageAsync(
                    message.MessageId,
                    message.PopReceipt,
                    message.MessageText,
                    TimeSpan.Zero,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error.WriteLine(
                $"Failed to make job message {message.MessageId} visible again: {ex.Message}");
        }
    }

    /// <summary>
    /// Peeks (non-destructively) at one result message and pretty-prints it.
    /// Returns true if a result was found.
    /// </summary>
    public async Task<bool> PeekResultAsync(CancellationToken cancellationToken)
    {
        await _resultsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        PeekedMessage? peeked = await _resultsQueue
            .PeekMessageAsync(cancellationToken)
            .ConfigureAwait(false);

        if (peeked is null)
        {
            _out.WriteLine("No result available");
            return false;
        }

        var result = JsonSerializer.Deserialize<InferenceResult>(peeked.MessageText, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Result message could not be deserialized.");

        var pretty = JsonSerializer.Serialize(result, JsonDefaults.PrettyOptions);
        _out.WriteLine(pretty);
        return true;
    }

    /// <summary>
    /// Receives (not peeks) at most one result message, pretty-prints it, and
    /// only then deletes it. Unlike <see cref="PeekResultAsync"/> this is
    /// destructive: the message is removed from the queue once consumed.
    /// If deserialization or output fails, the message is left undeleted so it
    /// becomes visible again after the visibility timeout. Returns true if a
    /// result was consumed, false if the queue was empty.
    /// </summary>
    public async Task<bool> ConsumeResultAsync(CancellationToken cancellationToken)
    {
        await _resultsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Receive with a non-zero visibility timeout so the message is hidden
        // while we process it. It is only deleted after successful output.
        QueueMessage? message = await _resultsQueue
            .ReceiveMessageAsync(VisibilityTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            _out.WriteLine("No result available");
            return false;
        }

        // Deserialize and print first. If either throws, we fall out of the
        // method without deleting, so the result reappears after the timeout.
        var result = JsonSerializer.Deserialize<InferenceResult>(message.MessageText, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Result message could not be deserialized.");

        var pretty = JsonSerializer.Serialize(result, JsonDefaults.PrettyOptions);
        _out.WriteLine(pretty);

        // Only now that output succeeded is it safe to remove the message.
        await _resultsQueue
            .DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}

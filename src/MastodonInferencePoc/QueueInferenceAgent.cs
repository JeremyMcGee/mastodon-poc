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

        var job = JsonSerializer.Deserialize<InferenceJob>(message.MessageText, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Job message could not be deserialized.");

        // Step 2: call Ollama.
        var responseText = await _ollamaClient.GenerateAsync(job.Prompt, cancellationToken).ConfigureAwait(false);

        // Step 3: enqueue the result. If this throws, we do NOT delete the input,
        // so the job reappears after the visibility timeout and can be retried.
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
}

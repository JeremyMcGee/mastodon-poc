using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using MastodonInferencePoc.Models;

namespace MastodonInferencePoc;

/// <summary>
/// Coordinates the round trip: jobs queue -> blob request -> Ollama -> blob
/// result -> results queue. Queues carry only small tickets; the request and
/// result payloads live in Blob Storage.
/// </summary>
public sealed class QueueInferenceAgent
{
    // Ollama can be slow on first load; keep the input message hidden long enough
    // to finish inference and enqueue the result before it becomes visible again.
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(2);

    private readonly QueueClient _jobsQueue;
    private readonly QueueClient _resultsQueue;
    private readonly BlobPayloadStore _blobStore;
    private readonly OllamaClient _ollamaClient;
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    public QueueInferenceAgent(
        QueueClient jobsQueue,
        QueueClient resultsQueue,
        BlobPayloadStore blobStore,
        OllamaClient ollamaClient,
        TextWriter? standardOut = null,
        TextWriter? standardError = null)
    {
        _jobsQueue = jobsQueue ?? throw new ArgumentNullException(nameof(jobsQueue));
        _resultsQueue = resultsQueue ?? throw new ArgumentNullException(nameof(resultsQueue));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
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
    /// Creates a synthetic test request, uploads it as a blob, and only then
    /// enqueues a small job ticket that references the blob. Returns the job id.
    /// </summary>
    public async Task<string> EnqueueTestJobAsync(CancellationToken cancellationToken)
    {
        await _blobStore.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        await _jobsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var jobId = Guid.NewGuid().ToString();
        var createdAt = DateTimeOffset.UtcNow;

        var request = new InferenceRequest
        {
            JobId = jobId,
            Type = "test",
            Prompt = "Reply with exactly: Hello from Ollama via Azure Queue",
            CreatedAt = createdAt,
        };

        // Step 1: upload the request blob FIRST. The queue ticket must never
        // reference a blob that does not yet exist.
        await _blobStore.UploadRequestAsync(request, cancellationToken).ConfigureAwait(false);

        // Step 2: only after the blob upload succeeds, enqueue the small ticket.
        var ticket = new JobTicket
        {
            JobId = jobId,
            Type = "test",
            RequestBlob = BlobPayloadStore.RequestBlobPath(jobId),
            CreatedAt = createdAt,
        };

        var payload = JsonSerializer.Serialize(ticket, JsonDefaults.Options);
        await _jobsQueue.SendMessageAsync(payload, cancellationToken).ConfigureAwait(false);

        return jobId;
    }

    /// <summary>
    /// Receives at most one job ticket, resolves its request blob, runs
    /// inference, uploads a result blob, enqueues a result ticket, and only then
    /// deletes the input message. Returns true if a job was processed, false if
    /// the queue was empty.
    /// </summary>
    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await _blobStore.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
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
        // immediately instead of waiting for the timeout to expire, and we never
        // delete it.
        try
        {
            // Step 2: deserialize the small work ticket.
            var ticket = JsonSerializer.Deserialize<JobTicket>(message.MessageText, JsonDefaults.Options)
                ?? throw new InvalidOperationException("Job ticket could not be deserialized.");

            if (string.IsNullOrWhiteSpace(ticket.JobId) || string.IsNullOrWhiteSpace(ticket.RequestBlob))
            {
                throw new InvalidOperationException("Job ticket is missing jobId or requestBlob.");
            }

            // Step 3: honour an optional deadline. An expired ticket is not
            // processed, but it is also not deleted here - it stays on the queue
            // (visible again) so the failure is explicit rather than silent.
            if (ticket.Deadline is { } deadline && deadline < DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException(
                    $"Job {ticket.JobId} expired at {deadline:o}; skipping.");
            }

            // Step 4/5: download and deserialize the request blob.
            var request = await _blobStore
                .DownloadRequestAsync(ticket.RequestBlob, cancellationToken)
                .ConfigureAwait(false);

            // Validate that the blob content matches the ticket.
            if (!string.Equals(request.JobId, ticket.JobId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Request blob jobId '{request.JobId}' does not match ticket jobId '{ticket.JobId}'.");
            }

            // Step 6: call Ollama.
            var responseText = await _ollamaClient
                .GenerateAsync(request.Prompt, cancellationToken)
                .ConfigureAwait(false);

            // Step 7: build the result payload.
            var result = new InferenceResult
            {
                JobId = ticket.JobId,
                Status = "complete",
                Result = responseText,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            // Step 8: upload the result blob FIRST.
            await _blobStore.UploadResultAsync(result, cancellationToken).ConfigureAwait(false);

            // Step 9: only after the result blob exists, enqueue the small ticket.
            var resultTicket = new ResultTicket
            {
                JobId = ticket.JobId,
                Status = "complete",
                ResultBlob = BlobPayloadStore.ResultBlobPath(ticket.JobId),
                CompletedAt = result.CompletedAt,
            };

            var resultPayload = JsonSerializer.Serialize(resultTicket, JsonDefaults.Options);
            await _resultsQueue.SendMessageAsync(resultPayload, cancellationToken).ConfigureAwait(false);

            // Step 10: only after BOTH the result blob and the result ticket
            // exist is it safe to remove the input message.
            await _jobsQueue
                .DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken)
                .ConfigureAwait(false);

            _out.WriteLine($"Processed job {ticket.JobId}");
            return true;
        }
        catch (Exception)
        {
            // Best-effort: make the message visible again immediately so a retry
            // can pick it up. The message is NOT deleted and its content is left
            // unchanged. Any failure here is logged but must not mask the
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
    /// Peeks (non-destructively) at one result ticket, downloads the referenced
    /// result blob, and pretty-prints the actual result. Leaves the queue
    /// message untouched. Returns true if a result was found.
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

        var result = await ResolveResultAsync(peeked.MessageText, cancellationToken).ConfigureAwait(false);

        var pretty = JsonSerializer.Serialize(result, JsonDefaults.PrettyOptions);
        _out.WriteLine(pretty);
        return true;
    }

    /// <summary>
    /// Receives one result ticket, downloads and deserializes the referenced
    /// result blob, pretty-prints it, and only then deletes the queue message.
    /// If any step fails, the message is left undeleted so it becomes visible
    /// again after the visibility timeout. Result blobs are never deleted.
    /// Returns true if a result was consumed, false if the queue was empty.
    /// </summary>
    public async Task<bool> ConsumeResultAsync(CancellationToken cancellationToken)
    {
        await _resultsQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        QueueMessage? message = await _resultsQueue
            .ReceiveMessageAsync(VisibilityTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            _out.WriteLine("No result available");
            return false;
        }

        // Resolve blob and print first. If either throws, we fall out without
        // deleting, so the result ticket reappears after the timeout.
        var result = await ResolveResultAsync(message.MessageText, cancellationToken).ConfigureAwait(false);

        var pretty = JsonSerializer.Serialize(result, JsonDefaults.PrettyOptions);
        _out.WriteLine(pretty);

        // Only now that processing succeeded is it safe to remove the ticket.
        // The result blob is intentionally retained for diagnostics.
        await _resultsQueue
            .DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Deserializes a result ticket, downloads the referenced result blob, and
    /// validates the blob's jobId matches the ticket's.
    /// </summary>
    private async Task<InferenceResult> ResolveResultAsync(string ticketJson, CancellationToken cancellationToken)
    {
        var ticket = JsonSerializer.Deserialize<ResultTicket>(ticketJson, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Result ticket could not be deserialized.");

        if (string.IsNullOrWhiteSpace(ticket.JobId) || string.IsNullOrWhiteSpace(ticket.ResultBlob))
        {
            throw new InvalidOperationException("Result ticket is missing jobId or resultBlob.");
        }

        var result = await _blobStore
            .DownloadResultAsync(ticket.ResultBlob, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(result.JobId, ticket.JobId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Result blob jobId '{result.JobId}' does not match ticket jobId '{ticket.JobId}'.");
        }

        return result;
    }
}

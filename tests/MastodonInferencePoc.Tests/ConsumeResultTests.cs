using System.Text.Json;
using Azure.Storage.Queues.Models;
using MastodonInferencePoc;
using MastodonInferencePoc.Models;
using MastodonInferencePoc.Tests.Fakes;

namespace MastodonInferencePoc.Tests;

public class ConsumeResultTests
{
    private const string MessageId = "result-msg-1";
    private const string PopReceipt = "result-pop-1";
    private const string JobId = "job-abc";

    private static QueueMessage BuildTicketMessage(string body) =>
        QueuesModelFactory.QueueMessage(
            messageId: MessageId,
            popReceipt: PopReceipt,
            body: BinaryData.FromString(body),
            dequeueCount: 1);

    private static QueueMessage BuildValidResultTicketMessage()
    {
        var ticket = new ResultTicket
        {
            JobId = JobId,
            Status = "complete",
            ResultBlob = BlobPayloadStore.ResultBlobPath(JobId),
            CompletedAt = DateTimeOffset.UtcNow,
        };

        return BuildTicketMessage(JsonSerializer.Serialize(ticket, JsonDefaults.Options));
    }

    private static InferenceResult ResultFor(string jobId) => new()
    {
        JobId = jobId,
        Status = "complete",
        Result = "Hello from Ollama via Azure Queue",
        CompletedAt = DateTimeOffset.UtcNow,
    };

    // A placeholder OllamaClient; result commands never touch Ollama.
    private static OllamaClient UnusedOllama() =>
        new(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/") }, "unused");

    [Fact]
    public async Task PeekResult_DoesNotDeleteOrHideTheResult()
    {
        var resultsQueue = new FakeQueueClient(BuildValidResultTicketMessage());
        var blobStore = new FakeBlobPayloadStore();
        blobStore.SeedResult(ResultFor(JobId));

        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, blobStore, UnusedOllama(),
            standardOut: TextWriter.Null);

        var found = await agent.PeekResultAsync(CancellationToken.None);

        Assert.True(found);
        // Peek is non-destructive: it must not receive (hide), delete, or update.
        Assert.Equal(1, resultsQueue.PeekCallCount);
        Assert.Equal(0, resultsQueue.ReceiveCallCount);
        Assert.Equal(0, resultsQueue.DeleteCallCount);
        Assert.Equal(0, resultsQueue.UpdateCallCount);
    }

    [Fact]
    public async Task ConsumeResult_DeletesSuccessfullyProcessedResult()
    {
        var resultsQueue = new FakeQueueClient(BuildValidResultTicketMessage());
        var blobStore = new FakeBlobPayloadStore();
        blobStore.SeedResult(ResultFor(JobId));

        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, blobStore, UnusedOllama(),
            standardOut: TextWriter.Null);

        var consumed = await agent.ConsumeResultAsync(CancellationToken.None);

        Assert.True(consumed);
        Assert.Equal(1, resultsQueue.ReceiveCallCount);
        Assert.Equal(1, resultsQueue.DeleteCallCount);
        Assert.Equal(MessageId, resultsQueue.DeletedMessageId);
        Assert.Equal(PopReceipt, resultsQueue.DeletedPopReceipt);
        Assert.Equal(0, resultsQueue.PeekCallCount);
        // Result blob is retained for diagnostics.
        Assert.True(blobStore.HasResult(JobId));
    }

    [Fact]
    public async Task ConsumeResult_MissingResultBlob_LeavesTicketUndeleted()
    {
        // Valid ticket, but the referenced result blob was never uploaded.
        var resultsQueue = new FakeQueueClient(BuildValidResultTicketMessage());
        var blobStore = new FakeBlobPayloadStore();

        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, blobStore, UnusedOllama(),
            standardOut: TextWriter.Null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => agent.ConsumeResultAsync(CancellationToken.None));

        Assert.Equal(0, resultsQueue.DeleteCallCount);
    }

    [Fact]
    public async Task ConsumeResult_MalformedTicket_LeavesTicketUndeleted()
    {
        var resultsQueue = new FakeQueueClient(BuildTicketMessage("this is not json"));
        var blobStore = new FakeBlobPayloadStore();

        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, blobStore, UnusedOllama(),
            standardOut: TextWriter.Null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => agent.ConsumeResultAsync(CancellationToken.None));

        Assert.Equal(0, resultsQueue.DeleteCallCount);
    }

    [Fact]
    public async Task ConsumeResult_JobIdMismatch_LeavesTicketUndeleted()
    {
        var resultsQueue = new FakeQueueClient(BuildValidResultTicketMessage());
        var blobStore = new FakeBlobPayloadStore();
        // Result blob at the ticket's path but with a different jobId inside.
        blobStore.SeedResult(new InferenceResult
        {
            JobId = "different-job",
            Status = "complete",
            Result = "x",
            CompletedAt = DateTimeOffset.UtcNow,
        });
        // The seed above went to results/different-job.json; place a mismatching
        // blob at the ticket's expected path instead.
        blobStore.SeedResultAt(BlobPayloadStore.ResultBlobPath(JobId), new InferenceResult
        {
            JobId = "different-job",
            Status = "complete",
            Result = "x",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, blobStore, UnusedOllama(),
            standardOut: TextWriter.Null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ConsumeResultAsync(CancellationToken.None));

        Assert.Contains("does not match", ex.Message);
        Assert.Equal(0, resultsQueue.DeleteCallCount);
    }

    [Fact]
    public async Task ConsumeResult_EmptyQueue_PrintsNoResultAndReturnsFalse()
    {
        var output = new StringWriter();
        var resultsQueue = new FakeQueueClient(messageToReturn: null);
        var blobStore = new FakeBlobPayloadStore();

        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, blobStore, UnusedOllama(),
            standardOut: output);

        var consumed = await agent.ConsumeResultAsync(CancellationToken.None);

        Assert.False(consumed);
        Assert.Equal(0, resultsQueue.DeleteCallCount);
        Assert.Contains("No result available", output.ToString());
    }
}

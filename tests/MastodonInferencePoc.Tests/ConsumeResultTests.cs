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

    private static QueueMessage BuildResultMessage(string body) =>
        QueuesModelFactory.QueueMessage(
            messageId: MessageId,
            popReceipt: PopReceipt,
            body: BinaryData.FromString(body),
            dequeueCount: 1);

    private static QueueMessage BuildValidResultMessage()
    {
        var result = new InferenceResult
        {
            JobId = "job-abc",
            Status = "complete",
            Result = "Hello from Ollama via Azure Queue",
            CompletedAt = DateTimeOffset.UtcNow,
        };

        return BuildResultMessage(JsonSerializer.Serialize(result, JsonDefaults.Options));
    }

    // A placeholder OllamaClient; result commands never touch Ollama.
    private static OllamaClient UnusedOllama() =>
        new(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/") }, "unused");

    [Fact]
    public async Task PeekResult_DoesNotDeleteOrHideTheResult()
    {
        var resultsQueue = new FakeQueueClient(BuildValidResultMessage());
        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, UnusedOllama(),
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
        var resultsQueue = new FakeQueueClient(BuildValidResultMessage());
        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, UnusedOllama(),
            standardOut: TextWriter.Null);

        var consumed = await agent.ConsumeResultAsync(CancellationToken.None);

        Assert.True(consumed);
        Assert.Equal(1, resultsQueue.ReceiveCallCount);
        Assert.Equal(1, resultsQueue.DeleteCallCount);
        Assert.Equal(MessageId, resultsQueue.DeletedMessageId);
        Assert.Equal(PopReceipt, resultsQueue.DeletedPopReceipt);
        // Consume uses receive (hide), never peek.
        Assert.Equal(0, resultsQueue.PeekCallCount);
    }

    [Fact]
    public async Task ConsumeResult_LeavesFailedResultUndeleted()
    {
        // Body is not valid InferenceResult JSON, so deserialization/output fails.
        var resultsQueue = new FakeQueueClient(BuildResultMessage("this is not json"));
        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, UnusedOllama(),
            standardOut: TextWriter.Null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => agent.ConsumeResultAsync(CancellationToken.None));

        // Failure before delete: message stays on the queue.
        Assert.Equal(0, resultsQueue.DeleteCallCount);
    }

    [Fact]
    public async Task ConsumeResult_EmptyQueue_PrintsNoResultAndReturnsFalse()
    {
        var output = new StringWriter();
        var resultsQueue = new FakeQueueClient(messageToReturn: null);
        var agent = new QueueInferenceAgent(
            resultsQueue, resultsQueue, UnusedOllama(),
            standardOut: output);

        var consumed = await agent.ConsumeResultAsync(CancellationToken.None);

        Assert.False(consumed);
        Assert.Equal(0, resultsQueue.DeleteCallCount);
        Assert.Contains("No result available", output.ToString());
    }
}

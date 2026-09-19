using System.Net;
using System.Text.Json;
using Azure.Storage.Queues.Models;
using MastodonInferencePoc;
using MastodonInferencePoc.Models;
using MastodonInferencePoc.Tests.Fakes;

namespace MastodonInferencePoc.Tests;

public class ProcessOnceTests
{
    private const string MessageId = "msg-1";
    private const string PopReceipt = "pop-1";

    private static QueueMessage BuildJobMessage()
    {
        var job = new InferenceJob
        {
            JobId = "job-abc",
            Type = "test",
            Prompt = "hello",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var body = JsonSerializer.Serialize(job, JsonDefaults.Options);

        return QueuesModelFactory.QueueMessage(
            messageId: MessageId,
            popReceipt: PopReceipt,
            body: BinaryData.FromString(body),
            dequeueCount: 1);
    }

    private static OllamaClient OllamaReturning(string response)
    {
        var payload = new OllamaGenerateResponse { Model = "test", Response = response, Done = true };
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        return new OllamaClient(httpClient, "test-model");
    }

    private static OllamaClient OllamaThatFails()
    {
        var httpClient = new HttpClient(StubHttpMessageHandler.Throwing())
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
        };
        return new OllamaClient(httpClient, "test-model");
    }

    [Fact]
    public async Task Failure_DoesNotDeleteTheInputMessage()
    {
        var jobsQueue = new FakeQueueClient(BuildJobMessage());
        var resultsQueue = new FakeQueueClient();
        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, OllamaThatFails(),
            standardError: TextWriter.Null);

        // Ollama fails, so ProcessOnceAsync must throw and NOT delete the input.
        await Assert.ThrowsAnyAsync<Exception>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        Assert.Equal(0, jobsQueue.DeleteCallCount);
    }

    [Fact]
    public async Task Failure_MakesMessageVisibleAgainWithZeroTimeout()
    {
        var jobsQueue = new FakeQueueClient(BuildJobMessage());
        var resultsQueue = new FakeQueueClient();
        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, OllamaThatFails(),
            standardError: TextWriter.Null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        // Exactly one UpdateMessageAsync, using the received id + pop receipt,
        // content unchanged, visibility timeout zero.
        Assert.Equal(1, jobsQueue.UpdateCallCount);
        Assert.Equal(MessageId, jobsQueue.UpdatedMessageId);
        Assert.Equal(PopReceipt, jobsQueue.UpdatedPopReceipt);
        Assert.Equal(TimeSpan.Zero, jobsQueue.UpdatedVisibilityTimeout);

        // Content left unchanged: the text passed back equals the received body.
        var restored = JsonSerializer.Deserialize<InferenceJob>(
            jobsQueue.UpdatedMessageText!, JsonDefaults.Options);
        Assert.NotNull(restored);
        Assert.Equal("job-abc", restored!.JobId);
    }

    [Fact]
    public async Task Failure_OnResultEnqueue_DoesNotDeleteAndResetsVisibility()
    {
        var jobsQueue = new FakeQueueClient(BuildJobMessage());
        var resultsQueue = new FakeQueueClient { FailOnSend = true };
        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, OllamaReturning("some output"),
            standardError: TextWriter.Null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        Assert.Equal(0, jobsQueue.DeleteCallCount);
        Assert.Equal(1, jobsQueue.UpdateCallCount);
        Assert.Equal(TimeSpan.Zero, jobsQueue.UpdatedVisibilityTimeout);
    }

    [Fact]
    public async Task Success_DeletesTheInputExactlyOnceAndDoesNotResetVisibility()
    {
        var jobsQueue = new FakeQueueClient(BuildJobMessage());
        var resultsQueue = new FakeQueueClient();
        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, OllamaReturning("Hello from Ollama via Azure Queue"),
            standardOut: TextWriter.Null);

        var processed = await agent.ProcessOnceAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(1, resultsQueue.SendCallCount);
        Assert.Equal(1, jobsQueue.DeleteCallCount);
        Assert.Equal(MessageId, jobsQueue.DeletedMessageId);
        Assert.Equal(PopReceipt, jobsQueue.DeletedPopReceipt);
        // No visibility reset on the happy path.
        Assert.Equal(0, jobsQueue.UpdateCallCount);
    }
}

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
    private const string JobId = "job-abc";

    private static QueueMessage BuildTicketMessage(JobTicket ticket)
    {
        var body = JsonSerializer.Serialize(ticket, JsonDefaults.Options);
        return QueuesModelFactory.QueueMessage(
            messageId: MessageId,
            popReceipt: PopReceipt,
            body: BinaryData.FromString(body),
            dequeueCount: 1);
    }

    private static JobTicket ValidTicket(DateTimeOffset? deadline = null) => new()
    {
        JobId = JobId,
        Type = "test",
        RequestBlob = BlobPayloadStore.RequestBlobPath(JobId),
        CreatedAt = DateTimeOffset.UtcNow,
        Deadline = deadline,
    };

    private static InferenceRequest RequestFor(string jobId) => new()
    {
        JobId = jobId,
        Type = "test",
        Prompt = "hello",
        CreatedAt = DateTimeOffset.UtcNow,
    };

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
    public async Task Success_UploadsResultBlob_EnqueuesTicket_ThenDeletesInputOnce()
    {
        var jobsQueue = new FakeQueueClient(BuildTicketMessage(ValidTicket()));
        var resultsQueue = new FakeQueueClient();
        var blobStore = new FakeBlobPayloadStore();
        blobStore.SeedRequest(RequestFor(JobId));

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaReturning("Hello from Ollama via Azure Queue"),
            standardOut: TextWriter.Null);

        var processed = await agent.ProcessOnceAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(1, blobStore.ResultUploadCount);      // result blob uploaded
        Assert.Equal(1, resultsQueue.SendCallCount);       // result ticket enqueued
        Assert.Equal(1, jobsQueue.DeleteCallCount);        // input deleted exactly once
        Assert.Equal(MessageId, jobsQueue.DeletedMessageId);
        Assert.Equal(PopReceipt, jobsQueue.DeletedPopReceipt);
        Assert.Equal(0, jobsQueue.UpdateCallCount);        // no visibility reset on success
        Assert.Equal("Hello from Ollama via Azure Queue", blobStore.LastUploadedResult!.Result);
    }

    [Fact]
    public async Task OllamaFailure_DoesNotUploadResult_DoesNotDelete_ResetsVisibility()
    {
        var jobsQueue = new FakeQueueClient(BuildTicketMessage(ValidTicket()));
        var resultsQueue = new FakeQueueClient();
        var blobStore = new FakeBlobPayloadStore();
        blobStore.SeedRequest(RequestFor(JobId));

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaThatFails(),
            standardError: TextWriter.Null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        Assert.Equal(0, blobStore.ResultUploadCount);      // no success result uploaded
        Assert.Equal(0, resultsQueue.SendCallCount);
        Assert.Equal(0, jobsQueue.DeleteCallCount);        // input preserved
        Assert.Equal(1, jobsQueue.UpdateCallCount);        // made visible again
        Assert.Equal(TimeSpan.Zero, jobsQueue.UpdatedVisibilityTimeout);
    }

    [Fact]
    public async Task ResultTicketEnqueueFailure_DoesNotDelete_ResetsVisibility()
    {
        var jobsQueue = new FakeQueueClient(BuildTicketMessage(ValidTicket()));
        var resultsQueue = new FakeQueueClient { FailOnSend = true };
        var blobStore = new FakeBlobPayloadStore();
        blobStore.SeedRequest(RequestFor(JobId));

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaReturning("out"),
            standardError: TextWriter.Null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        // Result blob was uploaded before the enqueue, but the input is NOT
        // deleted because the result ticket never made it onto the queue.
        Assert.Equal(0, jobsQueue.DeleteCallCount);
        Assert.Equal(1, jobsQueue.UpdateCallCount);
    }

    [Fact]
    public async Task MissingRequestBlob_DoesNotDelete_ResetsVisibility()
    {
        // No SeedRequest: the referenced blob does not exist.
        var jobsQueue = new FakeQueueClient(BuildTicketMessage(ValidTicket()));
        var resultsQueue = new FakeQueueClient();
        var blobStore = new FakeBlobPayloadStore();

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaReturning("out"),
            standardError: TextWriter.Null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        Assert.Equal(0, jobsQueue.DeleteCallCount);        // not silently lost
        Assert.Equal(0, blobStore.ResultUploadCount);
        Assert.Equal(1, jobsQueue.UpdateCallCount);
    }

    [Fact]
    public async Task JobIdMismatchBetweenTicketAndBlob_IsRejected_DoesNotDelete()
    {
        var jobsQueue = new FakeQueueClient(BuildTicketMessage(ValidTicket()));
        var resultsQueue = new FakeQueueClient();
        var blobStore = new FakeBlobPayloadStore();
        // Seed a request blob at the ticket's path but with a different jobId
        // inside it - the agent must detect the mismatch.
        blobStore.SeedRequestAt(BlobPayloadStore.RequestBlobPath(JobId), new InferenceRequest
        {
            JobId = "some-other-job",
            Type = "test",
            Prompt = "hello",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaReturning("out"),
            standardError: TextWriter.Null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        Assert.Contains("does not match", ex.Message);
        Assert.Equal(0, jobsQueue.DeleteCallCount);
        Assert.Equal(0, blobStore.ResultUploadCount);
    }

    [Fact]
    public async Task ExpiredTicket_IsNotProcessed_DoesNotDelete()
    {
        var expired = ValidTicket(deadline: DateTimeOffset.UtcNow.AddMinutes(-5));
        var jobsQueue = new FakeQueueClient(BuildTicketMessage(expired));
        var resultsQueue = new FakeQueueClient();
        var blobStore = new FakeBlobPayloadStore();
        blobStore.SeedRequest(RequestFor(JobId));

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaReturning("out"),
            standardError: TextWriter.Null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ProcessOnceAsync(CancellationToken.None));

        Assert.Contains("expired", ex.Message);
        Assert.Equal(0, jobsQueue.DeleteCallCount);
        Assert.Equal(0, blobStore.ResultUploadCount);
    }

    [Fact]
    public async Task EmptyQueue_PrintsNoJob_ReturnsFalse()
    {
        var output = new StringWriter();
        var jobsQueue = new FakeQueueClient(messageToReturn: null);
        var resultsQueue = new FakeQueueClient();
        var blobStore = new FakeBlobPayloadStore();

        var agent = new QueueInferenceAgent(
            jobsQueue, resultsQueue, blobStore, OllamaReturning("out"),
            standardOut: output);

        var processed = await agent.ProcessOnceAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Contains("No job available", output.ToString());
        Assert.Equal(0, jobsQueue.DeleteCallCount);
    }
}

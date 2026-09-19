using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace MastodonInferencePoc.Tests.Fakes;

/// <summary>
/// A test double for <see cref="QueueClient"/>. QueueClient exposes virtual
/// members and a protected parameterless constructor specifically so it can be
/// subclassed in tests, so no production interface is needed. This fake records
/// the calls the agent makes and lets a test force a send failure.
/// </summary>
internal sealed class FakeQueueClient : QueueClient
{
    private readonly QueueMessage? _messageToReturn;

    public FakeQueueClient(QueueMessage? messageToReturn = null)
    {
        _messageToReturn = messageToReturn;
    }

    // Recorded interactions.
    public int DeleteCallCount { get; private set; }
    public string? DeletedMessageId { get; private set; }
    public string? DeletedPopReceipt { get; private set; }

    public int UpdateCallCount { get; private set; }
    public string? UpdatedMessageId { get; private set; }
    public string? UpdatedPopReceipt { get; private set; }
    public string? UpdatedMessageText { get; private set; }
    public TimeSpan? UpdatedVisibilityTimeout { get; private set; }

    public int SendCallCount { get; private set; }

    /// <summary>When true, SendMessageAsync throws to simulate an enqueue failure.</summary>
    public bool FailOnSend { get; set; }

    public override Task<Response> CreateIfNotExistsAsync(
        System.Collections.Generic.IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Response>(new FakeResponse());

    public override Task<Response<QueueMessage>> ReceiveMessageAsync(
        TimeSpan? visibilityTimeout = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Response.FromValue(_messageToReturn!, new FakeResponse()));

    public override Task<Response<SendReceipt>> SendMessageAsync(
        string messageText,
        CancellationToken cancellationToken = default)
    {
        SendCallCount++;

        if (FailOnSend)
        {
            throw new InvalidOperationException("Simulated result enqueue failure.");
        }

        var receipt = QueuesModelFactory.SendReceipt(
            messageId: Guid.NewGuid().ToString(),
            insertionTime: DateTimeOffset.UtcNow,
            expirationTime: DateTimeOffset.UtcNow.AddDays(7),
            popReceipt: "send-pop",
            timeNextVisible: DateTimeOffset.UtcNow);

        return Task.FromResult(Response.FromValue(receipt, new FakeResponse()));
    }

    public override Task<Response> DeleteMessageAsync(
        string messageId,
        string popReceipt,
        CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        DeletedMessageId = messageId;
        DeletedPopReceipt = popReceipt;
        return Task.FromResult<Response>(new FakeResponse());
    }

    public override Task<Response<UpdateReceipt>> UpdateMessageAsync(
        string messageId,
        string popReceipt,
        string messageText = default!,
        TimeSpan visibilityTimeout = default,
        CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        UpdatedMessageId = messageId;
        UpdatedPopReceipt = popReceipt;
        UpdatedMessageText = messageText;
        UpdatedVisibilityTimeout = visibilityTimeout;

        var receipt = QueuesModelFactory.UpdateReceipt("updated-pop", DateTimeOffset.UtcNow);

        return Task.FromResult(Response.FromValue(receipt, new FakeResponse()));
    }
}

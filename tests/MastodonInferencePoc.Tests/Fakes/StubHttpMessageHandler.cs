using System.Net;

namespace MastodonInferencePoc.Tests.Fakes;

/// <summary>
/// An HttpMessageHandler that returns a canned response (or throws), so
/// OllamaClient can be exercised without any real network call.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string body) =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        });

    public static StubHttpMessageHandler Throwing() =>
        new(_ => throw new HttpRequestException("Simulated Ollama connection failure."));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(_responder(request));
}

using Azure;
using Azure.Core;

namespace MastodonInferencePoc.Tests.Fakes;

/// <summary>
/// Bare-minimum <see cref="Response"/> so tests can wrap values with
/// <see cref="Response.FromValue{T}(T, Response)"/> without pulling in a
/// mocking framework. Only the members the SDK touches are implemented.
/// </summary>
internal sealed class FakeResponse : Response
{
    public override int Status => 200;
    public override string ReasonPhrase => "OK";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = string.Empty;

    public override void Dispose()
    {
    }

    protected override bool ContainsHeader(string name) => false;

    protected override IEnumerable<HttpHeader> EnumerateHeaders() => Array.Empty<HttpHeader>();

    protected override bool TryGetHeader(string name, out string? value)
    {
        value = null;
        return false;
    }

    protected override bool TryGetHeaderValues(string name, out IEnumerable<string>? values)
    {
        values = null;
        return false;
    }
}

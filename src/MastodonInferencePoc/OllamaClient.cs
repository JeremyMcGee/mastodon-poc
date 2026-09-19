using System.Net.Http.Json;
using MastodonInferencePoc.Models;

namespace MastodonInferencePoc;

/// <summary>
/// Thin wrapper over Ollama's HTTP API. Uses the supplied <see cref="HttpClient"/>
/// as-is; TLS certificate validation is never disabled.
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OllamaClient(HttpClient httpClient, string model)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("An Ollama model name is required.", nameof(model));
        }

        _model = model;
    }

    /// <summary>
    /// Calls POST {BaseAddress}/api/generate with streaming disabled and returns
    /// the model's textual response. Throws if the call fails or the body is unusable.
    /// </summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var request = new OllamaGenerateRequest
        {
            Model = _model,
            Prompt = prompt,
            Stream = false,
        };

        using var response = await _httpClient
            .PostAsJsonAsync("api/generate", request, JsonDefaults.Options, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            throw new OllamaException(
                $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }

        var payload = await response.Content
            .ReadFromJsonAsync<OllamaGenerateResponse>(JsonDefaults.Options, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null)
        {
            throw new OllamaException("Ollama response body was empty or not valid JSON.");
        }

        if (payload.Response is null)
        {
            throw new OllamaException("Ollama response did not contain a 'response' property.");
        }

        return payload.Response;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return "<unreadable>";
        }
    }
}

/// <summary>
/// Raised when a call to Ollama fails or produces an unusable result.
/// </summary>
public sealed class OllamaException : Exception
{
    public OllamaException(string message) : base(message)
    {
    }

    public OllamaException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

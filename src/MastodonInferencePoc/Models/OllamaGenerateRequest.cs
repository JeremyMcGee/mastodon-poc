using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// Request body for Ollama's POST /api/generate endpoint.
/// </summary>
public sealed record OllamaGenerateRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    // The PoC uses non-streaming responses so we can deserialize a single object.
    [JsonPropertyName("stream")]
    public bool Stream { get; init; }
}

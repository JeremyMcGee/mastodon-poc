using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// Response body from Ollama's POST /api/generate endpoint (non-streaming).
/// Only the fields the PoC needs are modelled.
/// </summary>
public sealed record OllamaGenerateResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("response")]
    public string? Response { get; init; }

    [JsonPropertyName("done")]
    public bool Done { get; init; }
}

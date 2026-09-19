using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// The full inference request payload. Stored as a blob at requests/{jobId}.json;
/// the queue only carries a small <see cref="JobTicket"/> that references it.
/// For this PoC the prompt remains a simple string.
/// </summary>
public sealed record InferenceRequest
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

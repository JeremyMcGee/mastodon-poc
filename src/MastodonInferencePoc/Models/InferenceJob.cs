using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// A unit of work read from the jobs queue.
/// </summary>
public sealed record InferenceJob
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

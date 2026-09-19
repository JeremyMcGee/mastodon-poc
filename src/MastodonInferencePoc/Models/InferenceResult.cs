using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// The outcome written to the results queue after a job is processed.
/// </summary>
public sealed record InferenceResult
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }
}

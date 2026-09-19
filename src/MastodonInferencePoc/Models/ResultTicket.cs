using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// The small ticket placed on the results queue. It references the result blob
/// rather than carrying the model output itself.
/// </summary>
public sealed record ResultTicket
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Blob path of the result payload, e.g. results/{jobId}.json.</summary>
    [JsonPropertyName("resultBlob")]
    public required string ResultBlob { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }
}

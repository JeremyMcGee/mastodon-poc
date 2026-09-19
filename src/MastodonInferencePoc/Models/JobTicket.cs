using System.Text.Json.Serialization;

namespace MastodonInferencePoc.Models;

/// <summary>
/// The small work ticket placed on the jobs queue. It references the request
/// blob rather than carrying the prompt itself.
/// </summary>
public sealed record JobTicket
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Blob path of the request payload, e.g. requests/{jobId}.json.</summary>
    [JsonPropertyName("requestBlob")]
    public required string RequestBlob { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Optional processing deadline. When present and in the past at receive
    /// time, the ticket is considered expired and is not processed.
    /// </summary>
    [JsonPropertyName("deadline")]
    public DateTimeOffset? Deadline { get; init; }
}

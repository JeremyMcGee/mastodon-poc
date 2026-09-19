using MastodonInferencePoc;
using MastodonInferencePoc.Models;

namespace MastodonInferencePoc.Tests.Fakes;

/// <summary>
/// In-memory <see cref="BlobPayloadStore"/> for tests. Records uploads and lets
/// a test seed request blobs, simulate a missing/malformed blob, or force an
/// upload failure. No real Azure calls.
/// </summary>
internal sealed class FakeBlobPayloadStore : BlobPayloadStore
{
    // blobPath -> deserialized payload
    private readonly Dictionary<string, InferenceRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InferenceResult> _results = new(StringComparer.Ordinal);

    public int EnsureContainerCallCount { get; private set; }
    public int RequestUploadCount { get; private set; }
    public int ResultUploadCount { get; private set; }

    public InferenceResult? LastUploadedResult { get; private set; }

    /// <summary>When true, UploadResultAsync throws to simulate a blob failure.</summary>
    public bool FailOnResultUpload { get; set; }

    public override Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        EnsureContainerCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>Seed a request blob so process-once can download it.</summary>
    public void SeedRequest(InferenceRequest request) =>
        _requests[BlobPayloadStore.RequestBlobPath(request.JobId)] = request;

    /// <summary>
    /// Seed a request blob at an explicit path (used to simulate a jobId
    /// mismatch between the ticket's blob path and the blob's contents).
    /// </summary>
    public void SeedRequestAt(string blobPath, InferenceRequest request) =>
        _requests[blobPath] = request;

    /// <summary>Seed a result blob so peek/consume can download it.</summary>
    public void SeedResult(InferenceResult result) =>
        _results[BlobPayloadStore.ResultBlobPath(result.JobId)] = result;

    /// <summary>Seed a result blob at an explicit path (for jobId-mismatch tests).</summary>
    public void SeedResultAt(string blobPath, InferenceResult result) =>
        _results[blobPath] = result;

    public bool HasResult(string jobId) =>
        _results.ContainsKey(BlobPayloadStore.ResultBlobPath(jobId));

    public override Task UploadRequestAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        RequestUploadCount++;
        _requests[BlobPayloadStore.RequestBlobPath(request.JobId)] = request;
        return Task.CompletedTask;
    }

    public override Task UploadResultAsync(InferenceResult result, CancellationToken cancellationToken)
    {
        if (FailOnResultUpload)
        {
            throw new InvalidOperationException("Simulated result blob upload failure.");
        }

        ResultUploadCount++;
        LastUploadedResult = result;
        _results[BlobPayloadStore.ResultBlobPath(result.JobId)] = result;
        return Task.CompletedTask;
    }

    public override Task<InferenceRequest> DownloadRequestAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (!_requests.TryGetValue(blobPath, out var request))
        {
            // Mirrors a missing blob: the SDK would throw, and so do we.
            throw new InvalidOperationException($"Request blob '{blobPath}' not found.");
        }

        return Task.FromResult(request);
    }

    public override Task<InferenceResult> DownloadResultAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (!_results.TryGetValue(blobPath, out var result))
        {
            throw new InvalidOperationException($"Result blob '{blobPath}' not found.");
        }

        return Task.FromResult(result);
    }
}

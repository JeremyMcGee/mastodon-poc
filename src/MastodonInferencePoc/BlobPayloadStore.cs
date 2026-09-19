using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MastodonInferencePoc.Models;

namespace MastodonInferencePoc;

/// <summary>
/// Stores and retrieves inference payloads in Azure Blob Storage. Queues carry
/// only small tickets that reference blobs owned by this store.
///
/// Methods are virtual so tests can subclass this with an in-memory fake; the
/// PoC intentionally avoids introducing an interface purely for abstraction.
/// </summary>
public class BlobPayloadStore
{
    private readonly BlobContainerClient _container;

    // Blob path conventions.
    public static string RequestBlobPath(string jobId) => $"requests/{jobId}.json";
    public static string ResultBlobPath(string jobId) => $"results/{jobId}.json";

    // Parameterless constructor for test subclasses that override every method.
    protected BlobPayloadStore()
    {
        _container = null!;
    }

    public BlobPayloadStore(string connectionString, string containerName)
    {
        _container = new BlobContainerClient(connectionString, containerName);
    }

    /// <summary>
    /// Creates the container if it does not exist. The container is created with
    /// <see cref="PublicAccessType.None"/> so it remains private.
    /// </summary>
    public virtual async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        await _container
            .CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Uploads the request payload to requests/{jobId}.json.</summary>
    public virtual Task UploadRequestAsync(InferenceRequest request, CancellationToken cancellationToken) =>
        UploadJsonAsync(RequestBlobPath(request.JobId), request, cancellationToken);

    /// <summary>Uploads the result payload to results/{jobId}.json.</summary>
    public virtual Task UploadResultAsync(InferenceResult result, CancellationToken cancellationToken) =>
        UploadJsonAsync(ResultBlobPath(result.JobId), result, cancellationToken);

    /// <summary>Downloads and deserializes the request payload at the given blob path.</summary>
    public virtual Task<InferenceRequest> DownloadRequestAsync(string blobPath, CancellationToken cancellationToken) =>
        DownloadJsonAsync<InferenceRequest>(blobPath, cancellationToken);

    /// <summary>Downloads and deserializes the result payload at the given blob path.</summary>
    public virtual Task<InferenceResult> DownloadResultAsync(string blobPath, CancellationToken cancellationToken) =>
        DownloadJsonAsync<InferenceResult>(blobPath, cancellationToken);

    private async Task UploadJsonAsync<T>(string blobPath, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        var blob = _container.GetBlobClient(blobPath);

        // overwrite: true so re-processing a job replaces rather than fails.
        await blob
            .UploadAsync(BinaryData.FromBytes(json), overwrite: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> DownloadJsonAsync<T>(string blobPath, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(blobPath);

        BlobDownloadResult download = await blob
            .DownloadContentAsync(cancellationToken)
            .ConfigureAwait(false);

        var text = download.Content.ToString();

        var value = JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Blob '{blobPath}' did not deserialize into {typeof(T).Name}.");
        }

        return value;
    }
}

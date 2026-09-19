namespace MastodonInferencePoc;

/// <summary>
/// Strongly typed view over the environment variables the PoC needs.
/// Defaults are applied here so the rest of the app never re-implements them.
/// </summary>
public sealed class Configuration
{
    public const string DefaultOllamaBaseUrl = "http://127.0.0.1:11434";
    public const string DefaultJobsQueue = "inference-jobs";
    public const string DefaultResultsQueue = "inference-results";

    public string? AzureStorageConnectionString { get; }
    public string OllamaBaseUrl { get; }
    public string? OllamaModel { get; }
    public string JobsQueueName { get; }
    public string ResultsQueueName { get; }

    private Configuration(
        string? azureStorageConnectionString,
        string ollamaBaseUrl,
        string? ollamaModel,
        string jobsQueueName,
        string resultsQueueName)
    {
        AzureStorageConnectionString = azureStorageConnectionString;
        OllamaBaseUrl = ollamaBaseUrl;
        OllamaModel = ollamaModel;
        JobsQueueName = jobsQueueName;
        ResultsQueueName = resultsQueueName;
    }

    /// <summary>
    /// Builds configuration from the process environment.
    /// </summary>
    public static Configuration FromEnvironment() =>
        FromLookup(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Builds configuration from an arbitrary lookup function.
    /// Kept internal-friendly (public) so unit tests can supply values
    /// without mutating the real process environment.
    /// </summary>
    public static Configuration FromLookup(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        return new Configuration(
            azureStorageConnectionString: Trimmed(lookup("AZURE_STORAGE_CONNECTION_STRING")),
            ollamaBaseUrl: Trimmed(lookup("OLLAMA_BASE_URL")) ?? DefaultOllamaBaseUrl,
            ollamaModel: Trimmed(lookup("OLLAMA_MODEL")),
            jobsQueueName: Trimmed(lookup("INFERENCE_JOBS_QUEUE")) ?? DefaultJobsQueue,
            resultsQueueName: Trimmed(lookup("INFERENCE_RESULTS_QUEUE")) ?? DefaultResultsQueue);
    }

    /// <summary>
    /// The Azure connection string is required for any queue operation.
    /// Returns the value or throws a clear error.
    /// </summary>
    public string RequireAzureStorageConnectionString()
    {
        if (string.IsNullOrWhiteSpace(AzureStorageConnectionString))
        {
            throw new InvalidOperationException(
                "AZURE_STORAGE_CONNECTION_STRING is not set. It is required to talk to Azure Queue Storage.");
        }

        return AzureStorageConnectionString;
    }

    /// <summary>
    /// The Ollama model is only required when actually processing a job.
    /// </summary>
    public string RequireOllamaModel()
    {
        if (string.IsNullOrWhiteSpace(OllamaModel))
        {
            throw new InvalidOperationException(
                "OLLAMA_MODEL is not set. It is required to run inference.");
        }

        return OllamaModel;
    }

    private static string? Trimmed(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

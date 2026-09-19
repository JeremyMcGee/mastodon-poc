using MastodonInferencePoc;

namespace MastodonInferencePoc.Tests;

public class ConfigurationTests
{
    private static Func<string, string?> Lookup(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Fact]
    public void Defaults_AreAppliedWhenOptionalVarsMissing()
    {
        // Only the mandatory-for-queue connection string is provided.
        var config = Configuration.FromLookup(Lookup(new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
        }));

        Assert.Equal("http://127.0.0.1:11434", config.OllamaBaseUrl);
        Assert.Equal("inference-jobs", config.JobsQueueName);
        Assert.Equal("inference-results", config.ResultsQueueName);
        Assert.Null(config.OllamaModel);
    }

    [Fact]
    public void ProvidedValues_OverrideDefaults()
    {
        var config = Configuration.FromLookup(Lookup(new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = "conn",
            ["OLLAMA_BASE_URL"] = "http://localhost:9999",
            ["OLLAMA_MODEL"] = "llama3.2",
            ["INFERENCE_JOBS_QUEUE"] = "jobs-custom",
            ["INFERENCE_RESULTS_QUEUE"] = "results-custom",
        }));

        Assert.Equal("http://localhost:9999", config.OllamaBaseUrl);
        Assert.Equal("llama3.2", config.OllamaModel);
        Assert.Equal("jobs-custom", config.JobsQueueName);
        Assert.Equal("results-custom", config.ResultsQueueName);
    }

    [Fact]
    public void WhitespaceValues_AreTreatedAsUnset()
    {
        var config = Configuration.FromLookup(Lookup(new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = "   ",
            ["OLLAMA_BASE_URL"] = "",
            ["OLLAMA_MODEL"] = "  ",
        }));

        Assert.Null(config.AzureStorageConnectionString);
        Assert.Equal("http://127.0.0.1:11434", config.OllamaBaseUrl);
        Assert.Null(config.OllamaModel);
    }

    [Fact]
    public void RequireOllamaModel_ThrowsWhenMissing()
    {
        var config = Configuration.FromLookup(Lookup(new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = "conn",
        }));

        Assert.Throws<InvalidOperationException>(() => config.RequireOllamaModel());
    }

    [Fact]
    public void RequireAzureStorageConnectionString_ThrowsWhenMissing()
    {
        var config = Configuration.FromLookup(Lookup(new Dictionary<string, string?>()));

        Assert.Throws<InvalidOperationException>(() => config.RequireAzureStorageConnectionString());
    }
}

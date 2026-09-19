using MastodonInferencePoc;

// Wire Ctrl+C to cooperative cancellation.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "help";

try
{
    switch (command)
    {
        case "enqueue-test":
            return await RunEnqueueTestAsync(cts.Token).ConfigureAwait(false);

        case "process-once":
            return await RunProcessOnceAsync(cts.Token).ConfigureAwait(false);

        case "peek-result":
            return await RunPeekResultAsync(cts.Token).ConfigureAwait(false);

        case "consume-result":
            return await RunConsumeResultAsync(cts.Token).ConfigureAwait(false);

        case "help":
        case "--help":
        case "-h":
            PrintUsage();
            return 0;

        default:
            await Console.Error.WriteLineAsync($"Unknown command: {command}").ConfigureAwait(false);
            PrintUsage();
            return 1;
    }
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
    return 130;
}

static async Task<int> RunEnqueueTestAsync(CancellationToken cancellationToken)
{
    var config = Configuration.FromEnvironment();
    var connectionString = config.RequireAzureStorageConnectionString();
    var jobsQueue = QueueInferenceAgent.CreateQueueClient(connectionString, config.JobsQueueName);
    var blobStore = new BlobPayloadStore(connectionString, config.BlobContainerName);

    // Ollama isn't needed to enqueue; pass a placeholder client that is never called.
    var agent = new QueueInferenceAgent(jobsQueue, jobsQueue, blobStore, CreateUnusedOllamaClient());

    var jobId = await agent.EnqueueTestJobAsync(cancellationToken).ConfigureAwait(false);
    Console.WriteLine(jobId);
    return 0;
}

static async Task<int> RunProcessOnceAsync(CancellationToken cancellationToken)
{
    var config = Configuration.FromEnvironment();
    var connectionString = config.RequireAzureStorageConnectionString();
    var model = config.RequireOllamaModel();

    var jobsQueue = QueueInferenceAgent.CreateQueueClient(connectionString, config.JobsQueueName);
    var resultsQueue = QueueInferenceAgent.CreateQueueClient(connectionString, config.ResultsQueueName);
    var blobStore = new BlobPayloadStore(connectionString, config.BlobContainerName);

    using var httpClient = CreateOllamaHttpClient(config.OllamaBaseUrl);
    var ollamaClient = new OllamaClient(httpClient, model);

    var agent = new QueueInferenceAgent(jobsQueue, resultsQueue, blobStore, ollamaClient);

    try
    {
        await agent.ProcessOnceAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Deliberately do NOT delete the input message on failure; it will
        // reappear after the visibility timeout so the job can be retried.
        await Console.Error.WriteLineAsync($"process-once failed: {ex.Message}").ConfigureAwait(false);
        return 1;
    }
}

static async Task<int> RunPeekResultAsync(CancellationToken cancellationToken)
{
    var config = Configuration.FromEnvironment();
    var connectionString = config.RequireAzureStorageConnectionString();
    var resultsQueue = QueueInferenceAgent.CreateQueueClient(connectionString, config.ResultsQueueName);
    var blobStore = new BlobPayloadStore(connectionString, config.BlobContainerName);

    var agent = new QueueInferenceAgent(resultsQueue, resultsQueue, blobStore, CreateUnusedOllamaClient());

    await agent.PeekResultAsync(cancellationToken).ConfigureAwait(false);
    return 0;
}

static async Task<int> RunConsumeResultAsync(CancellationToken cancellationToken)
{
    var config = Configuration.FromEnvironment();
    var connectionString = config.RequireAzureStorageConnectionString();
    var resultsQueue = QueueInferenceAgent.CreateQueueClient(connectionString, config.ResultsQueueName);
    var blobStore = new BlobPayloadStore(connectionString, config.BlobContainerName);

    var agent = new QueueInferenceAgent(resultsQueue, resultsQueue, blobStore, CreateUnusedOllamaClient());

    try
    {
        await agent.ConsumeResultAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // On failure the message is left undeleted; it reappears after the
        // visibility timeout so it can be consumed again later.
        await Console.Error.WriteLineAsync($"consume-result failed: {ex.Message}").ConfigureAwait(false);
        return 1;
    }
}

static HttpClient CreateOllamaHttpClient(string baseUrl)
{
    // Ensure a trailing slash so relative "api/generate" resolves correctly.
    var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

    return new HttpClient
    {
        BaseAddress = new Uri(normalized, UriKind.Absolute),
        Timeout = TimeSpan.FromMinutes(3),
    };
}

// Used by commands that don't touch Ollama. The client is constructed but never
// invoked, so no HTTP call is made and TLS settings are left at framework defaults.
static OllamaClient CreateUnusedOllamaClient() =>
    new(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/") }, "unused");

static void PrintUsage()
{
    Console.WriteLine("MastodonInferencePoc - Azure Queue <-> Ollama round trip proof of concept");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/MastodonInferencePoc -- <command>");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  enqueue-test   Upload a test request blob, then enqueue a job ticket. Prints the job id.");
    Console.WriteLine("  process-once   Receive a job ticket, run Ollama, write the result blob + ticket, then delete the job.");
    Console.WriteLine("  peek-result    Peek a result ticket, download the result blob, and print it (non-destructive).");
    Console.WriteLine("  consume-result Receive a result ticket, download and print the result blob, then delete the ticket.");
}

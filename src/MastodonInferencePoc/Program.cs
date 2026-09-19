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
    var jobsQueue = QueueInferenceAgent.CreateQueueClient(
        config.RequireAzureStorageConnectionString(), config.JobsQueueName);

    // Ollama isn't needed to enqueue; pass a placeholder client that is never called.
    var agent = new QueueInferenceAgent(jobsQueue, jobsQueue, CreateUnusedOllamaClient());

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

    using var httpClient = CreateOllamaHttpClient(config.OllamaBaseUrl);
    var ollamaClient = new OllamaClient(httpClient, model);

    var agent = new QueueInferenceAgent(jobsQueue, resultsQueue, ollamaClient);

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
    var resultsQueue = QueueInferenceAgent.CreateQueueClient(
        config.RequireAzureStorageConnectionString(), config.ResultsQueueName);

    var agent = new QueueInferenceAgent(resultsQueue, resultsQueue, CreateUnusedOllamaClient());

    await agent.PeekResultAsync(cancellationToken).ConfigureAwait(false);
    return 0;
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
    Console.WriteLine("  enqueue-test   Send a test job to the jobs queue and print its id.");
    Console.WriteLine("  process-once   Receive one job, run Ollama, write the result, then delete the job.");
    Console.WriteLine("  peek-result    Peek (non-destructively) at one message in the results queue.");
}

# MastodonInferencePoc

A deliberately minimal .NET 10 console app that proves one round trip on macOS:

```
Azure Queue Storage `inference-jobs`
        -> native Ollama on http://127.0.0.1:11434
                -> Azure Queue Storage `inference-results`
```

This is a proof of concept. It intentionally has **no** Docker, Mastodon, Blob
Storage, DI framework, scheduler, database, or web server. Just enough code to
show the queue-to-model-to-queue loop works.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- An Azure Storage account (or the [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) emulator) reachable via a connection string
- [Ollama](https://ollama.com/) running natively with at least one model pulled, e.g. `ollama pull llama3.2`

## Environment variables

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AZURE_STORAGE_CONNECTION_STRING` | Yes (all commands) | _none_ | Connection string for Azure Queue Storage. Never printed. |
| `OLLAMA_BASE_URL` | No | `http://127.0.0.1:11434` | Base URL of the native Ollama server. |
| `OLLAMA_MODEL` | Yes (for `process-once`) | _none_ | Model name passed to Ollama, e.g. `llama3.2`. |
| `INFERENCE_JOBS_QUEUE` | No | `inference-jobs` | Queue that jobs are read from / written to. |
| `INFERENCE_RESULTS_QUEUE` | No | `inference-results` | Queue that results are written to / peeked from. |

Example (macOS / zsh / bash):

```bash
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"
export OLLAMA_BASE_URL="http://127.0.0.1:11434"
export OLLAMA_MODEL="llama3.2"
# INFERENCE_JOBS_QUEUE / INFERENCE_RESULTS_QUEUE can be left unset to use defaults
```

The connection string is read but **never** written to stdout or stderr.

## Build

```bash
# Restore + build the whole solution
dotnet build

# Build just the app
dotnet build src/MastodonInferencePoc/MastodonInferencePoc.csproj
```

## Run

All three modes are subcommands of the same executable. The `--` separates
`dotnet run` arguments from the app's own arguments.

### 1. `enqueue-test`

Sends a test job to the jobs queue and prints the new job id.

```bash
dotnet run --project src/MastodonInferencePoc -- enqueue-test
```

Expected output (the GUID will differ):

```
3f8c1e2a-9b7d-4c6e-8a1f-2d3e4f5a6b7c
```

The message placed on the queue looks like:

```json
{
  "jobId": "3f8c1e2a-9b7d-4c6e-8a1f-2d3e4f5a6b7c",
  "type": "test",
  "prompt": "Reply with exactly: Hello from Ollama via Azure Queue",
  "createdAt": "2026-09-19T12:34:56.789Z"
}
```

### 2. `process-once`

Receives at most one job, calls Ollama, writes a result, then deletes the job.

```bash
dotnet run --project src/MastodonInferencePoc -- process-once
```

Expected output when a job is processed:

```
Processed job 3f8c1e2a-9b7d-4c6e-8a1f-2d3e4f5a6b7c
```

Expected output when the jobs queue is empty (exit code 0):

```
No job available
```

On failure (Ollama unreachable, model missing, result enqueue fails, etc.) it
prints a concise error to **stderr** and exits with a non-zero code. The input
job is left on the queue.

### 3. `peek-result`

Peeks (without consuming) at one message in the results queue and pretty-prints it.

```bash
dotnet run --project src/MastodonInferencePoc -- peek-result
```

Expected output:

```json
{
  "jobId": "3f8c1e2a-9b7d-4c6e-8a1f-2d3e4f5a6b7c",
  "status": "complete",
  "result": "Hello from Ollama via Azure Queue",
  "completedAt": "2026-09-19T12:35:10.123Z"
}
```

When the results queue is empty:

```
No result available
```

## End-to-end walk-through

```bash
dotnet run --project src/MastodonInferencePoc -- enqueue-test   # queues a job
dotnet run --project src/MastodonInferencePoc -- process-once   # job -> Ollama -> result
dotnet run --project src/MastodonInferencePoc -- peek-result    # inspect the result
```

## Azure Queue visibility & delete behaviour

This is the important part of the PoC, so it is spelled out explicitly.

`process-once` follows an **at-least-once, delete-after-success** pattern:

1. **Receive** one message from `inference-jobs`. Azure immediately makes that
   message *invisible* to other readers for a **visibility timeout** of about
   **2 minutes**. The message is not removed from the queue.
2. **Call Ollama** with the job's prompt.
3. **Enqueue the result** onto `inference-results`.
4. **Only then delete** the original job message, using its `MessageId` and
   `PopReceipt`.

Why this order matters:

- If Ollama fails, or writing the result fails, the code **does not delete** the
  input message and exits non-zero. Instead, as a best-effort step, it calls
  `UpdateMessageAsync` with the received `MessageId` and `PopReceipt`, leaving
  the message content unchanged and setting the visibility timeout to
  `TimeSpan.Zero`. That makes the job **immediately visible again**, so the very
  next `process-once` run can retry it without waiting out the ~2-minute
  timeout. No work is silently lost.
- If resetting the visibility also fails (for example, the pop receipt is
  already stale), that secondary failure is logged to stderr but the original
  processing exception is preserved and rethrown, and the process still exits
  non-zero. The message then simply reappears after the normal visibility
  timeout instead of immediately.
- If the process crashes outright between receiving and deleting, no reset runs,
  so the message reappears after the timeout.
- The delete (and the visibility reset) require the `PopReceipt` returned by the
  receive call. If the visibility timeout had already expired and another reader
  picked the message up, the original `PopReceipt` would be stale and the call
  would fail, which is the queue protecting you from double-processing.

The trade-off is that a job could be processed more than once (for example, if
inference takes longer than the visibility timeout, or the result is enqueued
but the delete fails). That is an acceptable at-least-once guarantee for this
proof of concept; a production system would add idempotency keys or a longer /
renewed visibility timeout.

Messages use Base64 message encoding (`QueueMessageEncoding.Base64`) so payloads
round-trip consistently.

## Project layout

```
src/MastodonInferencePoc/
  Program.cs                     # command dispatch (enqueue-test / process-once / peek-result)
  Configuration.cs               # environment-variable configuration + defaults
  JsonDefaults.cs                # shared System.Text.Json options
  OllamaClient.cs                # HttpClient wrapper for POST /api/generate
  QueueInferenceAgent.cs         # the queue <-> Ollama orchestration
  Models/
    InferenceJob.cs
    InferenceResult.cs
    OllamaGenerateRequest.cs
    OllamaGenerateResponse.cs
tests/MastodonInferencePoc.Tests/
  ...                            # serialization + configuration unit tests
```

## Tests

The unit tests cover JSON serialization of jobs/results, Ollama response
deserialization, and configuration defaults. They never contact real Azure or
Ollama.

```bash
dotnet test
```

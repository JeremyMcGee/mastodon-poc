# MastodonInferencePoc

A deliberately minimal .NET 10 console app that proves one round trip on macOS,
with large payloads kept in Blob Storage and only small tickets on the queues:

```
Blob: requests/{jobId}.json  <-- the prompt lives here
        -> Azure Queue `inference-jobs`   (small ticket referencing the blob)
                -> native Ollama on http://127.0.0.1:11434
                        -> Blob: results/{jobId}.json  <-- the output lives here
                                -> Azure Queue `inference-results` (small ticket)
```

This is a proof of concept. It intentionally has **no** Mastodon, scheduler,
Docker, database, DI framework, or web server. Just enough code to show the
blob-backed queue-to-model-to-queue loop works.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- An Azure Storage account (or the [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) emulator) reachable via a connection string. The same connection string is used for both Queue Storage and Blob Storage.
- [Ollama](https://ollama.com/) running natively with at least one model pulled, e.g. `ollama pull llama3.2`

## Environment variables

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AZURE_STORAGE_CONNECTION_STRING` | Yes (all commands) | _none_ | Connection string for Azure Queue **and** Blob Storage. Never printed. |
| `OLLAMA_BASE_URL` | No | `http://127.0.0.1:11434` | Base URL of the native Ollama server. |
| `OLLAMA_MODEL` | Yes (for `process-once`) | _none_ | Model name passed to Ollama, e.g. `llama3.2`. |
| `INFERENCE_JOBS_QUEUE` | No | `inference-jobs` | Queue that job tickets are read from / written to. |
| `INFERENCE_RESULTS_QUEUE` | No | `inference-results` | Queue that result tickets are written to / read from. |
| `INFERENCE_BLOB_CONTAINER` | No | `inference-data` | Private blob container holding request and result payloads. |

Example (macOS / zsh / bash):

```bash
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"
export OLLAMA_BASE_URL="http://127.0.0.1:11434"
export OLLAMA_MODEL="llama3.2"
# INFERENCE_JOBS_QUEUE / INFERENCE_RESULTS_QUEUE / INFERENCE_BLOB_CONTAINER
# can be left unset to use defaults
```

The connection string is read but **never** written to stdout or stderr.

## Payloads vs tickets

The queues only ever carry small **tickets**; the full payloads live in blobs.

Request blob at `requests/{jobId}.json` (`InferenceRequest`):

```json
{
  "jobId": "3f8c1e2a-...",
  "type": "test",
  "prompt": "Reply with exactly: Hello from Ollama via Azure Queue",
  "createdAt": "2026-09-19T12:34:56.789Z"
}
```

Job ticket on `inference-jobs` (`JobTicket`) — note there is **no** prompt:

```json
{
  "jobId": "3f8c1e2a-...",
  "type": "test",
  "requestBlob": "requests/3f8c1e2a-....json",
  "createdAt": "2026-09-19T12:34:56.789Z"
}
```

`JobTicket` may optionally carry a `"deadline"`. If present and in the past when
`process-once` receives it, the ticket is treated as expired and is not processed.

Result blob at `results/{jobId}.json` (`InferenceResult`):

```json
{
  "jobId": "3f8c1e2a-...",
  "status": "complete",
  "result": "Hello from Ollama via Azure Queue",
  "completedAt": "2026-09-19T12:35:10.123Z"
}
```

Result ticket on `inference-results` (`ResultTicket`) — note there is **no** result text:

```json
{
  "jobId": "3f8c1e2a-...",
  "status": "complete",
  "resultBlob": "results/3f8c1e2a-....json",
  "completedAt": "2026-09-19T12:35:10.123Z"
}
```

## Build

```bash
# Restore + build the whole solution
dotnet build

# Build just the app
dotnet build src/MastodonInferencePoc/MastodonInferencePoc.csproj
```

## Run

All four modes are subcommands of the same executable. The `--` separates
`dotnet run` arguments from the app's own arguments.

### 1. `enqueue-test`

Uploads a test request blob, then enqueues a job ticket that references it, and
prints the new job id.

```bash
dotnet run --project src/MastodonInferencePoc -- enqueue-test
```

Expected output (the GUID will differ):

```
3f8c1e2a-9b7d-4c6e-8a1f-2d3e4f5a6b7c
```

The request blob (`requests/{jobId}.json`) is uploaded **first**; only after the
upload succeeds is the job ticket enqueued, so the queue never references a blob
that does not yet exist.

### 2. `process-once`

Receives one job ticket, downloads the referenced request blob, calls Ollama,
uploads a result blob, enqueues a result ticket, then deletes the job ticket.

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

On failure (Ollama unreachable, model missing, request blob missing/malformed,
jobId mismatch, expired ticket, result-blob upload fails, or result-ticket
enqueue fails) it prints a concise error to **stderr** and exits non-zero. The
input job ticket is left on the queue and made immediately visible again.

### 3. `peek-result`

Peeks (without consuming) at one result ticket, downloads the referenced result
blob, and pretty-prints the actual result. The queue message is left untouched.

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

### 4. `consume-result`

Like `peek-result`, but **destructive** for the queue ticket: it receives one
result ticket, downloads and deserializes the referenced result blob,
pretty-prints it, and then deletes the ticket. The result **blob is retained**
for diagnostics — only the queue message is removed.

```bash
dotnet run --project src/MastodonInferencePoc -- consume-result
```

Output matches `peek-result` when a result is present, and prints
`No result available` (exit code 0) when the queue is empty. The ticket is only
deleted **after** the blob has been successfully downloaded, validated, and
printed. If any step fails, the ticket is left on the queue (not deleted), a
concise error is written to stderr, and the command exits non-zero; the ticket
becomes visible again after its visibility timeout.

## End-to-end walk-through

```bash
dotnet run --project src/MastodonInferencePoc -- enqueue-test    # upload request blob + enqueue job ticket
dotnet run --project src/MastodonInferencePoc -- process-once    # ticket -> request blob -> Ollama -> result blob -> result ticket
dotnet run --project src/MastodonInferencePoc -- peek-result     # download + inspect the result (non-destructive)
dotnet run --project src/MastodonInferencePoc -- consume-result  # download + print the result, then delete the ticket
```

## Correctness guarantees

The ordering is deliberate so nothing is ever referenced before it exists and
nothing is deleted before its successor exists:

- **A queue message is never created before its referenced blob exists.**
  `enqueue-test` uploads `requests/{jobId}.json` before sending the job ticket;
  `process-once` uploads `results/{jobId}.json` before sending the result ticket.
- **An input job is never deleted before both its result blob and result ticket
  exist.** `process-once` deletes the job ticket only after step 8 (result blob
  upload) and step 9 (result ticket enqueue) have both succeeded.
- **Malformed or missing blobs do not silently lose input messages.** If a
  request blob is missing, malformed, or fails validation, `process-once`
  throws, does **not** delete the job ticket, and makes it visible again. The
  same applies to `consume-result` and result blobs.
- **jobIds are validated to match.** The jobId in a `JobTicket` must equal the
  `jobId` inside the downloaded request blob, and the jobId in a `ResultTicket`
  must equal the `jobId` inside the downloaded result blob. A mismatch is
  rejected without deleting the queue message.
- **No secrets are logged.** The connection string is never printed; error
  messages reference blob paths and ids only.

## Azure Queue visibility & delete behaviour

`process-once` follows an **at-least-once, delete-after-success** pattern:

1. **Receive** one job ticket from `inference-jobs`. Azure immediately makes that
   message *invisible* for a **visibility timeout** of about **2 minutes**. The
   message is not removed from the queue.
2. **Download** the referenced request blob and validate it.
3. **Call Ollama** with the request's prompt.
4. **Upload** the result blob (`results/{jobId}.json`).
5. **Enqueue** the small result ticket onto `inference-results`.
6. **Only then delete** the original job ticket, using its `MessageId` and
   `PopReceipt`.

Why this order matters:

- If any step fails, the code **does not delete** the input ticket and exits
  non-zero. As a best-effort step it calls `UpdateMessageAsync` with the received
  `MessageId` and `PopReceipt`, leaving the content unchanged and setting the
  visibility timeout to `TimeSpan.Zero`, so the ticket becomes **immediately
  visible again** for retry rather than waiting out the ~2-minute timeout.
- If resetting the visibility also fails (for example, a stale pop receipt), that
  secondary failure is logged to stderr but the original processing exception is
  preserved and rethrown, and the process still exits non-zero. The ticket then
  reappears after the normal visibility timeout.
- If the process crashes outright between receiving and deleting, no reset runs,
  so the ticket reappears after the timeout.

The trade-off is at-least-once processing: a job could be processed more than
once (e.g. if inference outlives the visibility timeout, or the delete fails
after the result was written). Because result blobs are uploaded with
`overwrite: true` and keyed by jobId, reprocessing simply replaces the result
blob. A production system would add idempotency keys or a renewed visibility
timeout. Result blobs are intentionally **not** deleted — retaining them is
useful for diagnostics.

Queue messages use Base64 message encoding (`QueueMessageEncoding.Base64`) so
tickets round-trip consistently. The blob container is created with
`PublicAccessType.None`, i.e. it remains **private**.

## Project layout

```
src/MastodonInferencePoc/
  Program.cs                     # command dispatch (enqueue-test / process-once / peek-result / consume-result)
  Configuration.cs               # environment-variable configuration + defaults
  JsonDefaults.cs                # shared System.Text.Json options
  OllamaClient.cs                # HttpClient wrapper for POST /api/generate
  BlobPayloadStore.cs            # private blob container; request/result payload upload + download
  QueueInferenceAgent.cs         # the blob <-> queue <-> Ollama orchestration
  Models/
    InferenceRequest.cs          # request payload (blob)
    InferenceResult.cs           # result payload (blob)
    JobTicket.cs                 # small jobs-queue message referencing the request blob
    ResultTicket.cs              # small results-queue message referencing the result blob
    OllamaGenerateRequest.cs
    OllamaGenerateResponse.cs
tests/MastodonInferencePoc.Tests/
  SerializationTests.cs          # payload + ticket JSON shape/round-trip
  ProcessOnceTests.cs            # blob-backed processing, validation, delete/visibility semantics
  ConsumeResultTests.cs          # peek vs consume, missing/malformed blob, jobId mismatch
  ConfigurationTests.cs          # env-var defaults + guards
  Fakes/                         # in-memory QueueClient, BlobPayloadStore, HTTP handler
```

## Tests

The unit tests cover payload and ticket serialization, blob-backed processing
and validation (jobId matching, expiry, missing/malformed blobs), the
delete/visibility semantics, and configuration defaults. They use in-memory
fakes for the queue client, the blob store, and Ollama's HTTP endpoint, so they
never contact real Azure or Ollama.

```bash
dotnet test
```

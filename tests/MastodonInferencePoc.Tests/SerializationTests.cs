using System.Text.Json;
using MastodonInferencePoc;
using MastodonInferencePoc.Models;

namespace MastodonInferencePoc.Tests;

public class SerializationTests
{
    [Fact]
    public void InferenceJob_SerializesWithExpectedPropertyNames()
    {
        var job = new InferenceJob
        {
            JobId = "abc-123",
            Type = "test",
            Prompt = "Reply with exactly: Hello from Ollama via Azure Queue",
            CreatedAt = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(job, JsonDefaults.Options);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("abc-123", root.GetProperty("jobId").GetString());
        Assert.Equal("test", root.GetProperty("type").GetString());
        Assert.Equal(
            "Reply with exactly: Hello from Ollama via Azure Queue",
            root.GetProperty("prompt").GetString());
        Assert.True(root.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public void InferenceJob_RoundTrips()
    {
        var original = new InferenceJob
        {
            JobId = Guid.NewGuid().ToString(),
            Type = "test",
            Prompt = "hello",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(original, JsonDefaults.Options);
        var restored = JsonSerializer.Deserialize<InferenceJob>(json, JsonDefaults.Options);

        Assert.NotNull(restored);
        Assert.Equal(original.JobId, restored!.JobId);
        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.Prompt, restored.Prompt);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
    }

    [Fact]
    public void InferenceResult_SerializesWithExpectedPropertyNames()
    {
        var result = new InferenceResult
        {
            JobId = "abc-123",
            Status = "complete",
            Result = "Hello from Ollama via Azure Queue",
            CompletedAt = new DateTimeOffset(2026, 9, 19, 12, 5, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("abc-123", root.GetProperty("jobId").GetString());
        Assert.Equal("complete", root.GetProperty("status").GetString());
        Assert.Equal("Hello from Ollama via Azure Queue", root.GetProperty("result").GetString());
        Assert.True(root.TryGetProperty("completedAt", out _));
    }

    [Fact]
    public void InferenceResult_RoundTrips()
    {
        var original = new InferenceResult
        {
            JobId = "job-1",
            Status = "complete",
            Result = "some text",
            CompletedAt = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(original, JsonDefaults.Options);
        var restored = JsonSerializer.Deserialize<InferenceResult>(json, JsonDefaults.Options);

        Assert.NotNull(restored);
        Assert.Equal(original.JobId, restored!.JobId);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.Result, restored.Result);
        Assert.Equal(original.CompletedAt, restored.CompletedAt);
    }

    [Fact]
    public void OllamaGenerateResponse_DeserializesResponseProperty()
    {
        // Shape mirrors a real non-streaming Ollama /api/generate reply.
        const string json = """
        {
          "model": "llama3.2",
          "created_at": "2026-09-19T12:05:00.000Z",
          "response": "Hello from Ollama via Azure Queue",
          "done": true,
          "total_duration": 123456789
        }
        """;

        var parsed = JsonSerializer.Deserialize<OllamaGenerateResponse>(json, JsonDefaults.Options);

        Assert.NotNull(parsed);
        Assert.Equal("llama3.2", parsed!.Model);
        Assert.Equal("Hello from Ollama via Azure Queue", parsed.Response);
        Assert.True(parsed.Done);
    }

    [Fact]
    public void OllamaGenerateRequest_SerializesStreamFalse()
    {
        var request = new OllamaGenerateRequest
        {
            Model = "llama3.2",
            Prompt = "hi",
            Stream = false,
        };

        var json = JsonSerializer.Serialize(request, JsonDefaults.Options);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("llama3.2", root.GetProperty("model").GetString());
        Assert.Equal("hi", root.GetProperty("prompt").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
    }
}

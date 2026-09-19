using System.Text.Json;

namespace MastodonInferencePoc;

/// <summary>
/// Shared System.Text.Json options so every queue message and Ollama payload
/// is (de)serialized consistently.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Property names are pinned via [JsonPropertyName]; ignore casing on the way in.
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions PrettyOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

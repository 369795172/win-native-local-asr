using System.Text.Json;

namespace WinLocalASR.Core.Inference;

/// <summary>
/// Minimal reader for the resolved llama-server artifact paths. Takes an explicit file path so
/// tests can feed it temp JSON. Forward-compatible with the Task 5 versions.json manifest by
/// construction: exactly the three keys below are read and every other key (pins, SHAs,
/// download URLs) is ignored, so a richer manifest can keep these top-level resolved-path keys.
///
///   { "llamaServerPath": "...\\llama-server.exe",
///     "mainModelPath":   "...\\Qwen3-ASR-1.7B-Q8_0.gguf",
///     "mmprojModelPath": "...\\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf" }
/// </summary>
public sealed record LlamaServerPaths(
    string LlamaServerExePath,
    string MainModelPath,
    string MmprojModelPath)
{
    public static LlamaServerPaths Load(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
        {
            throw new FileNotFoundException($"Versions manifest not found: '{jsonFilePath}'.", jsonFilePath);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonFilePath));
        JsonElement root = document.RootElement;
        return new LlamaServerPaths(
            RequiredString(root, "llamaServerPath", jsonFilePath),
            RequiredString(root, "mainModelPath", jsonFilePath),
            RequiredString(root, "mmprojModelPath", jsonFilePath));
    }

    private static string RequiredString(JsonElement root, string key, string sourcePath)
    {
        if (root.TryGetProperty(key, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            return element.GetString()!;
        }

        throw new InvalidDataException($"Versions manifest '{sourcePath}' is missing required key '{key}'.");
    }
}

using System.IO;
using System.Text.Json;

namespace WinLocalASR.Core.Versions;

/// <summary>
/// llama.cpp Windows binary pin. <c>sha256Policy</c>: <c>"official"</c> when the
/// hash comes from an authoritative source (release checksum asset or the
/// GitHub-computed release-asset digest), <c>"tofu"</c> (trust-on-first-use,
/// sha256 null until first download computes and freezes it) otherwise.
/// </summary>
public sealed record LlamaCppPin(string Tag, string AssetName, string? Sha256, string Sha256Policy);

public sealed record ModelPin(string Repo, string File, string Revision, string Sha256, long SizeBytes);

public sealed record ModelPins(ModelPin Main, ModelPin Mmproj);

/// <summary>Base URLs for downloads; ghProxyPrefix is used as <c>{prefix}{original-url}</c>.</summary>
public sealed record MirrorUrls(string HuggingFace, string HfMirror, string Github, string GhProxyPrefix);

/// <summary>
/// Strongly-typed reader for the versions.json manifest (repo-root template;
/// the setup flow writes an installed copy with the three resolved-path keys
/// <c>llamaServerPath</c>/<c>mainModelPath</c>/<c>mmprojModelPath</c> added —
/// consumed by <see cref="WinLocalASR.Core.Inference.LlamaServerPaths"/>, ignored here).
/// Unknown keys are ignored for forward compatibility.
/// </summary>
public sealed record VersionManifest(LlamaCppPin LlamaCpp, ModelPins Models, MirrorUrls Urls)
{
    public static VersionManifest Load(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
        {
            throw new FileNotFoundException($"Versions manifest not found: '{jsonFilePath}'.", jsonFilePath);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonFilePath));
        JsonElement root = document.RootElement;

        JsonElement llamaCpp = RequiredObject(root, "llamaCpp", jsonFilePath);
        JsonElement models = RequiredObject(root, "models", jsonFilePath);
        JsonElement urls = RequiredObject(root, "urls", jsonFilePath);

        return new VersionManifest(
            new LlamaCppPin(
                RequiredString(llamaCpp, "tag", jsonFilePath),
                RequiredString(llamaCpp, "assetName", jsonFilePath),
                OptionalString(llamaCpp, "sha256"),
                RequiredString(llamaCpp, "sha256Policy", jsonFilePath)),
            new ModelPins(
                ReadModel(models, "main", jsonFilePath),
                ReadModel(models, "mmproj", jsonFilePath)),
            new MirrorUrls(
                RequiredString(urls, "huggingface", jsonFilePath),
                RequiredString(urls, "hfMirror", jsonFilePath),
                RequiredString(urls, "github", jsonFilePath),
                RequiredString(urls, "ghProxyPrefix", jsonFilePath)));
    }

    private static ModelPin ReadModel(JsonElement models, string name, string sourcePath)
    {
        JsonElement element = RequiredObject(models, name, sourcePath);
        return new ModelPin(
            RequiredString(element, "repo", sourcePath),
            RequiredString(element, "file", sourcePath),
            RequiredString(element, "revision", sourcePath),
            RequiredString(element, "sha256", sourcePath),
            RequiredLong(element, "sizeBytes", sourcePath));
    }

    private static JsonElement RequiredObject(JsonElement parent, string key, string sourcePath)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(key, out JsonElement element)
            && element.ValueKind == JsonValueKind.Object)
        {
            return element;
        }

        throw new InvalidDataException($"Versions manifest '{sourcePath}' is missing required object '{key}'.");
    }

    private static string RequiredString(JsonElement parent, string key, string sourcePath)
    {
        if (parent.TryGetProperty(key, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            return element.GetString()!;
        }

        throw new InvalidDataException($"Versions manifest '{sourcePath}' is missing required key '{key}'.");
    }

    private static long RequiredLong(JsonElement parent, string key, string sourcePath)
    {
        if (parent.TryGetProperty(key, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out long value))
        {
            return value;
        }

        throw new InvalidDataException($"Versions manifest '{sourcePath}' is missing required numeric key '{key}'.");
    }

    private static string? OptionalString(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

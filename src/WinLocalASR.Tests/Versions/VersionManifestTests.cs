using System.Text.Json.Nodes;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Versions;
using Xunit;

namespace WinLocalASR.Tests.Versions;

public class VersionManifestTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (string path in _tempFiles)
        {
            File.Delete(path);
        }
    }

    private static string RepoTemplatePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "versions.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("repo-root versions.json not found above the test bin directory");
    }

    private string WriteNode(JsonNode node)
    {
        string path = Path.Combine(Path.GetTempPath(), $"winlocalasr-versions-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, node.ToJsonString());
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Repo_template_loads_with_the_pinned_values()
    {
        VersionManifest manifest = VersionManifest.Load(RepoTemplatePath());

        Assert.Equal("b10964", manifest.LlamaCpp.Tag);
        Assert.Equal("llama-b10964-bin-win-cpu-x64.zip", manifest.LlamaCpp.AssetName);
        Assert.Equal("917f39c076402c421224824607397af20f53625a60defc20e8dd22446bf4c5d7", manifest.LlamaCpp.Sha256);
        Assert.Equal("official", manifest.LlamaCpp.Sha256Policy);

        Assert.Equal("ggml-org/Qwen3-ASR-1.7B-GGUF", manifest.Models.Main.Repo);
        Assert.Equal("Qwen3-ASR-1.7B-Q8_0.gguf", manifest.Models.Main.File);
        Assert.Equal("58e22d0532d4eacaf034cfac17a6fed159f37c41390c710186783be439d1fc57", manifest.Models.Main.Sha256);
        Assert.Equal(2_165_034_944L, manifest.Models.Main.SizeBytes);

        Assert.Equal("ggml-org/Qwen3-ASR-1.7B-GGUF", manifest.Models.Mmproj.Repo);
        Assert.Equal("mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", manifest.Models.Mmproj.File);
        Assert.Equal("46c1d533af3f354ceb37ce855dbceff7da7fa7cf1e6a523df3b13440bd164c0d", manifest.Models.Mmproj.Sha256);
        Assert.Equal(355_709_344L, manifest.Models.Mmproj.SizeBytes);

        Assert.Equal("https://huggingface.co", manifest.Urls.HuggingFace);
        Assert.Equal("https://hf-mirror.com", manifest.Urls.HfMirror);
        Assert.Equal("https://github.com", manifest.Urls.Github);
        Assert.Equal("https://ghproxy.cn/", manifest.Urls.GhProxyPrefix);
    }

    /// <summary>
    /// The installed manifest (repo template + the three resolved-path keys the
    /// setup flow writes) must load through BOTH readers: the pins here and the
    /// engine paths via LlamaServerPaths.
    /// </summary>
    [Fact]
    public void Template_plus_resolved_paths_loads_through_both_readers()
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(RepoTemplatePath()))!;
        node["llamaServerPath"] = @"C:\app\bin\llama-server.exe";
        node["mainModelPath"] = @"C:\app\models\Qwen3-ASR-1.7B-Q8_0.gguf";
        node["mmprojModelPath"] = @"C:\app\models\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf";
        string installed = WriteNode(node);

        VersionManifest manifest = VersionManifest.Load(installed);
        LlamaServerPaths paths = LlamaServerPaths.Load(installed);

        Assert.Equal("b10964", manifest.LlamaCpp.Tag);
        Assert.Equal(@"C:\app\bin\llama-server.exe", paths.LlamaServerExePath);
        Assert.Equal(@"C:\app\models\Qwen3-ASR-1.7B-Q8_0.gguf", paths.MainModelPath);
        Assert.Equal(@"C:\app\models\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", paths.MmprojModelPath);
    }

    /// <summary>
    /// The repo template deliberately omits the three resolved-path keys (setup
    /// adds them to the installed copy). The engine path reader requires them,
    /// which is exactly the installed-copy contract.
    /// </summary>
    [Fact]
    public void Raw_template_rejects_engine_path_reader_until_setup_fills_them()
    {
        Assert.Throws<InvalidDataException>(() => LlamaServerPaths.Load(RepoTemplatePath()));
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(RepoTemplatePath()))!;
        node["someFutureFeature"] = new JsonObject { ["nested"] = 1 };
        node["llamaCpp"]!["extra"] = "ignored";
        node["models"]!["main"]!["note"] = "ignored";
        string path = WriteNode(node);

        VersionManifest manifest = VersionManifest.Load(path);

        Assert.Equal("b10964", manifest.LlamaCpp.Tag); // everything still readable
        Assert.Equal(2_165_034_944L, manifest.Models.Main.SizeBytes);
    }

    [Fact]
    public void Tofu_pin_with_null_sha256_is_representable()
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(RepoTemplatePath()))!;
        node["llamaCpp"]!["sha256"] = null;
        node["llamaCpp"]!["sha256Policy"] = "tofu";
        string path = WriteNode(node);

        VersionManifest manifest = VersionManifest.Load(path);

        Assert.Null(manifest.LlamaCpp.Sha256);
        Assert.Equal("tofu", manifest.LlamaCpp.Sha256Policy);
    }

    [Fact]
    public void Missing_required_key_throws_invalid_data()
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(RepoTemplatePath()))!;
        ((JsonObject)node["llamaCpp"]!).Remove("tag");
        string path = WriteNode(node);

        Assert.Throws<InvalidDataException>(() => VersionManifest.Load(path));
    }

    [Fact]
    public void Missing_file_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(
            () => VersionManifest.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));
    }
}

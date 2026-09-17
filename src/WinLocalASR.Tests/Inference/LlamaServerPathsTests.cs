using WinLocalASR.Core.Inference;
using Xunit;

namespace WinLocalASR.Tests.Inference;

public class LlamaServerPathsTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string WriteJson(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"winlocalasr-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in _tempFiles)
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_reads_the_three_resolved_paths()
    {
        string path = WriteJson("""
            {
              "llamaServerPath": "C:\\app\\bin\\llama-server.exe",
              "mainModelPath": "C:\\app\\models\\Qwen3-ASR-1.7B-Q8_0.gguf",
              "mmprojModelPath": "C:\\app\\models\\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf"
            }
            """);

        LlamaServerPaths paths = LlamaServerPaths.Load(path);

        Assert.Equal("C:\\app\\bin\\llama-server.exe", paths.LlamaServerExePath);
        Assert.Equal("C:\\app\\models\\Qwen3-ASR-1.7B-Q8_0.gguf", paths.MainModelPath);
        Assert.Equal("C:\\app\\models\\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", paths.MmprojModelPath);
    }

    [Fact]
    public void Load_throws_FileNotFoundException_for_missing_manifest()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"winlocalasr-absent-{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => LlamaServerPaths.Load(missing));
    }

    [Fact]
    public void Load_throws_InvalidData_naming_the_missing_key()
    {
        string path = WriteJson("""
            { "llamaServerPath": "C:\\app\\bin\\llama-server.exe" }
            """);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => LlamaServerPaths.Load(path));
        Assert.Contains("mainModelPath", ex.Message);
    }

    [Fact]
    public void Load_ignores_unknown_keys_for_forward_compatibility_with_task5_manifest()
    {
        string path = WriteJson("""
            {
              "llamaServerPath": "C:\\app\\bin\\llama-server.exe",
              "mainModelPath": "C:\\app\\models\\main.gguf",
              "mmprojModelPath": "C:\\app\\models\\mmproj.gguf",
              "llamaCppTag": "b10964",
              "mainModelSha256": "58e22d0532d4eacaf034cfac17a6fed159f37c41390c710186783be439d1fc57",
              "mirrors": ["https://hf-mirror.com"]
            }
            """);

        LlamaServerPaths paths = LlamaServerPaths.Load(path);
        Assert.Equal("C:\\app\\models\\main.gguf", paths.MainModelPath);
    }
}

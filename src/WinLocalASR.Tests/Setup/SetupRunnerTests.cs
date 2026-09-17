using System.Threading;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Setup;
using WinLocalASR.Core.Versions;
using Xunit;

namespace WinLocalASR.Tests.Setup;

/// <summary>QA+ / QA- / resume / failover evidence for the six-step installer.
/// All downloads are served by local fakes (HttpListener file server or in-process
/// stub handler); the real 2.5 GB models and the real llama.cpp zip are NEVER fetched
/// here — that is the user-machine first run / Task 13 CI smoke's job.</summary>
public class SetupRunnerTests
{
    private const int MainSize = 100_000;
    private const int MmprojSize = 50_000;

    /// <summary>Fresh-machine simulation harness: fake file server + temp install and
    /// appdata bases + a manifest whose pins match the fake artifacts byte-for-byte.</summary>
    private sealed class Harness : IDisposable
    {
        public readonly byte[] Zip = SetupTestAssets.MakeFakeLlamaZip();
        public readonly byte[] Main = SetupTestAssets.FakeBytes(MainSize, 11);
        public readonly byte[] Mmproj = SetupTestAssets.FakeBytes(MmprojSize, 29);

        public readonly string InstallBase = SetupTestAssets.NewTempDir();
        public readonly string AppDataBase = SetupTestAssets.NewTempDir();
        public readonly FakeFileServer Server;
        public readonly string ManifestPath;

        private readonly object _gate = new();
        private List<SetupProgress> _progress = new();
        private int _completed;

        public Harness()
        {
            Server = FakeFileServer.Start(new Dictionary<string, byte[]>
            {
                [$"/gh/ggml-org/llama.cpp/releases/download/{SetupTestAssets.Tag}/{SetupTestAssets.AssetName}"] = Zip,
                [$"/hf/{SetupTestAssets.Repo}/resolve/main/{SetupTestAssets.MainModelFile}"] = Main,
                [$"/hf/{SetupTestAssets.Repo}/resolve/main/{SetupTestAssets.MmprojModelFile}"] = Mmproj,
            });

            ManifestPath = SetupTestAssets.WriteManifest(
                github: $"{Server.BaseUrl}/gh",
                ghProxyPrefix: $"{Server.BaseUrl}/proxy/",
                huggingFace: $"{Server.BaseUrl}/hf",
                hfMirror: $"{Server.BaseUrl}/hfm",
                llamaSha256: SetupTestAssets.Sha256(Zip),
                mainSha256: SetupTestAssets.Sha256(Main),
                mmprojSha256: SetupTestAssets.Sha256(Mmproj),
                mainSizeBytes: MainSize,
                mmprojSizeBytes: MmprojSize,
                directory: InstallBase);
        }

        public string InstallRoot => Path.Combine(InstallBase, SetupRunner.InstallDirName);

        public string ModelsDir => Path.Combine(InstallRoot, SetupRunner.ModelsDirName);

        public string BinDir => Path.Combine(InstallRoot, SetupRunner.BinDirName);

        public int Completed
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        public List<SetupProgress> ProgressSnapshot()
        {
            lock (_gate)
            {
                return _progress.ToList();
            }
        }

        public SetupRunner NewRunner()
            => new(ManifestPath, new SetupRunnerOptions
            {
                InstallBaseDirectory = InstallBase,
                Settings = new SettingsStore(AppDataBase),
                MirrorRetryDelay = TimeSpan.Zero,
                Completion = () => { lock (_gate) _completed++; return Task.CompletedTask; },
            });

        public Task RunAsync(SetupRunner runner, List<string>? logSink = null)
            => runner.RunAsync(p =>
            {
                lock (_gate)
                {
                    _progress.Add(p);
                }

                if (logSink is not null && p.FilePercent is null)
                {
                    lock (logSink)
                    {
                        logSink.Add(p.Message);
                    }
                }
            });

        public void Dispose()
        {
            Server.Dispose();
            SetupTestAssets.DeleteDir(InstallBase);
            SetupTestAssets.DeleteDir(AppDataBase);
        }
    }

    // ---- URL construction: exact pinned forms against the real repo manifest ----

    [Fact]
    public void Url_builders_match_the_pinned_manifest_forms()
    {
        VersionManifest manifest = VersionManifest.Load(SetupTestAssets.RepoTemplatePath());

        const string llamaPrimary =
            "https://github.com/ggml-org/llama.cpp/releases/download/b10964/llama-b10964-bin-win-cpu-x64.zip";

        Assert.Equal(llamaPrimary, SetupRunner.LlamaCppDownloadUrl(manifest));
        Assert.Equal("https://ghproxy.cn/" + llamaPrimary, SetupRunner.LlamaCppProxyDownloadUrl(manifest));

        Assert.Equal(
            "https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF/resolve/main/Qwen3-ASR-1.7B-Q8_0.gguf",
            SetupRunner.ModelDownloadUrl(manifest.Models.Main, manifest.Urls.HuggingFace));
        Assert.Equal(
            "https://hf-mirror.com/ggml-org/Qwen3-ASR-1.7B-GGUF/resolve/main/Qwen3-ASR-1.7B-Q8_0.gguf",
            SetupRunner.ModelMirrorDownloadUrl(manifest.Models.Main, manifest.Urls));
        Assert.Equal(
            "https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF/resolve/main/mmproj-Qwen3-ASR-1.7B-Q8_0.gguf",
            SetupRunner.ModelDownloadUrl(manifest.Models.Mmproj, manifest.Urls.HuggingFace));
        Assert.Equal(
            "https://hf-mirror.com/ggml-org/Qwen3-ASR-1.7B-GGUF/resolve/main/mmproj-Qwen3-ASR-1.7B-Q8_0.gguf",
            SetupRunner.ModelMirrorDownloadUrl(manifest.Models.Mmproj, manifest.Urls));
    }

    // ---- QA+: fresh-machine simulation, all six steps green ----

    [Fact]
    public async Task Fresh_machine_simulation_completes_all_six_steps()
    {
        using var harness = new Harness();
        SetupRunner runner = harness.NewRunner();
        await harness.RunAsync(runner);

        // Step 1: directories.
        Assert.True(Directory.Exists(harness.BinDir), "bin/ directory missing");
        Assert.True(Directory.Exists(harness.ModelsDir), "models/ directory missing");

        // Steps 2+4: whole zip extracted (exe + dispatch DLLs + OpenMP), zip retained.
        Assert.True(File.Exists(runner.LlamaServerExePath), "llama-server.exe not extracted");
        foreach (string dll in new[]
                 {
                     "llama-server-impl.dll", "llama.dll", "llama-common.dll", "mtmd.dll",
                     "ggml.dll", "ggml-base.dll", "ggml-cpu-haswell.dll", "ggml-cpu-sse42.dll",
                     "ggml-cpu-zen4.dll", "ggml-rpc.dll", "libomp.dll",
                 })
        {
            Assert.True(File.Exists(Path.Combine(harness.BinDir, dll)), $"{dll} not extracted");
        }

        Assert.True(File.Exists(runner.LlamaZipPath), "release zip should be retained as the idempotence anchor");

        // Step 3: models byte-exact (implies correct SHA-256s).
        Assert.Equal(harness.Main, await File.ReadAllBytesAsync(runner.MainModelPath));
        Assert.Equal(harness.Mmproj, await File.ReadAllBytesAsync(runner.MmprojModelPath));

        // Step 5: installed manifest carries manifest fields AND the three resolved-path
        // keys, loadable through BOTH readers (Task 4 compat is load-bearing).
        Assert.True(File.Exists(runner.InstalledVersionsPath));
        VersionManifest installed = VersionManifest.Load(runner.InstalledVersionsPath);
        Assert.Equal(SetupTestAssets.Tag, installed.LlamaCpp.Tag);
        Assert.Equal(SetupTestAssets.Sha256(harness.Main), installed.Models.Main.Sha256);
        LlamaServerPaths paths = LlamaServerPaths.Load(runner.InstalledVersionsPath);
        Assert.Equal(runner.LlamaServerExePath, paths.LlamaServerExePath);
        Assert.Equal(runner.MainModelPath, paths.MainModelPath);
        Assert.Equal(runner.MmprojModelPath, paths.MmprojModelPath);
        Assert.Equal(MainSize, installed.Models.Main.SizeBytes);

        // Configured=true via SettingsStore.
        Assert.True(new SettingsStore(harness.AppDataBase).Load().Configured);

        // Step 6: completion invoked exactly once.
        Assert.Equal(1, harness.Completed);

        // Progress: all six steps reported, percent ticks observed, primary URLs only.
        List<SetupProgress> progress = harness.ProgressSnapshot();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, progress.Select(p => p.Step).Distinct());
        Assert.Contains(progress, p => p.FilePercent is not null && p.FilePercent > 0);
        Assert.Equal(
            new[]
            {
                $"{harness.Server.BaseUrl}/gh/ggml-org/llama.cpp/releases/download/{SetupTestAssets.Tag}/{SetupTestAssets.AssetName}",
                $"{harness.Server.BaseUrl}/hf/{SetupTestAssets.Repo}/resolve/main/{SetupTestAssets.MainModelFile}",
                $"{harness.Server.BaseUrl}/hf/{SetupTestAssets.Repo}/resolve/main/{SetupTestAssets.MmprojModelFile}",
            },
            runner.AttemptedUrls);
    }

    [Fact]
    public async Task Second_run_is_idempotent_server_not_hit_again()
    {
        using var harness = new Harness();
        await harness.RunAsync(harness.NewRunner());
        int hitsAfterFirstRun = harness.Server.HitCount;

        var secondRunLog = new List<string>();
        SetupRunner second = harness.NewRunner();
        await harness.RunAsync(second, secondRunLog);

        Assert.Equal(hitsAfterFirstRun, harness.Server.HitCount);
        Assert.Empty(second.AttemptedUrls);
        Assert.Equal(3, secondRunLog.Count(line => line.Contains("skipping download", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(secondRunLog, line => line.Contains("already extracted", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, harness.Completed);
        Assert.True(new SettingsStore(harness.AppDataBase).Load().Configured);
    }

    // ---- Resume: .part + HTTP Range continuation ----

    [Fact]
    public async Task Interrupted_model_download_resumes_via_range_request()
    {
        using var harness = new Harness();
        const int alreadyDownloaded = 40_000;
        string partPath = Path.Combine(harness.ModelsDir, SetupTestAssets.MainModelFile + SetupRunner.PartSuffix);
        Directory.CreateDirectory(harness.ModelsDir);
        await File.WriteAllBytesAsync(partPath, harness.Main[..alreadyDownloaded]);

        SetupRunner runner = harness.NewRunner();
        await harness.RunAsync(runner);

        FileServerRequest resumeRequest = Assert.Single(
            harness.Server.SnapshotRequests(),
            r => r.RawUrl.Contains(SetupTestAssets.MainModelFile, StringComparison.OrdinalIgnoreCase)
                 && r.RangeHeader == $"bytes={alreadyDownloaded}-");
        Assert.NotNull(resumeRequest);

        // Assembled file is byte-exact, .part gone, setup completed.
        Assert.Equal(harness.Main, await File.ReadAllBytesAsync(runner.MainModelPath));
        Assert.False(File.Exists(partPath));
        Assert.True(new SettingsStore(harness.AppDataBase).Load().Configured);
        Assert.Equal(1, harness.Completed);
    }

    [Fact]
    public async Task Interrupted_zip_download_resumes_using_content_range_total()
    {
        using var harness = new Harness();
        const int alreadyDownloaded = 1_000;
        string partPath = Path.Combine(harness.BinDir, SetupTestAssets.AssetName + SetupRunner.PartSuffix);
        Directory.CreateDirectory(harness.BinDir);
        await File.WriteAllBytesAsync(partPath, harness.Zip[..alreadyDownloaded]);

        SetupRunner runner = harness.NewRunner();
        await harness.RunAsync(runner);

        Assert.Contains(harness.Server.SnapshotRequests(), r =>
            r.RawUrl.Contains(SetupTestAssets.AssetName, StringComparison.OrdinalIgnoreCase)
            && r.RangeHeader == $"bytes={alreadyDownloaded}-");
        Assert.Equal(harness.Zip, await File.ReadAllBytesAsync(runner.LlamaZipPath));
        Assert.False(File.Exists(partPath));
        Assert.True(File.Exists(runner.LlamaServerExePath));
        Assert.True(new SettingsStore(harness.AppDataBase).Load().Configured);
    }

    // ---- QA-: bad SHA aborts at verification, nothing configured ----

    [Fact]
    public async Task Wrong_sha256_aborts_at_verification_and_leaves_unconfigured()
    {
        using var harness = new Harness();
        string badSha = new string('0', 64);
        string manifestPath = SetupTestAssets.WriteManifest(
            github: $"{harness.Server.BaseUrl}/gh",
            ghProxyPrefix: $"{harness.Server.BaseUrl}/proxy/",
            huggingFace: $"{harness.Server.BaseUrl}/hf",
            hfMirror: $"{harness.Server.BaseUrl}/hfm",
            llamaSha256: SetupTestAssets.Sha256(harness.Zip),
            mainSha256: badSha,
            mmprojSha256: SetupTestAssets.Sha256(harness.Mmproj),
            mainSizeBytes: MainSize,
            mmprojSizeBytes: MmprojSize,
            directory: harness.InstallBase);
        var runner = new SetupRunner(manifestPath, new SetupRunnerOptions
        {
            InstallBaseDirectory = harness.InstallBase,
            Settings = new SettingsStore(harness.AppDataBase),
            MirrorRetryDelay = TimeSpan.Zero,
        });

        SetupException ex = await Assert.ThrowsAsync<SetupException>(() => runner.RunAsync());

        Assert.Contains("verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SetupTestAssets.MainModelFile, ex.Message);
        Assert.False(File.Exists(runner.InstalledVersionsPath), "installed manifest must not be written on bad SHA");
        Assert.False(new SettingsStore(harness.AppDataBase).Load().Configured);
        // Extraction is gated behind verification — no engine binaries on disk.
        Assert.False(File.Exists(runner.LlamaServerExePath));
    }

    // ---- QA-: 404 failover (exact URL forms, order, exactly-one retry) ----

    private const string LlamaPrimaryUrl =
        "https://github.com/ggml-org/llama.cpp/releases/download/b10964/llama-b10964-bin-win-cpu-x64.zip";

    private const string LlamaProxyUrl = "https://ghproxy.cn/" + LlamaPrimaryUrl;

    private const string MainPrimaryUrl =
        "https://huggingface.co/" + SetupTestAssets.Repo + "/resolve/main/" + SetupTestAssets.MainModelFile;

    private const string MainMirrorUrl =
        "https://hf-mirror.com/" + SetupTestAssets.Repo + "/resolve/main/" + SetupTestAssets.MainModelFile;

    private const string MmprojPrimaryUrl =
        "https://huggingface.co/" + SetupTestAssets.Repo + "/resolve/main/" + SetupTestAssets.MmprojModelFile;

    private static (string ManifestPath, string InstallBase, string AppDataBase) WriteRealUrlManifest(
        string llamaSha, string mainSha, string mmprojSha, long mainSize, long mmprojSize)
    {
        string installBase = SetupTestAssets.NewTempDir();
        string appDataBase = SetupTestAssets.NewTempDir();
        string manifestPath = SetupTestAssets.WriteManifest(
            github: "https://github.com",
            ghProxyPrefix: "https://ghproxy.cn/",
            huggingFace: "https://huggingface.co",
            hfMirror: "https://hf-mirror.com",
            llamaSha256: llamaSha,
            mainSha256: mainSha,
            mmprojSha256: mmprojSha,
            mainSizeBytes: mainSize,
            mmprojSizeBytes: mmprojSize,
            directory: installBase);
        return (manifestPath, installBase, appDataBase);
    }

    [Fact]
    public async Task Llama_cpp_404_fails_over_exactly_once_to_ghproxy_prefixed_url()
    {
        byte[] zip = SetupTestAssets.MakeFakeLlamaZip();
        byte[] main = SetupTestAssets.FakeBytes(4096, 5);
        byte[] mmproj = SetupTestAssets.FakeBytes(2048, 9);
        var (manifestPath, installBase, appDataBase) = WriteRealUrlManifest(
            SetupTestAssets.Sha256(zip), SetupTestAssets.Sha256(main), SetupTestAssets.Sha256(mmproj),
            main.Length, mmproj.Length);
        var handler = new StubHttpFileHandler(uri => uri switch
        {
            LlamaPrimaryUrl => StubHttpFileHandler.NotFound(),
            LlamaProxyUrl => StubHttpFileHandler.Bytes(zip),
            MainPrimaryUrl => StubHttpFileHandler.Bytes(main),
            MmprojPrimaryUrl => StubHttpFileHandler.Bytes(mmproj),
            _ => StubHttpFileHandler.NotFound(),
        });
        var runner = new SetupRunner(manifestPath, new SetupRunnerOptions
        {
            InstallBaseDirectory = installBase,
            Settings = new SettingsStore(appDataBase),
            HttpMessageHandler = handler,
            MirrorRetryDelay = TimeSpan.Zero,
        });

        await runner.RunAsync();

        IReadOnlyList<string> uris = handler.SnapshotUris();
        Assert.Equal(LlamaPrimaryUrl, uris[0]); // order: primary first
        Assert.Equal(LlamaProxyUrl, uris[1]);   // exact ghproxy prefix form
        Assert.Equal(2, uris.Count(u => u.Contains(SetupTestAssets.AssetName, StringComparison.OrdinalIgnoreCase)));
        Assert.True(File.Exists(runner.LlamaServerExePath), "mirror-served zip must still extract");
        Assert.True(new SettingsStore(appDataBase).Load().Configured);
    }

    [Fact]
    public async Task Llama_cpp_mirror_404_too_fails_after_exactly_two_attempts()
    {
        var (manifestPath, installBase, appDataBase) = WriteRealUrlManifest(
            SetupTestAssets.Sha256(SetupTestAssets.FakeBytes(64, 1)),
            SetupTestAssets.Sha256(SetupTestAssets.FakeBytes(16, 2)),
            SetupTestAssets.Sha256(SetupTestAssets.FakeBytes(16, 3)),
            16, 16);
        var handler = new StubHttpFileHandler(_ => StubHttpFileHandler.NotFound()); // llama primary AND ghproxy both 404
        var runner = new SetupRunner(manifestPath, new SetupRunnerOptions
        {
            InstallBaseDirectory = installBase,
            Settings = new SettingsStore(appDataBase),
            HttpMessageHandler = handler,
            MirrorRetryDelay = TimeSpan.Zero,
        });

        SetupException ex = await Assert.ThrowsAsync<SetupException>(() => runner.RunAsync());

        Assert.Contains("download failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.SnapshotUris().Count); // exactly one retry, no third attempt
        Assert.Equal(LlamaPrimaryUrl, handler.SnapshotUris()[0]);
        Assert.Equal(LlamaProxyUrl, handler.SnapshotUris()[1]);
    }

    [Fact]
    public async Task Model_404_fails_over_to_hf_mirror_host_swap()
    {
        byte[] zip = SetupTestAssets.MakeFakeLlamaZip();
        byte[] main = SetupTestAssets.FakeBytes(4096, 5);
        byte[] mmproj = SetupTestAssets.FakeBytes(2048, 9);
        var (manifestPath, installBase, appDataBase) = WriteRealUrlManifest(
            SetupTestAssets.Sha256(zip), SetupTestAssets.Sha256(main), SetupTestAssets.Sha256(mmproj),
            main.Length, mmproj.Length);
        var handler = new StubHttpFileHandler(uri => uri switch
        {
            LlamaPrimaryUrl => StubHttpFileHandler.Bytes(zip),
            MainPrimaryUrl => StubHttpFileHandler.NotFound(),
            MainMirrorUrl => StubHttpFileHandler.Bytes(main),
            MmprojPrimaryUrl => StubHttpFileHandler.Bytes(mmproj),
            _ => StubHttpFileHandler.NotFound(),
        });
        var runner = new SetupRunner(manifestPath, new SetupRunnerOptions
        {
            InstallBaseDirectory = installBase,
            Settings = new SettingsStore(appDataBase),
            HttpMessageHandler = handler,
            MirrorRetryDelay = TimeSpan.Zero,
        });

        await runner.RunAsync();

        IReadOnlyList<string> uris = handler.SnapshotUris();
        int mainPrimaryIndex = uris.ToList().IndexOf(MainPrimaryUrl);
        int mainMirrorIndex = uris.ToList().IndexOf(MainMirrorUrl);
        Assert.True(mainPrimaryIndex >= 0, "primary model URL must be requested");
        Assert.True(mainMirrorIndex > mainPrimaryIndex, "mirror must be requested after primary fails");
        Assert.Equal(MainMirrorUrl, uris[mainMirrorIndex]); // exact host-swap form
        Assert.Equal(main, await File.ReadAllBytesAsync(runner.MainModelPath));
        Assert.True(new SettingsStore(appDataBase).Load().Configured);
    }

    // ---- Cancellation: .part cleaned, finished artifacts kept ----

    [Fact]
    public async Task Cancellation_mid_download_cleans_part_files_and_keeps_finished_artifacts()
    {
        byte[] zip = SetupTestAssets.MakeFakeLlamaZip();
        var blocking = new BlockingStream();
        var (manifestPath, installBase, appDataBase) = WriteRealUrlManifest(
            SetupTestAssets.Sha256(zip),
            SetupTestAssets.Sha256(SetupTestAssets.FakeBytes(32, 7)),
            SetupTestAssets.Sha256(SetupTestAssets.FakeBytes(32, 8)),
            32, 32);
        var handler = new StubHttpFileHandler(uri => uri switch
        {
            LlamaPrimaryUrl => StubHttpFileHandler.Bytes(zip),
            MainPrimaryUrl => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StreamContent(blocking),
            },
            _ => StubHttpFileHandler.NotFound(),
        });
        var runner = new SetupRunner(manifestPath, new SetupRunnerOptions
        {
            InstallBaseDirectory = installBase,
            Settings = new SettingsStore(appDataBase),
            HttpMessageHandler = handler,
            MirrorRetryDelay = TimeSpan.Zero,
        });
        string installRoot = Path.Combine(installBase, SetupRunner.InstallDirName);

        using var cancellation = new CancellationTokenSource();
        Task run = Task.Run(() => runner.RunAsync(null, cancellation.Token));

        string mainPart = Path.Combine(
            installRoot, SetupRunner.ModelsDirName, SetupTestAssets.MainModelFile + SetupRunner.PartSuffix);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(mainPart), 10_000), "model .part never appeared");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(runner.LlamaZipPath), 10_000), "zip never finished");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(File.Exists(mainPart), "cancelled .part must be cleaned up");
        Assert.Empty(Directory.EnumerateFiles(installRoot, "*" + SetupRunner.PartSuffix, SearchOption.AllDirectories));
        Assert.True(File.Exists(runner.LlamaZipPath), "finished artifacts must survive cancellation");
        Assert.False(File.Exists(runner.InstalledVersionsPath), "cancelled run must not finalize");
        Assert.False(new SettingsStore(appDataBase).Load().Configured);
        blocking.Release();
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Versions;

namespace WinLocalASR.Core.Setup;

/// <summary>
/// Six-step first-run installer (Windows port of the macOS SetupRunner philosophy:
/// one click, self-contained, progress callbacks; the artifacts differ — pinned
/// llama.cpp binaries + GGUF models instead of a Python venv).
///
///   1. Create %LOCALAPPDATA%\WinLocalASR\{bin,models}.
///   2. Download the pinned llama.cpp win-x64 zip into bin/ (failover: GitHub direct
///      fails → exactly ONE retry with the ghproxy.cn prefix form).
///   3. Download the Q8_0 GGUF pair into models/ (failover: huggingface.co fails →
///      hf-mirror.com host swap). Resumable via .part + HTTP Range.
///   4. SHA256-verify every artifact against the manifest, then extract the WHOLE
///      zip (CPU dispatch DLLs are selected at load time) — never extract unverified bytes.
///   5. Write the installed versions.json (manifest + the three resolved-path keys
///      consumed by LlamaServerPaths — that contract is load-bearing) and set
///      Configured=true via SettingsStore.
///   6. Invoke the completion delegate.
///
/// Idempotent: an existing file whose SHA matches the manifest is skipped. Cancellation
/// is cooperative and cleans up .part files (finished artifacts stay; .part files after
/// a non-cancel failure stay too, so the next run resumes instead of re-downloading).
/// Every URL attempted is recorded in <see cref="AttemptedUrls"/> and mirrored into the
/// step-log so a real-machine run can prove whether failover triggered.
/// </summary>
public sealed class SetupRunner
{
    public const string InstallDirName = "WinLocalASR";
    public const string BinDirName = "bin";
    public const string ModelsDirName = "models";
    public const string InstalledVersionsFileName = "versions.json";
    public const string PartSuffix = ".part";
    public const string LlamaCppRepoPath = "ggml-org/llama.cpp";
    public const int TotalSteps = 6;

    private static readonly JsonSerializerOptions InstalledManifestOptions = new() { WriteIndented = true };

    private readonly SetupRunnerOptions _options;
    private readonly string _rawManifestJson;
    private readonly List<string> _attemptedUrls = new();
    private bool _zipDownloadedThisRun;

    public SetupRunner(string manifestPath, SetupRunnerOptions? options = null)
    {
        Manifest = VersionManifest.Load(manifestPath);
        _rawManifestJson = File.ReadAllText(manifestPath);
        _options = options ?? new SetupRunnerOptions();

        InstallRoot = Path.Combine(_options.InstallBaseDirectory, InstallDirName);
        BinDirectory = Path.Combine(InstallRoot, BinDirName);
        ModelsDirectory = Path.Combine(InstallRoot, ModelsDirName);
        LlamaZipPath = Path.Combine(BinDirectory, Manifest.LlamaCpp.AssetName);
        LlamaServerExePath = Path.Combine(BinDirectory, "llama-server.exe");
        MainModelPath = Path.Combine(ModelsDirectory, Manifest.Models.Main.File);
        MmprojModelPath = Path.Combine(ModelsDirectory, Manifest.Models.Mmproj.File);
        InstalledVersionsPath = Path.Combine(InstallRoot, InstalledVersionsFileName);
    }

    public VersionManifest Manifest { get; }

    public string InstallRoot { get; }

    public string BinDirectory { get; }

    public string ModelsDirectory { get; }

    /// <summary>The retained release zip in bin/ — the idempotence anchor for step 2
    /// (existing zip with matching SHA = skip download AND extraction).</summary>
    public string LlamaZipPath { get; }

    public string LlamaServerExePath { get; }

    public string MainModelPath { get; }

    public string MmprojModelPath { get; }

    public string InstalledVersionsPath { get; }

    /// <summary>Diagnostics: every download URL attempted this run, in order.</summary>
    public IReadOnlyList<string> AttemptedUrls => _attemptedUrls;

    public async Task RunAsync(Action<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _attemptedUrls.Clear();
        _zipDownloadedThisRun = false;

        using var handler = _options.HttpMessageHandler ?? new HttpClientHandler();
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            // Downloads of ~2.5 GB must not be killed by the 100 s default request
            // timeout. Transport failures still surface fast and trigger mirror
            // failover; true hangs are covered by user cancel + resume.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        try
        {
            await Step1CreateDirectoriesAsync(progress, cancellationToken).ConfigureAwait(false);
            await Step2DownloadLlamaCppAsync(client, progress, cancellationToken).ConfigureAwait(false);
            await Step3DownloadModelsAsync(client, progress, cancellationToken).ConfigureAwait(false);
            await Step4VerifyAndExtractAsync(progress, cancellationToken).ConfigureAwait(false);
            Step5WriteInstalledManifest(progress);
            await Step6CompleteAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupPartFiles();
            throw;
        }
    }

    private Task Step1CreateDirectoriesAsync(Action<SetupProgress>? progress, CancellationToken ct)
    {
        Report(progress, 1, "CreateDirectories", $"Creating install directories under '{InstallRoot}'…");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Report(progress, 1, "CreateDirectories", "Install directories ready.");
        return Task.CompletedTask;
    }

    private async Task Step2DownloadLlamaCppAsync(HttpClient client, Action<SetupProgress>? progress, CancellationToken ct)
    {
        Report(progress, 2, "DownloadLlamaCpp",
            $"Downloading llama.cpp {Manifest.LlamaCpp.Tag} from GitHub ({Manifest.LlamaCpp.AssetName})…");
        _zipDownloadedThisRun = await DownloadArtifactAsync(
            client,
            LlamaCppDownloadUrl(Manifest),
            LlamaCppProxyDownloadUrl(Manifest),
            LlamaZipPath,
            Manifest.LlamaCpp.Sha256,
            expectedSize: null,
            label: Manifest.LlamaCpp.AssetName,
            step: 2,
            stepName: "DownloadLlamaCpp",
            progress,
            ct).ConfigureAwait(false);
    }

    private async Task Step3DownloadModelsAsync(HttpClient client, Action<SetupProgress>? progress, CancellationToken ct)
    {
        Report(progress, 3, "DownloadModels", "Downloading Qwen3-ASR-1.7B Q8_0 models (~2.5 GB) from Hugging Face…");
        await DownloadArtifactAsync(
            client,
            ModelDownloadUrl(Manifest.Models.Main, Manifest.Urls.HuggingFace),
            ModelMirrorDownloadUrl(Manifest.Models.Main, Manifest.Urls),
            MainModelPath,
            Manifest.Models.Main.Sha256,
            Manifest.Models.Main.SizeBytes,
            Manifest.Models.Main.File,
            3, "DownloadModels", progress, ct).ConfigureAwait(false);
        await DownloadArtifactAsync(
            client,
            ModelDownloadUrl(Manifest.Models.Mmproj, Manifest.Urls.HuggingFace),
            ModelMirrorDownloadUrl(Manifest.Models.Mmproj, Manifest.Urls),
            MmprojModelPath,
            Manifest.Models.Mmproj.Sha256,
            Manifest.Models.Mmproj.SizeBytes,
            Manifest.Models.Mmproj.File,
            3, "DownloadModels", progress, ct).ConfigureAwait(false);
    }

    private async Task Step4VerifyAndExtractAsync(Action<SetupProgress>? progress, CancellationToken ct)
    {
        Report(progress, 4, "VerifyChecksums", "Verifying SHA-256 checksums…");
        ct.ThrowIfCancellationRequested();
        VerifyArtifact(LlamaZipPath, Manifest.LlamaCpp.Sha256, Manifest.LlamaCpp.AssetName, progress);
        VerifyArtifact(MainModelPath, Manifest.Models.Main.Sha256, Manifest.Models.Main.File, progress);
        VerifyArtifact(MmprojModelPath, Manifest.Models.Mmproj.Sha256, Manifest.Models.Mmproj.File, progress);

        if (_zipDownloadedThisRun || !File.Exists(LlamaServerExePath))
        {
            ct.ThrowIfCancellationRequested();
            Report(progress, 4, "VerifyChecksums",
                $"Extracting {Manifest.LlamaCpp.AssetName} into '{BinDirectory}' (whole zip — CPU dispatch DLLs load dynamically)…");
            try
            {
                ZipFile.ExtractToDirectory(LlamaZipPath, BinDirectory, overwriteFiles: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new SetupException($"Failed to extract {Manifest.LlamaCpp.AssetName}: {ex.Message}", _attemptedUrls.ToArray(), ex);
            }

            if (!File.Exists(LlamaServerExePath))
            {
                throw new SetupException(
                    $"The llama.cpp zip did not contain llama-server.exe (expected at '{LlamaServerExePath}').",
                    _attemptedUrls.ToArray());
            }
        }
        else
        {
            Report(progress, 4, "VerifyChecksums", "llama-server.exe already extracted — skipping extraction.");
        }
    }

    private void Step5WriteInstalledManifest(Action<SetupProgress>? progress)
    {
        Report(progress, 5, "FinalizeInstall", "Writing installed versions.json…");
        JsonObject root = JsonNode.Parse(_rawManifestJson)!.AsObject();

        // TOFU freeze: pin the computed llama.cpp hash into the installed copy.
        if (Manifest.LlamaCpp.Sha256Policy == "tofu" && Manifest.LlamaCpp.Sha256 is null)
        {
            root["llamaCpp"]!["sha256"] = Sha256File(LlamaZipPath);
        }

        root["llamaServerPath"] = LlamaServerExePath;
        root["mainModelPath"] = MainModelPath;
        root["mmprojModelPath"] = MmprojModelPath;

        string tmpPath = InstalledVersionsPath + ".tmp";
        File.WriteAllText(tmpPath, root.ToJsonString(InstalledManifestOptions));
        File.Move(tmpPath, InstalledVersionsPath, overwrite: true);

        SettingsStore store = _options.Settings ?? new SettingsStore();
        store.Save(store.Load() with { Configured = true });
        Report(progress, 5, "FinalizeInstall", $"Installed manifest written to '{InstalledVersionsPath}'; Configured=true.");
    }

    private async Task Step6CompleteAsync(Action<SetupProgress>? progress, CancellationToken ct)
    {
        Report(progress, 6, "Complete", "Setup complete.");
        if (_options.Completion is { } completion)
        {
            await completion().ConfigureAwait(false);
        }
    }

    /// <summary>Downloads one artifact to <paramref name="finalPath"/>, resuming an
    /// existing .part file via HTTP Range, failing over from primary to mirror exactly
    /// once. Returns true when bytes were fetched this run; false = skipped (existing
    /// file already verified).</summary>
    private async Task<bool> DownloadArtifactAsync(
        HttpClient client,
        string primaryUrl,
        string mirrorUrl,
        string finalPath,
        string? expectedSha256,
        long? expectedSize,
        string label,
        int step,
        string stepName,
        Action<SetupProgress>? progress,
        CancellationToken ct)
    {
        if (IsVerified(finalPath, expectedSha256, expectedSize))
        {
            Report(progress, step, stepName, $"{label}: already present and verified — skipping download.");
            return false;
        }

        string partPath = finalPath + PartSuffix;
        string[] attempts = { primaryUrl, mirrorUrl };
        Exception? lastError = null;

        for (int i = 0; i < attempts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string url = attempts[i];
            _attemptedUrls.Add(url);
            Report(progress, step, stepName, $"{label}: fetching {url}");
            try
            {
                await DownloadOnceAsync(client, url, partPath, finalPath, expectedSize, label, step, stepName, progress, ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (i == 0)
                {
                    Report(progress, step, stepName,
                        $"{label}: primary failed ({ex.Message}); retrying once via mirror: {mirrorUrl}");
                    await _options.EffectiveDelay(_options.MirrorRetryDelay, ct).ConfigureAwait(false);
                }
            }
        }

        throw new SetupException(
            $"{label}: download failed after primary and mirror attempts. URLs tried: {string.Join(" | ", _attemptedUrls)}",
            _attemptedUrls.ToArray(),
            lastError);
    }

    private async Task DownloadOnceAsync(
        HttpClient client,
        string url,
        string partPath,
        string finalPath,
        long? expectedSize,
        string label,
        int step,
        string stepName,
        Action<SetupProgress>? progress,
        CancellationToken ct)
    {
        long startLength = 0;
        if (File.Exists(partPath))
        {
            startLength = new FileInfo(partPath).Length;
            if (expectedSize is long pinned && startLength > pinned)
            {
                // A .part larger than the pinned size is corrupt — restart cleanly.
                File.Delete(partPath);
                startLength = 0;
            }
            else if (expectedSize is long complete && startLength == complete)
            {
                // Finished download that never got moved (e.g. process killed post-write).
                File.Move(partPath, finalPath, overwrite: true);
                Report(progress, step, stepName, $"{label}: completed .part finalized ({complete:N0} bytes).");
                return;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (startLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(startLength, null);
        }

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}");
        }

        bool resumed = startLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (startLength > 0 && !resumed)
        {
            // Server ignored the Range — restart from scratch (FileMode.Create truncates).
            startLength = 0;
        }

        long total = expectedSize ?? 0;
        if (resumed && ParseContentRangeTotal(response.Content.Headers.ContentRange) is long rangeTotal)
        {
            total = rangeTotal;
        }
        else if (total == 0 && response.Content.Headers.ContentLength is long contentLength)
        {
            total = contentLength;
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        long finalLength;
        await using (var target = new FileStream(
                   partPath,
                   resumed ? FileMode.Append : FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 81920,
                   useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            int? lastPercent = null;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    int percent = (int)(100 * (startLength + received) / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        Report(progress, step, stepName, $"{label}: {percent}%", label, percent);
                    }
                }
            }

            finalLength = startLength + received;
        }

        if (total > 0 && finalLength != total)
        {
            throw new HttpRequestException($"Incomplete download for {url}: got {finalLength:N0} of {total:N0} bytes.");
        }

        // The .part handle is closed above: Windows File.Move refuses open files (Unix allows it).
        File.Move(partPath, finalPath, overwrite: true);
        Report(progress, step, stepName,
            $"{label}: download complete ({finalLength:N0} bytes{(resumed ? ", resumed from .part" : "")}).");
    }

    private void VerifyArtifact(string path, string? expectedSha256, string label, Action<SetupProgress>? progress)
    {
        if (expectedSha256 is null)
        {
            Report(progress, 4, "VerifyChecksums", $"{label}: no pinned sha256 (TOFU) — accepting first download.");
            return;
        }

        string actual = Sha256File(path);
        if (actual != expectedSha256)
        {
            throw new SetupException(
                $"Checksum verification failed for {label}: expected sha256 {expectedSha256}, got {actual}.",
                _attemptedUrls.ToArray());
        }

        Report(progress, 4, "VerifyChecksums", $"{label}: sha256 verified.");
    }

    private static bool IsVerified(string path, string? expectedSha256, long? expectedSize)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (expectedSize is long size && new FileInfo(path).Length != size)
        {
            return false;
        }

        return expectedSha256 is null || Sha256File(path) == expectedSha256;
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static long? ParseContentRangeTotal(ContentRangeHeaderValue? header)
    {
        string? text = header?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int slash = text.LastIndexOf('/');
        return slash >= 0 && slash < text.Length - 1 && long.TryParse(text[(slash + 1)..], out long total)
            ? total
            : null;
    }

    private void CleanupPartFiles()
    {
        try
        {
            foreach (string part in Directory.EnumerateFiles(InstallRoot, "*" + PartSuffix, SearchOption.AllDirectories))
            {
                File.Delete(part);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup on cancel.
        }
    }

    private static void Report(Action<SetupProgress>? progress, int step, string stepName, string message,
        string? fileName = null, int? filePercent = null)
        => progress?.Invoke(new SetupProgress(step, stepName, message, fileName, filePercent));

    // ---- URL construction (pure; exact forms are unit-tested against the pinned manifest) ----

    /// <summary>GitHub release download URL for the pinned llama.cpp win-x64 asset.</summary>
    public static string LlamaCppDownloadUrl(VersionManifest manifest) =>
        $"{manifest.Urls.Github.TrimEnd('/')}/{LlamaCppRepoPath}/releases/download/{manifest.LlamaCpp.Tag}/{manifest.LlamaCpp.AssetName}";

    /// <summary>ghproxy failover: the verbatim prefix form <c>{ghProxyPrefix}{primary-url}</c>.</summary>
    public static string LlamaCppProxyDownloadUrl(VersionManifest manifest) =>
        manifest.Urls.GhProxyPrefix + LlamaCppDownloadUrl(manifest);

    /// <summary>Hugging Face resolve URL for a pinned model file.</summary>
    public static string ModelDownloadUrl(ModelPin pin, string baseUrl) =>
        $"{baseUrl.TrimEnd('/')}/{pin.Repo}/resolve/{pin.Revision}/{pin.File}";

    /// <summary>hf-mirror failover: the same path with the host swapped from
    /// huggingface.co to the hf-mirror base.</summary>
    public static string ModelMirrorDownloadUrl(ModelPin pin, MirrorUrls urls)
    {
        string primary = ModelDownloadUrl(pin, urls.HuggingFace);
        string prefix = urls.HuggingFace.TrimEnd('/');
        if (!primary.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Model download URL did not start with the huggingface base URL.");
        }

        return urls.HfMirror.TrimEnd('/') + primary[prefix.Length..];
    }
}

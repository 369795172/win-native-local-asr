using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinLocalASR.Core.Inference;

/// <summary>
/// Port of MacLocalASR ASRBridgeClient.swift (lines 22-246) onto the spike-verified llama-server
/// HTTP contract (docs/rfc.md §Inference Contract). Public surface mapping:
///   start(bridgePath:modelPath:venvPython:)    -> StartAsync() (artifact paths via options)
///   ready                                      -> IsReady
///   transcribe(audioURL:context:timeout:)      -> TranscribeAsync(wavPath, context, cancellationToken, timeout)
///   stop()                                     -> StopAsync()
///   diagnosticLines (50-line ring buffer)      -> GetDiagnostics()
///   processTerminated -> restartAfterFailure   -> CrashRecovery / RestartFailed event
///
/// Wire shape (contract-pinned): spawn `llama-server -m main.gguf --mmproj mmproj.gguf
/// --host 127.0.0.1 --port P`; ready-poll GET /health until 200; transcribe via multipart POST
/// /v1/audio/transcriptions with required `file` part and optional `prompt` field (hotword
/// context; `response_format` omitted — json is the server default, `text` is a hard 400);
/// response `.text` is stripped of the `language X&lt;asr_text&gt;` artifact prefix.
///
/// Semantics ported from Swift: ready handshake before first transcribe; every transcribe
/// failure terminates the process and best-effort restarts it before the original error is
/// rethrown; crash while idle triggers the single-flight restart loop with 1/2/4 s backoff and
/// at most 3 attempts (then RestartFailedException); Stop() attempts POST /shutdown (contract:
/// the endpoint does not exist and 404s — kept for parity), waits StopGracePeriod, then kills.
///
/// Deliberate deviation: OperationCanceledException from the caller's token (Esc) propagates
/// without an engine restart — Swift has no cancellation path, and killing a healthy
/// multi-second model load because the caller cancelled would not serve those semantics.
/// </summary>
public sealed class LlamaServerClient : IDisposable
{
    private const int DiagnosticLineLimit = 50;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly LlamaServerClientOptions _options;
    private readonly IProcessSpawner _spawner;
    private readonly HttpClient _http;
    private readonly object _sync = new();          // process / ready / stopping state
    private readonly object _restartGate = new();  // single-flight restart loop
    private readonly List<string> _diagnostics = new();

    private IManagedProcess? _process;
    private Uri? _baseUrl;
    private bool _stopping;
    private Task _restartLoop = Task.CompletedTask;

    public bool IsReady { get; private set; }

    /// <summary>Raised when the crash auto-restart loop exhausts all attempts.</summary>
    public event Action<RestartFailedException>? RestartFailed;

    /// <summary>
    /// The restart loop triggered by the last unattended process exit. Faults with
    /// RestartFailedException on exhaustion; completes normally after a successful restart.
    /// </summary>
    public Task CrashRecovery
    {
        get { lock (_restartGate) { return _restartLoop; } }
    }

    public LlamaServerClient(LlamaServerClientOptions options, HttpMessageHandler? handler = null, IProcessSpawner? spawner = null)
    {
        _options = options;
        _spawner = spawner ?? new SystemProcessSpawner();
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request CancellationTokenSource owns timeouts
        };
    }

    /// <summary>Swift start(): launch llama-server and wait for /health. Never auto-restarts.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_restartGate)
        {
            _restartLoop = Task.CompletedTask; // Swift start() resets restartAttempts = 0
        }

        await LaunchAndWaitUntilReadyAsync(cancellationToken);
    }

    /// <summary>
    /// Swift transcribe(): POST the WAV, return the stripped transcript. Timeout defaults to
    /// max(90, 3 × duration + 20) s from the WAV header; pass <paramref name="timeout"/> to
    /// override (Swift's timeout parameter). On failure the engine is restarted best-effort and
    /// the original error rethrown.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string wavPath,
        string? context = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (!IsReady)
        {
            throw new NotReadyException();
        }

        TimeSpan effectiveTimeout = timeout
            ?? TranscriptionTimeout.Compute(WavDurationParser.ParseDuration(wavPath));

        try
        {
            return await PostTranscriptionAsync(wavPath, context, effectiveTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancelled: engine untouched (see class doc — deliberate deviation)
        }
        catch (Exception)
        {
            // Swift transcribe catch: terminate the process, best-effort restart, rethrow original.
            await TerminateProcessAsync();
            try
            {
                await GetOrStartRestartLoop();
            }
            catch (RestartFailedException failure)
            {
                AppendDiagnostic($"Auto-restart exhausted after transcription failure: {failure.Message}");
            }

            throw;
        }
    }

    /// <summary>Swift stop(): graceful attempt, grace period, then kill. Suppresses auto-restart.</summary>
    public async Task StopAsync()
    {
        lock (_sync)
        {
            _stopping = true;
            IsReady = false;
        }

        IManagedProcess? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is { HasExited: false })
        {
            // Contract: /shutdown does not exist (404). The attempt is kept for parity.
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl!, "/shutdown"));
                _ = await _http.SendAsync(request, shutdownCts.Token);
            }
            catch
            {
            }

            if (!process.HasExited)
            {
                await Task.Delay(_options.StopGracePeriod);
            }
        }

        await TerminateProcessAsync();

        lock (_sync)
        {
            _stopping = false;
        }
    }

    /// <summary>Last 50 diagnostic lines (server stdout/stderr + lifecycle events).</summary>
    public IReadOnlyList<string> GetDiagnostics()
    {
        lock (_diagnostics)
        {
            return _diagnostics.ToArray();
        }
    }

    private async Task<string> PostTranscriptionAsync(string wavPath, string? context, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl!, "/v1/audio/transcriptions"));
        using var multipart = new MultipartFormDataContent();
        await using FileStream wav = File.OpenRead(wavPath);
        var filePart = new StreamContent(wav);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(filePart, "file", Path.GetFileName(wavPath));
        if (!string.IsNullOrEmpty(context))
        {
            multipart.Add(new StringContent(context), "prompt");
        }

        request.Content = multipart;

        string body;
        using (HttpResponseMessage response = await SendAsync(request, cts.Token, cancellationToken))
        {
            body = await ReadBodyAsync(response, cts.Token, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new RuntimeException((int)response.StatusCode, Summarize(body));
            }
        }

        return ParseAndStripTranscript(body);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, CancellationToken callerToken)
    {
        try
        {
            return await _http.SendAsync(request, token);
        }
        catch (HttpRequestException ex)
        {
            // Swift send() guard: an unreachable engine surfaces as processExited.
            throw new ProcessExitedException($"llama-server is unreachable: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // the linked per-request token fired, not the caller's: our timeout
            throw new TimeoutException("Request timed out.");
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token, CancellationToken callerToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(token);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException("Reading the response timed out.");
        }
    }

    private static string ParseAndStripTranscript(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out JsonElement typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && typeElement.GetString() == "transcript.text.done"
                && root.TryGetProperty("text", out JsonElement textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                return TranscriptArtifactStripper.Strip(textElement.GetString()!);
            }
        }
        catch (JsonException)
        {
        }

        throw new RuntimeException($"Unexpected transcription response shape: {Summarize(body)}");
    }

    private async Task LaunchAndWaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        await TerminateProcessAsync(); // Swift: launchAndWaitUntilReady begins with terminateProcess

        int port = _options.Port ?? PortProber.FindFreePort(_options.BasePort, _options.ReservedPort);
        _baseUrl = new Uri($"http://127.0.0.1:{port}/");
        AppendDiagnostic($"Starting llama-server on 127.0.0.1:{port}");

        IManagedProcess process = _spawner.Spawn(BuildStartInfo(port));
        process.OutputLineReceived += (_, line) => AppendDiagnostic(line);
        process.ErrorLineReceived += (_, line) => AppendDiagnostic(line);
        process.Exited += OnProcessExited;
        lock (_sync)
        {
            _process = process;
        }

        try
        {
            await PollUntilHealthyAsync(cancellationToken);
        }
        catch
        {
            await TerminateProcessAsync();
            throw;
        }

        IsReady = true;
    }

    private ProcessStartInfo BuildStartInfo(int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Paths.LlamaServerExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(_options.Paths.MainModelPath);
        startInfo.ArgumentList.Add("--mmproj");
        startInfo.ArgumentList.Add(_options.Paths.MmprojModelPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private async Task PollUntilHealthyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.StartupTimeout);
        var healthUri = new Uri(_baseUrl!, "/health");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
            {
                throw new ProcessExitedException(
                    $"llama-server exited before becoming healthy. Recent logs: {RecentDiagnostics()}");
            }

            try
            {
                using HttpResponseMessage response = await _http.GetAsync(healthUri, deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // server not listening yet — keep polling
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"llama-server did not become healthy within {_options.StartupTimeout.TotalSeconds:0}s. Recent logs: {RecentDiagnostics()}");
            }

            try
            {
                await Task.Delay(_options.HealthPollInterval, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"llama-server did not become healthy within {_options.StartupTimeout.TotalSeconds:0}s. Recent logs: {RecentDiagnostics()}");
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        IManagedProcess? current;
        lock (_sync)
        {
            current = _process;
        }

        if (!ReferenceEquals(current, sender))
        {
            return; // stale event from an already-replaced process (Swift: process === guard)
        }

        IsReady = false;
        AppendDiagnostic("llama-server process exited.");

        bool stopping;
        lock (_sync)
        {
            stopping = _stopping;
        }

        if (!stopping)
        {
            _ = GetOrStartRestartLoop(); // fire-and-forget; observe via CrashRecovery / RestartFailed
        }
    }

    /// <summary>Single-flight: returns the running restart loop, or starts a new one (Swift loop: backoff 1/2/4, max 3 attempts).</summary>
    private Task GetOrStartRestartLoop()
    {
        lock (_restartGate)
        {
            if (_restartLoop.IsCompleted)
            {
                _restartLoop = RunRestartLoopAsync();
                _ = _restartLoop.ContinueWith(
                    static t => { _ = t.Exception; }, // observe so un-awaited faults stay silent
                    TaskScheduler.Default);
            }

            return _restartLoop;
        }
    }

    private async Task RunRestartLoopAsync()
    {
        foreach (TimeSpan delay in _options.RestartBackoff)
        {
            AppendDiagnostic($"llama-server crashed; restarting in {delay.TotalSeconds:0.##}s");
            await Task.Delay(delay);
            try
            {
                await LaunchAndWaitUntilReadyAsync(CancellationToken.None);
                AppendDiagnostic("llama-server restarted successfully.");
                return;
            }
            catch (Exception ex)
            {
                AppendDiagnostic($"Restart attempt failed: {ex.Message}");
            }
        }

        var failure = new RestartFailedException(
            $"llama-server failed to restart after {_options.RestartBackoff.Count} attempts.");
        AppendDiagnostic(failure.Message);
        try
        {
            RestartFailed?.Invoke(failure);
        }
        catch (Exception ex)
        {
            AppendDiagnostic($"RestartFailed subscriber threw: {ex.Message}");
        }

        throw failure;
    }

    private Task TerminateProcessAsync()
    {
        IManagedProcess? process;
        lock (_sync)
        {
            IsReady = false;
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.Dispose();
        }

        return Task.CompletedTask;
    }

    private void AppendDiagnostic(string line)
    {
        lock (_diagnostics)
        {
            _diagnostics.Add(line);
            if (_diagnostics.Count > DiagnosticLineLimit)
            {
                _diagnostics.RemoveRange(0, _diagnostics.Count - DiagnosticLineLimit);
            }
        }
    }

    private string RecentDiagnostics()
    {
        lock (_diagnostics)
        {
            return _diagnostics.Count == 0 ? "(none)" : string.Join(" | ", _diagnostics.TakeLast(5));
        }
    }

    private static string Summarize(string body)
    {
        string collapsed = Whitespace.Replace(body, " ").Trim();
        return collapsed.Length <= 500 ? collapsed : collapsed[..500] + "…";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _stopping = true;
        }

        TerminateProcessAsync().GetAwaiter().GetResult();
        _http.Dispose();
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Control;

/// <summary>
/// Opt-in localhost test-control API (Task 12; the mac original's ControlServer on 17844,
/// this port is 17846 so the two can coexist). Hard rules: NEVER constructed during normal
/// startup — only when the bootstrapper parsed <c>--enable-control-server</c> — and the
/// listener binds <c>localhost</c> only. Port conflicts degrade to a logged warning; the
/// main application is fully functional without it and never crashes.
///
/// The /status JSON field names are a load-bearing wire contract consumed by
/// <c>tests/e2e.ps1</c> (and Task 13's CI step): phase, hudControlValue, configured,
/// bridgeReady, lastTranscript, phaseHistory.
/// </summary>
public sealed class ControlServer : IDisposable
{
    public const int DefaultPort = 17846;

    private readonly ControlServerOptions _options;
    private readonly HttpListener _listener = new();
    private readonly object _historyLock = new();
    private readonly List<string> _phaseHistory = new();
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;

    public ControlServer(ControlServerOptions options)
    {
        _options = options;
        _listener.Prefixes.Add($"http://localhost:{options.Port}/");
        // Seed with the construction-time phase so the history starts honestly (usually
        // "loading" — the bootstrapper wires the server before InitializeAsync fires).
        _phaseHistory.Add(PhaseLabels.ToString(options.Controller.Phase));
        options.Controller.PhaseChanged += OnPhaseChanged;
    }

    /// <summary>False after a degraded <see cref="Start"/> (port conflict) or after Dispose.</summary>
    public bool IsListening => _listener.IsListening;

    /// <summary>
    /// Starts listening. A port conflict (or any listener failure) degrades to a WARN —
    /// the QA- contract: the main graph stays constructed and the app keeps running.
    /// </summary>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            _options.Log.Warn(
                $"control server degraded: cannot listen on localhost:{_options.Port} ({ex.Message}); " +
                "continuing without the test API");
            return;
        }

        _acceptCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));
        _options.Log.Info($"control server listening on http://localhost:{_options.Port}/");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_listener.IsListening && !cancellationToken.IsCancellationRequested)
            {
                // No cancellation-token overload exists on HttpListener; Dispose/Stop
                // unblocks a pending accept with ObjectDisposedException/HttpListenerException.
                HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                HandleRequestSafe(context);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void HandleRequestSafe(HttpListenerContext context)
    {
        try
        {
            HandleRequest(context);
        }
        catch (Exception ex)
        {
            Respond(context, 500, $$"""{"error":"{{JsonEscape(ex.Message)}}"}""");
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath.TrimEnd('/');
        string method = context.Request.HttpMethod;

        switch (path)
        {
            case "/status" when method == "GET":
                Respond(context, 200, BuildStatusJson());
                break;

            case "/setup" when method == "GET":
                if (_options.OpenSetup is null)
                {
                    Respond(context, 404, """{"error":"setup unavailable"}""");
                    break;
                }

                _options.OpenSetup();
                Respond(context, 200, """{"action":"setup_started"}""");
                break;

            case "/fake-transcript" when method == "POST":
                HandleFakeTranscript(context);
                break;

            case "/control/toggle" when method == "POST":
                // REAL state-machine path — the wiring marshals onto the controller's
                // dispatcher thread, exactly like the tray menu item does.
                _options.Toggle();
                Respond(context, 200, """{"action":"toggle"}""");
                break;

            case "/control/quit" when method == "POST":
                // Respond BEFORE quitting: the quit path stops the message loop and the
                // listener must stay alive long enough to deliver this response.
                Respond(context, 200, """{"action":"quit"}""");
                _options.Quit();
                break;

            case "/status":
            case "/setup":
            case "/fake-transcript":
            case "/control/toggle":
            case "/control/quit":
                Respond(context, 405, $$"""{"error":"method {{method}} not allowed on {{path}}"}""");
                break;

            default:
                Respond(context, 404, $$"""{"error":"not found: {{path}}}""");
                break;
        }
    }

    private void HandleFakeTranscript(HttpListenerContext context)
    {
        if (_options.PresetFakeTranscript is null)
        {
            Respond(context, 404, """{"error":"fake bridge not active"}""");
            return;
        }

        FakeTranscriptRequest? request;
        try
        {
            using MemoryStream buffer = new();
            context.Request.InputStream.CopyTo(buffer);
            request = JsonSerializer.Deserialize<FakeTranscriptRequest>(buffer.ToArray());
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrEmpty(request.text))
        {
            Respond(context, 400, """{"error":"body must be JSON {\"text\": \"...\"} with non-empty text"}""");
            return;
        }

        _options.PresetFakeTranscript(request.text);
        Respond(
            context,
            200,
            JsonSerializer.Serialize(new FakeTranscriptAck(request.text)));
    }

    private string BuildStatusJson()
    {
        StatusPayload payload = new(
            phase: PhaseLabels.ToString(_options.Controller.Phase),
            hudControlValue: _options.Controller.HudPresentation.ControlValue,
            configured: _options.Controller.IsConfigured,
            bridgeReady: _options.Controller.IsBridgeReady,
            lastTranscript: _options.Controller.LastTranscript,
            phaseHistory: SnapshotHistory());
        return JsonSerializer.Serialize(payload);
    }

    private string[] SnapshotHistory()
    {
        lock (_historyLock)
        {
            return _phaseHistory.ToArray();
        }
    }

    private void OnPhaseChanged(AppPhase phase)
    {
        lock (_historyLock)
        {
            _phaseHistory.Add(PhaseLabels.ToString(phase));
        }
    }

    private static void Respond(HttpListenerContext context, int statusCode, string json)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            // Keep-alive OFF: the managed (non-Windows) HttpListener mis-sequences pipelined
            // requests on one pooled connection; a fresh connection per request is robust
            // everywhere and the e2e polls slowly enough that the cost is irrelevant.
            context.Response.KeepAlive = false;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            // The client may have vanished mid-request (e.g. an e2e poll timeout) —
            // a failed response write must never take the accept loop down.
        }
    }

    private static string JsonEscape(string value) => JsonSerializer.Serialize(value)[1..^1];

    public void Dispose()
    {
        _options.Controller.PhaseChanged -= OnPhaseChanged;
        _acceptCts?.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Wire labels for <see cref="AppPhase"/>; error carries its message (mac parity).</summary>
    public static class PhaseLabels
    {
        public static string ToString(AppPhase phase) => phase switch
        {
            AppPhase.LoadingPhase => "loading",
            AppPhase.IdlePhase => "idle",
            AppPhase.RecordingPhase => "recording",
            AppPhase.ProcessingPhase => "processing",
            AppPhase.ErrorPhase error => "error:" + error.Message,
            _ => "unknown",
        };
    }

    // DTOs use the exact wire field names as property names (lowercase on purpose —
    // e2e.ps1 and the contract tests pin these strings).
    private sealed record StatusPayload(
        string phase,
        string hudControlValue,
        bool configured,
        bool bridgeReady,
        string lastTranscript,
        string[] phaseHistory);

    private sealed record FakeTranscriptRequest(string text);

    private sealed record FakeTranscriptAck(string text, string action = "fake_transcript_set");
}

/// <summary>Everything the ControlServer drives. All actions are provided pre-marshalled
/// by the bootstrapper wiring (they must reach the controller on its dispatcher thread).</summary>
public sealed class ControlServerOptions
{
    public required AppController Controller { get; init; }

    public required IShellLog Log { get; init; }

    /// <summary>Same path as the tray menu's Setup… item (degrade-guarded open).</summary>
    public Action? OpenSetup { get; init; }

    /// <summary>Sets the fake bridge's preset text; null when the real engine is active
    /// (then /fake-transcript answers 404).</summary>
    public Action<string>? PresetFakeTranscript { get; init; }

    /// <summary>Drives the REAL <see cref="AppController.ToggleRecording"/> (marshalled).</summary>
    public required Action Toggle { get; init; }

    /// <summary>Graceful quit: REAL <see cref="AppController.ShutdownAsync"/> + message-loop
    /// stop — the tray Exit path, never a state-machine bypass.</summary>
    public required Action Quit { get; init; }

    public int Port { get; init; } = ControlServer.DefaultPort;
}

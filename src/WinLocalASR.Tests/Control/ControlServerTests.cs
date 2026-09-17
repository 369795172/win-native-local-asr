using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Control;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.Audio;
using WinLocalASR.Tests.Shell;
using WinLocalASR.Tests.State;
using Xunit;
using CoreFakeTranscription = WinLocalASR.Core.Control.FakeTranscriptionService;

namespace WinLocalASR.Tests.Control;

/// <summary>
/// Cross-platform ControlServer contract tests (NU1202 precedent: the server lives in
/// Core so these run on macOS; windows-latest runs them for real against http.sys).
/// The /status field names and endpoint semantics here are the wire contract that
/// tests/e2e.ps1 and Task 13's CI step depend on. All Control test classes share one
/// collection: real listeners + real-time sine sources + spinning message loops are
/// exactly the parallel-load shape that starves other collections' timing budgets.
/// </summary>
[Collection("ControlServer")]
public sealed class ControlServerTests : IDisposable
{
    private readonly FakeTime _time = new();
    private readonly FakeTimer _timer;
    private readonly SyncDispatcher _dispatcher = new();
    private readonly FakeAudioCapture _audio;
    private readonly CoreFakeTranscription _transcription;
    private readonly FakeClipboardService _clipboard;
    private readonly FakeSettingsProvider _settings;
    private readonly AppController _controller;
    private readonly FakeShellLog _log = new();
    private readonly HttpClient _http = new();
    private readonly List<ControlServer> _servers = new();
    private readonly List<HttpListener> _occupiers = new();

    public ControlServerTests()
    {
        _timer = new FakeTimer(_time);
        _transcription = new CoreFakeTranscription(delay: TimeSpan.Zero);
        _audio = new FakeAudioCapture
        {
            // The Core fake READS the WAV from StopRecording, so the fake capture must
            // hand back a real (tiny) WAV file, exactly like the sine pipeline does.
            StopResult = Pcm16WavWriter.WriteWavFile(
                Path.Combine(Path.GetTempPath(), $"WinLocalASR-test-{Guid.NewGuid():N}.wav"),
                AudioTestHelpers.SinePcm16(440, 24000, 0.5, 0.25),
                24000,
                channels: 1),
        };
        _clipboard = new FakeClipboardService();
        _settings = new FakeSettingsProvider { Current = new WinLocalASR.Core.Settings.AppSettings { Configured = true } };
        _controller = new AppController(_audio, _transcription, _clipboard, _settings, _timer, _dispatcher, new FakeClock(_time));
    }

    public void Dispose()
    {
        foreach (ControlServer server in _servers)
        {
            server.Dispose();
        }

        foreach (HttpListener occupier in _occupiers)
        {
            occupier.Stop();
            occupier.Close();
        }

        _http.Dispose();
    }

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>A started server on a random port plus its base URL; drives REAL controller paths.</summary>
    private (ControlServer Server, string Base) StartServer(
        Action? openSetup = null,
        Action<string>? presetFakeTranscript = null,
        bool enableFakeBridge = true,
        Action? toggle = null,
        Action? quit = null)
    {
        int port = FreePort();
        ControlServer server = new(new ControlServerOptions
        {
            Controller = _controller,
            Log = _log,
            OpenSetup = openSetup,
            PresetFakeTranscript = enableFakeBridge
                ? presetFakeTranscript ?? (text => _transcription.PresetText = text)
                : null,
            Toggle = toggle ?? (() => _dispatcher.Post(_controller.ToggleRecording)),
            Quit = quit ?? (() => { }),
            Port = port,
        });
        _servers.Add(server);
        server.Start();
        return (server, $"http://localhost:{port}");
    }

    /// <summary>
    /// Bounded /status poll. Even with the synchronous test dispatcher, the fake's
    /// Task.Run(WAV parse) continuation lands on the pool thread (the documented Task 6
    /// settle behavior), so cycle completion is observed by polling, never assumed.
    /// </summary>
    private async Task<JsonElement> PollStatus(string baseUri, Func<JsonElement, bool> condition, int timeoutMs = 10000)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            JsonElement status = await GetJson(baseUri, "/status");
            if (condition(status))
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"status condition not reached on {baseUri} within {timeoutMs}ms");
    }

    private async Task<JsonElement> GetJson(string baseUri, string path)
    {
        using HttpResponseMessage response = await _http.GetAsync(baseUri + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Status_reports_the_exact_contract_field_names_and_startup_values()
    {
        (_, string baseUri) = StartServer();

        JsonElement status = await GetJson(baseUri, "/status");

        string[] actualNames = status.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[] { "bridgeReady", "configured", "hudControlValue", "lastTranscript", "phase", "phaseHistory" },
            actualNames);
        Assert.Equal("loading", status.GetProperty("phase").GetString());
        Assert.Equal("hidden", status.GetProperty("hudControlValue").GetString());
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("bridgeReady").GetBoolean());
        Assert.Equal(string.Empty, status.GetProperty("lastTranscript").GetString());
        Assert.Equal(new[] { "loading" }, status.GetProperty("phaseHistory").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Fake_transcript_preset_then_toggle_flow_over_real_controller()
    {
        (_, string baseUri) = StartServer();
        await _controller.InitializeAsync(); // configured fake → Idle, bridge ready

        using StringContent preset = new("{\"text\": \"你好世界 e2e\"}");
        using HttpResponseMessage presetResponse = await _http.PostAsync(baseUri + "/fake-transcript", preset);
        Assert.Equal(HttpStatusCode.OK, presetResponse.StatusCode);
        Assert.Equal("你好世界 e2e", _transcription.PresetText);

        using HttpResponseMessage firstToggle = await _http.PostAsync(baseUri + "/control/toggle", null);
        Assert.Equal(HttpStatusCode.OK, firstToggle.StatusCode);

        using HttpResponseMessage secondToggle = await _http.PostAsync(baseUri + "/control/toggle", null);
        Assert.Equal(HttpStatusCode.OK, secondToggle.StatusCode);

        JsonElement status = await PollStatus(
            baseUri,
            s => s.GetProperty("phase").GetString() == "idle" &&
                 s.GetProperty("lastTranscript").GetString() == "你好世界 e2e");
        Assert.Equal("你好世界 e2e", status.GetProperty("lastTranscript").GetString());
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("bridgeReady").GetBoolean());
        Assert.Equal(
            new[] { "loading", "idle", "recording", "processing", "idle" },
            status.GetProperty("phaseHistory").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("你好世界 e2e", _clipboard.LastText);
        TimeSpan observed = Assert.Single(_transcription.ObservedDurations);
        Assert.Equal(0.5, observed.TotalSeconds, precision: 2); // the fake really parsed the WAV the capture produced
    }

    [Fact]
    public async Task Hud_control_value_tracks_the_phase_in_status_snapshots()
    {
        (_, string baseUri) = StartServer();
        await _controller.InitializeAsync();
        await _http.PostAsync(baseUri + "/control/toggle", null);

        JsonElement recording = await GetJson(baseUri, "/status");
        Assert.Equal("recording", recording.GetProperty("phase").GetString());
        Assert.Equal("recording", recording.GetProperty("hudControlValue").GetString());

        await _http.PostAsync(baseUri + "/control/toggle", null);

        JsonElement done = await PollStatus(baseUri, s => s.GetProperty("phase").GetString() == "idle");
        Assert.Equal("idle", done.GetProperty("phase").GetString());
        // Within the copied-feedback window (virtual time frozen) the resolved presentation is "copied".
        Assert.Equal("copied", done.GetProperty("hudControlValue").GetString());
    }

    [Fact]
    public async Task Fake_transcript_endpoint_rejects_bad_bodies_and_missing_bridge()
    {
        // Scenario A (active fake bridge): malformed bodies → 400. Scenario B (real
        // engine, no fake wired): any body → 404 for the whole endpoint.
        (_, string bridgeBase) = StartServer();
        using (StringContent malformed = new("not json"))
        using (HttpResponseMessage response = await _http.PostAsync(bridgeBase + "/fake-transcript", malformed))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (StringContent emptyText = new("{\"text\": \"\"}"))
        using (HttpResponseMessage response = await _http.PostAsync(bridgeBase + "/fake-transcript", emptyText))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (StringContent noText = new("{}"))
        using (HttpResponseMessage response = await _http.PostAsync(bridgeBase + "/fake-transcript", noText))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Scenario B: real engine active → no fake bridge.
        (_, string realBase) = StartServer(enableFakeBridge: false);
        using (StringContent anyBody = new("{\"text\": \"x\"}"))
        using (HttpResponseMessage response = await _http.PostAsync(realBase + "/fake-transcript", anyBody))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Setup_endpoint_drives_the_setup_callback_and_reports_started()
    {
        int opens = 0;
        (_, string baseUri) = StartServer(openSetup: () => opens++);

        using HttpResponseMessage response = await _http.GetAsync(baseUri + "/setup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("setup_started", body.GetProperty("action").GetString());
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Quit_endpoint_acknowledges_then_invokes_quit()
    {
        int quits = 0;
        TaskCompletionSource quitReached = new();
        (_, string baseUri) = StartServer(quit: () => { quits++; quitReached.SetResult(); });

        using HttpResponseMessage response = await _http.PostAsync(baseUri + "/control/quit", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("quit", body.GetProperty("action").GetString());
        Assert.True(quitReached.Task.IsCompletedSuccessfully);
        Assert.Equal(1, quits);
    }

    [Fact]
    public async Task Unknown_paths_and_wrong_methods_return_404_and_405()
    {
        (_, string baseUri) = StartServer();

        using (HttpResponseMessage notFound = await _http.GetAsync(baseUri + "/nope"))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        using (HttpResponseMessage wrongMethod = await _http.PostAsync(baseUri + "/status", null))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        }

        using (HttpResponseMessage wrongMethod2 = await _http.GetAsync(baseUri + "/control/toggle"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod2.StatusCode);
        }
    }

    [Fact]
    public async Task QA_Port_conflict_degrades_to_a_warning_and_the_main_graph_keeps_working()
    {
        await _controller.InitializeAsync(); // → Idle: the toggles below need a live machine

        int port = FreePort();
        HttpListener occupier = new();
        occupier.Prefixes.Add($"http://localhost:{port}/");
        occupier.Start();
        _occupiers.Add(occupier);

        ControlServer server = new(new ControlServerOptions
        {
            Controller = _controller,
            Log = _log,
            Toggle = () => { },
            Quit = () => { },
            Port = port,
        });
        _servers.Add(server);

        server.Start(); // must NOT throw

        Assert.False(server.IsListening);
        Assert.Contains(_log.Warnings, w => w.Contains("control server degraded") && w.Contains($"localhost:{port}"));

        // Main functionality unaffected: the real controller still runs a full cycle.
        _dispatcher.Post(_controller.ToggleRecording);
        Assert.True(_controller.Phase is AppPhase.RecordingPhase);
        _dispatcher.Post(_controller.ToggleRecording);
        Assert.True(
            SpinWait.SpinUntil(
                () => _controller.Phase is AppPhase.IdlePhase && _controller.LastTranscript == "hello world",
                10000),
            $"phase stuck at {_controller.Phase}");
    }

    [Fact]
    public async Task Status_unreachable_after_dispose_is_a_fast_transport_error()
    {
        (ControlServer server, string baseUri) = StartServer();
        server.Dispose();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => _http.GetAsync(baseUri + "/status", cts.Token));
    }
}

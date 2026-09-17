using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinLocalASR.Core.Control;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.Shell;
using WinLocalASR.Tests.State;
using Xunit;

namespace WinLocalASR.Tests.Control;

/// <summary>
/// Bootstrapper-level integration: the --enable-control-server / --fake-configured flag
/// wiring (including the plan hard rule that normal startup opens NO socket) and the
/// /control/quit graceful-exit path over the real composition. The tray factory throws —
/// the headless-CI scenario — which the GUI-degrade requirement turns into a warning.
/// </summary>
[Collection("ControlServer")]
public sealed class BootstrapperControlServerTests
{
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static AppBootstrapper CreateBootstrapper(
        IAppServiceGraphFactory realGraph,
        IAppServiceGraphFactory? fakeGraph,
        out FakeMessageLoop loop,
        FakeShellLog log)
    {
        loop = new FakeMessageLoop();
        return new AppBootstrapper(new AppBootstrapperDependencies
        {
            MutexFactory = new FakeMutexFactory(true),
            ServiceGraphFactory = realGraph,
            FakeServiceGraphFactory = fakeGraph,
            TrayShellFactory = new ThrowingTrayFactory(),
            SetupDialogFactory = new FakeSetupDialogFactory(),
            SettingsDialogFactory = new FakeSettingsDialogFactory(),
            MessageLoop = loop,
            Log = log,
        });
    }

    private static FakeServiceGraphFactory FakeGraph() => new(
        resamplerFactory: new PairAveragingResamplerFactory(), // managed resampler: macOS cannot load mfplat.dll
        dispatcher: new SyncDispatcher(),
        fakeDelay: TimeSpan.FromMilliseconds(100));

    private static async Task<JsonElement> PollStatusUntil(
        string baseUri,
        Func<JsonElement, bool> condition,
        int timeoutMs = 15000)
    {
        using HttpClient http = new();
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        string lastBody = "<no response>";
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(baseUri + "/status");
                response.EnsureSuccessStatusCode();
                lastBody = await response.Content.ReadAsStringAsync();
                JsonElement status = JsonDocument.Parse(lastBody).RootElement;
                if (condition(status))
                {
                    return status;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"condition not reached on {baseUri}/status within {timeoutMs}ms; " +
            $"last body: {lastBody}; last transport error: {lastError?.Message ?? "none"}");
    }

    [Fact]
    public async Task Fake_configured_plus_control_server_reaches_idle_and_drives_a_cycle()
    {
        FakeServiceGraphFactory fakeGraph = FakeGraph();
        FakeShellLog log = new();
        int port = FreePort();
        AppBootstrapper bootstrapper = CreateBootstrapper(fakeGraph, fakeGraph, out FakeMessageLoop loop, log);
        loop.BlockUntilStop = true;

        Task<int> run = Task.Run(() => bootstrapper.RunAsync(new[]
        {
            "--enable-control-server",
            $"--control-server-port={port}",
            "--fake-configured",
        }));

        string baseUri = $"http://localhost:{port}";
        using HttpClient http = new();
        try
        {
            JsonElement startup = await PollStatusUntil(baseUri, s => s.GetProperty("phase").GetString() == "idle");
            Assert.True(startup.GetProperty("configured").GetBoolean());
            Assert.Equal(new[] { "loading", "idle" },
                startup.GetProperty("phaseHistory").EnumerateArray().Select(e => e.GetString()).ToArray());

            using (StringContent preset = new("{\"text\": \"你好世界 e2e\"}"))
            using (HttpResponseMessage presetResponse = await http.PostAsync(baseUri + "/fake-transcript", preset))
            {
                presetResponse.EnsureSuccessStatusCode();
            }

            using (HttpResponseMessage toggle = await http.PostAsync(baseUri + "/control/toggle", null))
            {
                toggle.EnsureSuccessStatusCode();
            }

            await PollStatusUntil(baseUri, s => s.GetProperty("phase").GetString() == "recording");
            await Task.Delay(500); // let the real-time sine capture accumulate frames before stopping

            using (HttpResponseMessage toggle = await http.PostAsync(baseUri + "/control/toggle", null))
            {
                toggle.EnsureSuccessStatusCode();
            }

            JsonElement done = await PollStatusUntil(
                baseUri,
                s => s.GetProperty("phase").GetString() == "idle" &&
                     s.GetProperty("lastTranscript").GetString() == "你好世界 e2e");
            Assert.Equal(
                new[] { "loading", "idle", "recording", "processing", "idle" },
                done.GetProperty("phaseHistory").EnumerateArray().Select(e => e.GetString()).ToArray());

            using (HttpResponseMessage quit = await http.PostAsync(baseUri + "/control/quit", null))
            {
                quit.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            loop.Stop();
        }

        int exitCode = await run;
        Assert.Equal(0, exitCode);
        Assert.Contains(log.Infos, m => m.Contains("control server listening"));
    }

    [Fact]
    public async Task Control_quit_stops_the_message_loop_gracefully()
    {
        FakeServiceGraphFactory fakeGraph = FakeGraph();
        int port = FreePort();
        AppBootstrapper bootstrapper = CreateBootstrapper(fakeGraph, fakeGraph, out FakeMessageLoop loop, new FakeShellLog());
        loop.BlockUntilStop = true;

        Task<int> run = Task.Run(() => bootstrapper.RunAsync(new[]
        {
            "--enable-control-server",
            $"--control-server-port={port}",
            "--fake-configured",
        }));

        Assert.True(loop.Entered.Wait(10000), "message loop never started");
        await PollStatusUntil($"http://localhost:{port}", s => s.GetProperty("phase").GetString() == "idle");

        using HttpClient http = new();
        using HttpResponseMessage quit = await http.PostAsync($"http://localhost:{port}/control/quit", null);
        quit.EnsureSuccessStatusCode();

        int exitCode = await run;
        Assert.Equal(0, exitCode);
        Assert.Equal(1, loop.RunCount);
        Assert.Equal(1, loop.StopCount);
    }

    [Fact]
    public async Task Normal_startup_without_the_flag_opens_no_socket()
    {
        FakeServiceGraphFactory fakeGraph = FakeGraph();
        FakeShellLog log = new();
        AppBootstrapper bootstrapper = CreateBootstrapper(fakeGraph, fakeGraph, out _, log);

        int exitCode = await bootstrapper.RunAsync(new[] { "--fake-configured" });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(log.Infos, m => m.Contains("control server"));
        using TcpClient probe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        try
        {
            await probe.ConnectAsync(IPAddress.Loopback, ControlServer.DefaultPort, cts.Token);
            Assert.Fail("something is listening on the default control port without --enable-control-server");
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Fake_configured_without_a_fake_graph_factory_falls_back_with_a_warning()
    {
        ControllerHarness harness = new();
        FakeAppServiceGraphFactory realGraph = new(new AppServiceGraph(harness.Controller, InitiallyConfigured: true));
        FakeShellLog log = new();
        AppBootstrapper bootstrapper = CreateBootstrapper(realGraph, fakeGraph: null, out _, log);

        int exitCode = await bootstrapper.RunAsync(new[] { "--fake-configured" });

        Assert.Equal(0, exitCode);
        Assert.Equal(1, realGraph.CreateCalls);
        Assert.Contains(log.Warnings, w => w.Contains("--fake-configured") && w.Contains("real service graph"));
    }
}

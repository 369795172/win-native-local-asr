using System.Diagnostics;
using System.Net;
using WinLocalASR.Core.Inference;
using Xunit;
using TimeoutException = WinLocalASR.Core.Inference.TimeoutException;

namespace WinLocalASR.Tests.Inference;

public class LlamaServerClientTests
{
    private static readonly LlamaServerPaths FakePaths =
        new("/fake/bin/llama-server.exe", "/fake/models/main.gguf", "/fake/models/mmproj.gguf");

    private static LlamaServerClientOptions FastOptions(FakeLlamaServer server, TimeSpan[]? backoff = null) => new()
    {
        Paths = FakePaths,
        Port = server.Port,
        RestartBackoff = backoff ?? new[] { TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30) },
        StopGracePeriod = TimeSpan.FromMilliseconds(50),
        HealthPollInterval = TimeSpan.FromMilliseconds(20),
        StartupTimeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task QA_plus_ready_handshake_then_transcribe_returns_stripped_text()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);

        await client.StartAsync();
        Assert.True(client.IsReady);

        (string wavPath, byte[] wavBytes) = InferenceWavFile.WriteTempWav(1.0);
        try
        {
            string result = await client.TranscribeAsync(wavPath, "hello world");
            Assert.Equal("hello", result);

            RecordedRequest request = Assert.Single(server.Requests, r => r.Path == "/v1/audio/transcriptions");
            Assert.Equal("POST", request.Method);

            List<MultipartPart> parts = MultipartFormData.Parse(request.ContentType, request.Body);

            MultipartPart file = Assert.Single(parts, p => p.Name == "file");
            Assert.EndsWith(".wav", file.Filename);
            Assert.Equal("audio/wav", file.ContentType);
            Assert.Equal(wavBytes, file.Content);

            MultipartPart prompt = Assert.Single(parts, p => p.Name == "prompt");
            Assert.Equal("hello world", System.Text.Encoding.UTF8.GetString(prompt.Content));

            Assert.DoesNotContain(parts, p => p.Name == "response_format");
            Assert.DoesNotContain(parts, p => p.Name == "model");
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task QA_plus_spawn_arguments_follow_the_pinned_contract()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);

        await client.StartAsync();

        ProcessStartInfo startInfo = Assert.Single(spawner.StartInfos);
        Assert.Equal(FakePaths.LlamaServerExePath, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            new[] { "-m", FakePaths.MainModelPath, "--mmproj", FakePaths.MmprojModelPath, "--host", "127.0.0.1", "--port", server.Port.ToString() },
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task Empty_context_omits_the_prompt_field()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);
        await client.StartAsync();

        (string wavPath, byte[] _) = InferenceWavFile.WriteTempWav(1.0);
        try
        {
            string result = await client.TranscribeAsync(wavPath, "");
            Assert.Equal("hello", result);

            RecordedRequest request = Assert.Single(server.Requests, r => r.Path == "/v1/audio/transcriptions");
            List<MultipartPart> parts = MultipartFormData.Parse(request.ContentType, request.Body);
            Assert.DoesNotContain(parts, p => p.Name == "prompt");
            Assert.Contains(parts, p => p.Name == "file");
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task Transcribe_before_start_throws_NotReady()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        using LlamaServerClient client = new(FastOptions(server));

        (string wavPath, byte[] _) = InferenceWavFile.WriteTempWav(1.0);
        try
        {
            await Assert.ThrowsAsync<NotReadyException>(() => client.TranscribeAsync(wavPath));
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task QA_minus_server_500_throws_RuntimeException_with_status_and_body_summary()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        server.Responder = request => FakeLlamaServer.RequestPath(request) switch
        {
            "/health" => Task.FromResult(new FakeHttpResponse(200, "application/json", "{\"status\":\"ok\"}")),
            "/v1/audio/transcriptions" => Task.FromResult(new FakeHttpResponse(
                500,
                "application/json",
                "{\"error\":{\"message\":\"boom from fake server\",\"type\":\"server_error\"}}")),
            _ => Task.FromResult(new FakeHttpResponse(404, "application/json", "{}")),
        };
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);
        await client.StartAsync();

        (string wavPath, byte[] _) = InferenceWavFile.WriteTempWav(1.0);
        try
        {
            RuntimeException ex = await Assert.ThrowsAsync<RuntimeException>(() => client.TranscribeAsync(wavPath));
            Assert.Equal(500, ex.StatusCode);
            Assert.Contains("boom from fake server", ex.BodySummary);
            Assert.Contains("500", ex.Message);

            // Swift parity: the failed engine is terminated and restarted, the original error rethrown.
            Assert.Equal(2, spawner.SpawnCount);
            Assert.True(client.IsReady);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task QA_minus_slow_server_past_client_timeout_throws_TimeoutException()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        server.Responder = SlowTranscriptionResponder;
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);
        await client.StartAsync();

        (string wavPath, byte[] _) = InferenceWavFile.WriteTempWav(1.0);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(
                () => client.TranscribeAsync(wavPath, timeout: TimeSpan.FromSeconds(1)));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
                $"timeout surfaced after {stopwatch.Elapsed.TotalSeconds:0.0}s, expected ~1s");
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task QA_minus_four_consecutive_exits_exhaust_restart_and_throw_RestartFailed()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);

        await client.StartAsync();
        Assert.True(client.IsReady);

        server.Stop();
        // every relaunch dies before its first health poll iteration — detected via HasExited,
        // so the loop timing never depends on how fast a refused connect fails (OS-specific)
        spawner.Factory = _ =>
        {
            var process = new FakeProcess();
            process.SimulateExit();
            return process;
        };

        var stopwatch = Stopwatch.StartNew();
        FakeProcess initial = spawner.Processes[0];
        initial.SimulateExit(); // the unattended crash that triggers the restart loop

        await Assert.ThrowsAsync<RestartFailedException>(() => client.CrashRecovery);
        stopwatch.Stop();

        // 1 initial launch + 3 restart attempts = 4 processes that all exited
        Assert.Equal(4, spawner.SpawnCount);
        Assert.False(client.IsReady);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"restart loop took {stopwatch.Elapsed.TotalSeconds:0.0}s; backoff must be injectable");
    }

    [Fact]
    public async Task Crash_restart_recovers_when_relaunch_succeeds()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);

        await client.StartAsync();
        FakeProcess initial = spawner.Processes[0];
        initial.SimulateExit(); // server stays up, so the relaunch reaches healthy

        await client.CrashRecovery;
        Assert.True(client.IsReady);
        Assert.Equal(2, spawner.SpawnCount);
    }

    [Fact]
    public async Task Stop_kills_the_process_and_suppresses_auto_restart()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);

        await client.StartAsync();
        FakeProcess initial = spawner.Processes[0];

        var stopwatch = Stopwatch.StartNew();
        await client.StopAsync();
        stopwatch.Stop();

        Assert.True(initial.Killed);
        Assert.False(client.IsReady);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "stop grace period must be injectable");
        Assert.Equal(1, spawner.SpawnCount); // no restart after a deliberate stop
    }

    [Fact]
    public async Task Diagnostics_ring_buffer_keeps_only_the_last_50_lines()
    {
        using FakeLlamaServer server = FakeLlamaServer.Start();
        var spawner = new FakeProcessSpawner();
        using LlamaServerClient client = new(FastOptions(server), spawner: spawner);
        await client.StartAsync();

        FakeProcess process = spawner.Processes[0];
        for (int i = 1; i <= 60; i++)
        {
            process.EmitOutput($"line {i}");
        }

        // 1 lifecycle line + 60 emits = 61 entries; the ring keeps the last 50 ("line 11".."line 60")
        IReadOnlyList<string> diagnostics = client.GetDiagnostics();
        Assert.Equal(50, diagnostics.Count);
        Assert.Equal("line 60", diagnostics[^1]);
        Assert.Contains(diagnostics, d => d == "line 11");
        Assert.DoesNotContain(diagnostics, d => d == "line 10");
    }

    private static async Task<FakeHttpResponse> SlowTranscriptionResponder(HttpListenerRequest request)
    {
        if (FakeLlamaServer.RequestPath(request) == "/v1/audio/transcriptions")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return new FakeHttpResponse(200, "application/json", "{\"type\":\"transcript.text.done\",\"text\":\"late\"}");
        }

        return new FakeHttpResponse(200, "application/json", "{\"status\":\"ok\"}");
    }
}

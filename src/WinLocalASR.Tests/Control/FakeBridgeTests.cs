using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Control;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.Audio;
using WinLocalASR.Tests.State;
using Xunit;
using CoreFakeTranscription = WinLocalASR.Core.Control.FakeTranscriptionService;

namespace WinLocalASR.Tests.Control;

/// <summary>
/// The --fake-configured building blocks: the synthetic sine capture must ride the REAL
/// AudioCaptureManager pipeline (resample → accumulate → WAV write) so a headless runner
/// produces a genuine WAV, and the FakeServiceGraphFactory must reach Idle with zero
/// engine/network. Runs in the shared ControlServer collection (parallel-load control).
/// </summary>
[Collection("ControlServer")]
public sealed class FakeBridgeTests
{
    [Fact]
    public void Sine_source_through_the_real_capture_manager_produces_a_valid_wav()
    {
        using AudioCaptureManager manager = new(new FakeAudioDeviceFactory(), new PairAveragingResamplerFactory());

        manager.StartRecording();
        Thread.Sleep(1200); // real-time source: ~1.2 s of 48 kHz sine
        string wavPath = manager.StopRecording();

        AudioTestHelpers.PcmWavData wav = AudioTestHelpers.ParsePcmWav(File.ReadAllBytes(wavPath));
        Assert.Equal(24000, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.Equal(16, wav.BitsPerSample);
        // 48k→24k pair-averaging halves the frames; tolerate timer jitter generously.
        Assert.InRange(wav.DurationSeconds, 0.7, 2.0);
        File.Delete(wavPath);
    }

    [Fact]
    public void Sine_source_stops_raising_chunks_after_stop_returns()
    {
        SineCaptureSource source = new();
        int raised = 0;
        source.PcmChunkReceived += (_, _) => raised++;
        source.Start();
        Thread.Sleep(350);
        source.Stop();
        int atStop = raised;
        Thread.Sleep(300);
        Assert.True(atStop > 0);
        Assert.Equal(atStop, raised); // no chunk after Stop returned
        source.Dispose();
    }

    [Fact]
    public void Fake_device_factory_lists_the_sine_device()
    {
        FakeAudioDeviceFactory factory = new();
        Assert.Equal(FakeAudioDeviceFactory.DeviceId, Assert.Single(factory.ListInputDevices()).Id);
        Assert.NotNull(factory.OpenCapture(FakeAudioDeviceFactory.DeviceId));
        Assert.Equal(1, factory.OpenCalls);
    }

    [Fact]
    public async Task Fake_transcription_returns_preset_after_delay_and_parses_the_wav()
    {
        string wavPath = Pcm16WavWriter.WriteWavFile(
            Path.Combine(Path.GetTempPath(), $"WinLocalASR-fake-{Guid.NewGuid():N}.wav"),
            AudioTestHelpers.SinePcm16(440, 24000, 2.0, 0.25),
            24000,
            channels: 1);
        CoreFakeTranscription fake = new(delay: TimeSpan.FromMilliseconds(50)) { PresetText = "回来吧" };

        string transcript = await fake.TranscribeAsync(wavPath, null, CancellationToken.None, TimeSpan.FromSeconds(90));

        Assert.Equal("回来吧", transcript);
        Assert.Equal(2.0, Assert.Single(fake.ObservedDurations).TotalSeconds, precision: 2);
        Assert.Equal(wavPath, fake.LastWavPath);
        Assert.Equal(1, fake.TranscribeCalls);
        File.Delete(wavPath);
    }

    [Fact]
    public async Task Fake_transcription_pins_the_one_second_default_delay()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CoreFakeTranscription.DefaultDelay);
        CoreFakeTranscription defaultFake = new();
        Task<string> pending = defaultFake.TranscribeAsync(
            WriteSilentWav(), null, CancellationToken.None, TimeSpan.FromSeconds(90));
        Assert.False(pending.IsCompleted); // the e2e-visible delay is real
        Assert.Equal("hello world", await pending);
    }

    [Fact]
    public async Task Fake_service_graph_reaches_configured_idle_with_zero_network()
    {
        FakeServiceGraphFactory factory = new(
            resamplerFactory: new PairAveragingResamplerFactory(),
            dispatcher: new SyncDispatcher(),
            fakeDelay: TimeSpan.Zero);
        AppServiceGraph graph = factory.Create();

        Assert.True(graph.InitiallyConfigured); // setup is skipped entirely
        Assert.NotNull(graph.Dispatcher);
        Assert.Same(graph.FakeTranscription, factory.FakeTranscription);

        await graph.Controller.InitializeAsync();

        Assert.True(graph.Controller.Phase is AppPhase.IdlePhase);
        Assert.True(graph.Controller.IsConfigured);
        Assert.True(graph.Controller.IsBridgeReady);

        graph.Controller.ToggleRecording();
        Assert.True(graph.Controller.Phase is AppPhase.RecordingPhase);
        Thread.Sleep(400); // let the real-time sine capture accumulate frames
        graph.Controller.ToggleRecording();
        Assert.True(
            SpinWait.SpinUntil(
                () => graph.Controller.Phase is AppPhase.IdlePhase &&
                      graph.Controller.LastTranscript == "hello world",
                10000),
            $"phase stuck at {graph.Controller.Phase}");
        Assert.NotNull(graph.FakeTranscription);
        Assert.Single(graph.FakeTranscription!.ObservedDurations);
    }

    private static string WriteSilentWav()
    {
        return Pcm16WavWriter.WriteWavFile(
            Path.Combine(Path.GetTempPath(), $"WinLocalASR-silent-{Guid.NewGuid():N}.wav"),
            new byte[2400],
            24000,
            channels: 1);
    }
}

/// <summary>
/// Minimal managed PCM16 mono resampler (average each sample pair) standing in for
/// MediaFoundation in cross-platform tests; the CI e2e runs the real MF factory via the
/// production wiring. Shared with the bootstrapper integration tests.
/// </summary>
internal sealed class PairAveragingResamplerFactory : IPcmResamplerFactory
{
    public IPcmResampler Create(AudioFormat sourceFormat, AudioFormat targetFormat) => new PairAveragingResampler();
}

internal sealed class PairAveragingResampler : IPcmResampler
{
    private readonly List<byte> _pending = new();

    public byte[] Process(ReadOnlySpan<byte> pcmChunk)
    {
        foreach (byte b in pcmChunk)
        {
            _pending.Add(b);
        }

        byte[] input = _pending.ToArray();
        int wholeFrames = input.Length / 4; // two 16-bit frames per output frame
        byte[] output = new byte[wholeFrames * 2];
        for (int i = 0; i < wholeFrames; i++)
        {
            short a = BitConverter.ToInt16(input, i * 4);
            short b = BitConverter.ToInt16(input, i * 4 + 2);
            BitConverter.GetBytes((short)((a + b) / 2)).CopyTo(output, i * 2);
        }

        _pending.RemoveRange(0, wholeFrames * 4);
        return output;
    }

    public byte[] Flush()
    {
        byte[] rest = _pending.ToArray();
        _pending.Clear();
        return rest.Length >= 2 ? rest[..^ (rest.Length % 2)] : Array.Empty<byte>();
    }

    public void Dispose()
    {
    }
}

using WinLocalASR.Core.Audio;
using Xunit;

namespace WinLocalASR.Tests.Audio;

public class AudioCaptureManagerTests
{
    private static AudioCaptureManager NewManager(
        IAudioCaptureSource? source,
        IPcmResamplerFactory? resamplerFactory = null)
        => new(new FakeDeviceFactory(source), resamplerFactory);

    [Fact]
    public void StartStop_With440HzSine_Writes24kMonoPcm16WavOfOneSecond()
    {
        byte[] sine = AudioTestHelpers.SinePcm16(440, sampleRate: 24000, seconds: 1.0, amplitude: 0.5);
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        source.EnqueueChunks(sine, chunkBytes: 4800); // 100 ms chunks exercise accumulation
        var levels = new List<float>();
        var manager = new AudioCaptureManager(
            new FakeDeviceFactory(source),
            new ThrowingResamplerFactory()); // identity path must not build a resampler
        manager.OnAudioLevel += levels.Add;

        manager.StartRecording("fake-mic");

        Assert.True(source.Started);
        string path = manager.StopRecording();

        try
        {
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.GetTempPath(), path);
            Assert.StartsWith("WinLocalASR-", Path.GetFileName(path));

            var parsed = AudioTestHelpers.ParsePcmWav(File.ReadAllBytes(path));
            Assert.Equal(24000, parsed.SampleRate);
            Assert.Equal(1, parsed.Channels);
            Assert.Equal(16, parsed.BitsPerSample);
            Assert.InRange(parsed.DurationSeconds, 0.9, 1.1);
            Assert.Equal(sine, parsed.Data); // identity path keeps injected bytes byte-exact
        }
        finally
        {
            File.Delete(path);
        }

        Assert.NotEmpty(levels);
        Assert.All(levels, level => Assert.InRange(level, 0f, 1f));
        Assert.InRange(levels[^1], 0.99f, 1f); // -9 dB sine saturates the (dB+80)/70 scale
    }

    [Fact]
    public void OnAudioLevel_QuietSine_ReportsScaledLevel()
    {
        byte[] quiet = AudioTestHelpers.SinePcm16(440, sampleRate: 24000, seconds: 0.05, amplitude: 0.001);
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        source.EnqueueChunk(quiet);
        float? observed = null;
        var manager = NewManager(source);
        manager.OnAudioLevel += level => observed = level;

        manager.StartRecording();
        manager.CancelRecording();

        Assert.InRange(observed ?? -1f, 0.22f, 0.26f);
    }

    [Fact]
    public void Start_WhenFactoryReturnsNull_ThrowsNoInputDevice()
    {
        var factory = new FakeDeviceFactory(source: null);
        var manager = new AudioCaptureManager(factory);

        Assert.Throws<NoInputDeviceException>(() => manager.StartRecording());
    }

    [Fact]
    public void Stop_WithEmptyBuffer_ThrowsNoAudioCaptured()
    {
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        var manager = NewManager(source);

        manager.StartRecording();

        Assert.Throws<NoAudioCapturedException>(() => manager.StopRecording());
    }

    [Fact]
    public void Stop_WhenNotRecording_ThrowsNotRecording()
    {
        var manager = NewManager(source: null);

        Assert.Throws<NotRecordingException>(() => manager.StopRecording());
    }

    [Fact]
    public void Cancel_DiscardsCapturedAudio_AndLeavesManagerNotRecording()
    {
        byte[] sine = AudioTestHelpers.SinePcm16(440, sampleRate: 24000, seconds: 0.2, amplitude: 0.5);
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        source.EnqueueChunk(sine);
        var manager = NewManager(source);

        manager.StartRecording();
        manager.CancelRecording();

        Assert.Throws<NotRecordingException>(() => manager.StopRecording());
    }

    [Fact]
    public void Cancel_WhenNotRecording_DoesNotThrow()
    {
        var manager = NewManager(source: null);

        manager.CancelRecording(); // Swift cancelRecording silently returns when idle
    }

    [Fact]
    public void Start_WhileAlreadyRecording_IsSilentNoOp()
    {
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        var factory = new FakeDeviceFactory(source);
        var manager = new AudioCaptureManager(factory);

        manager.StartRecording();
        manager.StartRecording();

        Assert.Equal(1, factory.OpenCalls); // second call never re-opened the device
        manager.CancelRecording();
    }

    [Fact]
    public void MaximumDuration_Elapse_InvokesOnMaximumDuration()
    {
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        var manager = NewManager(source);
        manager.MaximumDuration = 0.1;
        var fired = new ManualResetEventSlim(false);
        manager.OnMaximumDuration += fired.Set;

        manager.StartRecording();
        bool observed = fired.Wait(TimeSpan.FromSeconds(3));
        manager.CancelRecording();

        Assert.True(observed);
    }

    [Fact]
    public void StartRecording_UsesSelectedDeviceId_WhenNoArgumentGiven()
    {
        var source = new FakeCaptureSource(new AudioFormat(24000, 1, SampleFormat.Pcm16));
        string? seenDeviceId = null;
        var factory = new FakeDeviceFactory(id =>
        {
            seenDeviceId = id;
            return source;
        });
        var manager = new AudioCaptureManager(factory) { SelectedDeviceId = "preferred-mic" };

        manager.StartRecording();
        manager.CancelRecording();

        Assert.Equal("preferred-mic", seenDeviceId); // Swift startRecording reads selectedDeviceID
    }
}

using WinLocalASR.Core.Audio;
using Xunit;

namespace WinLocalASR.Tests.Audio;

/// <summary>
/// Windows-only runtime coverage (CI windows-latest executes; local macOS skips).
/// MediaFoundationResampler is a DSP transform and needs no audio hardware.
/// </summary>
public class WindowsAudioTests
{
    [SkippableFact]
    public void FullResamplerChain_48kStereoFloat_Produces24kMonoPcm16Wav()
    {
        Skip.If(!OperatingSystem.IsWindows());

        byte[] sine = AudioTestHelpers.SineFloat32Stereo(440, sampleRate: 48000, seconds: 1.0, amplitude: 0.5);
        var source = new FakeCaptureSource(new AudioFormat(48000, 2, SampleFormat.Float32));
        source.EnqueueChunks(sine, chunkBytes: 38_400); // 100 ms stereo float chunks
        var levels = new List<float>();
        var manager = new AudioCaptureManager(new FakeDeviceFactory(source)); // real MF resampler factory
        manager.OnAudioLevel += levels.Add;

        manager.StartRecording();
        string path = manager.StopRecording();

        try
        {
            var parsed = AudioTestHelpers.ParsePcmWav(File.ReadAllBytes(path));
            Assert.Equal(24000, parsed.SampleRate);
            Assert.Equal(1, parsed.Channels);
            Assert.Equal(16, parsed.BitsPerSample);
            Assert.InRange(parsed.DurationSeconds, 0.9, 1.1); // 1 s input +- 100 ms resampler tolerance
        }
        finally
        {
            File.Delete(path);
        }

        Assert.NotEmpty(levels);
        Assert.All(levels, level => Assert.InRange(level, 0f, 1f));
    }

    /// <summary>
    /// Regression for the 2026-09-17 windows-latest e2e crash (0xC0000005 in
    /// IMFTransform.ProcessOutput during the stop-recording drain): a REAL capture
    /// feeds the resampler in small real-time chunks, and NAudio's transform treats
    /// every transient source emptiness as end-of-stream, corrupting the MFT state
    /// until it dies natively. The batch MediaFoundationPcmResampler holds no MFT
    /// state between chunks, so cadence cannot matter by construction; this test pins
    /// the mono PCM16 48k chunked shape that crashed (the e2e sine configuration).
    /// </summary>
    [SkippableFact]
    public void FullResamplerChain_MonoPcm16Chunked_DoesNotCrashAtStop()
    {
        Skip.If(!OperatingSystem.IsWindows());

        byte[] sine = AudioTestHelpers.SinePcm16(440, sampleRate: 48000, seconds: 1.2, amplitude: 0.25);
        var source = new FakeCaptureSource(new AudioFormat(48000, 1, SampleFormat.Pcm16));
        source.EnqueueChunks(sine, chunkBytes: 9_600); // 100 ms mono PCM16 chunks (e2e sine shape)
        var manager = new AudioCaptureManager(new FakeDeviceFactory(source)); // real MF resampler factory

        manager.StartRecording();
        string path = manager.StopRecording();

        try
        {
            var parsed = AudioTestHelpers.ParsePcmWav(File.ReadAllBytes(path));
            Assert.Equal(24000, parsed.SampleRate);
            Assert.Equal(1, parsed.Channels);
            Assert.Equal(16, parsed.BitsPerSample);
            Assert.InRange(parsed.DurationSeconds, 1.1, 1.3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void ListInputDevices_ToleratesMachineWithoutCaptureDevices()
    {
        Skip.If(!OperatingSystem.IsWindows());

        var factory = new WindowsAudioDeviceFactory();
        IReadOnlyList<AudioDeviceInfo> devices = factory.ListInputDevices();

        foreach (AudioDeviceInfo device in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        }

        if (devices.Count == 0)
        {
            Assert.Null(factory.OpenCapture(null)); // CI-runner shape: graceful no-input-device, no throw
        }
    }
}

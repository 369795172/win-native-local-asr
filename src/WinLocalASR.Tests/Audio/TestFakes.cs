using WinLocalASR.Core.Audio;
using Xunit;

namespace WinLocalASR.Tests.Audio;

internal sealed class FakeDeviceFactory : IAudioDeviceFactory
{
    private readonly Func<string?, IAudioCaptureSource?> _open;

    public int OpenCalls { get; private set; }

    public FakeDeviceFactory(IAudioCaptureSource? source)
        => _open = _ => source;

    public FakeDeviceFactory(Func<string?, IAudioCaptureSource?> open)
        => _open = open;

    public IReadOnlyList<AudioDeviceInfo> Devices { get; init; } = Array.Empty<AudioDeviceInfo>();

    public IReadOnlyList<AudioDeviceInfo> ListInputDevices() => Devices;

    public IAudioCaptureSource? OpenCapture(string? deviceId)
    {
        OpenCalls++;
        return _open(deviceId);
    }
}

internal sealed class FakeCaptureSource : IAudioCaptureSource
{
    private readonly List<byte[]> _chunks = new();

    public FakeCaptureSource(AudioFormat format) => Format = format;

    public AudioFormat Format { get; }

    public bool Started { get; private set; }

    public event EventHandler<ReadOnlyMemory<byte>>? PcmChunkReceived;

    public void EnqueueChunk(byte[] pcm) => _chunks.Add(pcm);

    public void EnqueueChunks(byte[] pcm, int chunkBytes)
    {
        for (int offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            int length = Math.Min(chunkBytes, pcm.Length - offset);
            _chunks.Add(pcm[offset..(offset + length)]);
        }
    }

    public void Start()
    {
        Started = true;
        foreach (byte[] chunk in _chunks)
        {
            PcmChunkReceived?.Invoke(this, chunk);
        }
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>Guards the identity path: same-format input must never build a resampler.</summary>
internal sealed class ThrowingResamplerFactory : IPcmResamplerFactory
{
    public IPcmResampler Create(AudioFormat sourceFormat, AudioFormat targetFormat)
        => throw new InvalidOperationException("Identity path must not create a resampler.");
}

internal static class AudioTestHelpers
{
    public static byte[] SinePcm16(double frequencyHz, int sampleRate, double seconds, double amplitude)
    {
        int frames = (int)(sampleRate * seconds);
        var bytes = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)Math.Clamp(
                Math.Round(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate)),
                short.MinValue,
                short.MaxValue);
            BitConverter.GetBytes(sample).CopyTo(bytes, i * 2);
        }
        return bytes;
    }

    public static byte[] SineFloat32Stereo(double frequencyHz, int sampleRate, double seconds, double amplitude)
    {
        int frames = (int)(sampleRate * seconds);
        var bytes = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            float sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
            BitConverter.GetBytes(sample).CopyTo(bytes, i * 8);
            BitConverter.GetBytes(sample).CopyTo(bytes, i * 8 + 4);
        }
        return bytes;
    }

    public sealed record PcmWavData(int SampleRate, int Channels, int BitsPerSample, byte[] Data)
    {
        public double DurationSeconds => Data.Length / (double)(SampleRate * Channels * BitsPerSample / 8);
    }

    /// <summary>Byte-level RIFF/PCM parser: ffprobe-equivalent structural assertions.</summary>
    public static PcmWavData ParsePcmWav(byte[] wav)
    {
        Assert.True(wav.Length >= 44);
        Assert.Equal("RIFF"u8.ToArray(), wav[0..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal("fmt "u8.ToArray(), wav[12..16]);
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM (uncompressed)
        short channels = BitConverter.ToInt16(wav, 22);
        int sampleRate = BitConverter.ToInt32(wav, 24);
        int byteRate = BitConverter.ToInt32(wav, 28);
        short blockAlign = BitConverter.ToInt16(wav, 32);
        short bitsPerSample = BitConverter.ToInt16(wav, 34);
        Assert.Equal(sampleRate * channels * bitsPerSample / 8, byteRate);
        Assert.Equal(channels * bitsPerSample / 8, blockAlign);
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        int dataLength = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataLength);
        return new PcmWavData(sampleRate, channels, bitsPerSample, wav[44..]);
    }
}

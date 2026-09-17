using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WinLocalASR.Core.Audio;

public sealed class MediaFoundationResamplerFactory : IPcmResamplerFactory
{
    public IPcmResampler Create(AudioFormat sourceFormat, AudioFormat targetFormat)
        => new MediaFoundationPcmResampler(sourceFormat, targetFormat);
}

/// <summary>
/// MediaFoundation-backed streaming resampler (pure DSP: no audio hardware needed, but
/// Windows-only at runtime). Float32 input is converted to PCM16 in managed code first,
/// so MediaFoundation only has to resample PCM16 -> PCM16 (rate/channels).
/// </summary>
internal sealed class MediaFoundationPcmResampler : IPcmResampler
{
    private readonly BufferedWaveProvider _sourceBuffer;
    private readonly MediaFoundationResampler _resampler;
    private readonly bool _sourceIsFloat;
    private bool _disposed;

    internal MediaFoundationPcmResampler(AudioFormat source, AudioFormat target)
    {
        if (source.Format == SampleFormat.Float32)
        {
            _sourceIsFloat = true;
        }
        else if (source.Format != SampleFormat.Pcm16)
        {
            throw new NotSupportedException($"Unsupported source sample format: {source.Format}");
        }

        _sourceBuffer = new BufferedWaveProvider(new WaveFormat(source.SampleRate, 16, source.Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(20),
            DiscardOnBufferOverflow = true,
        };
        _resampler = new MediaFoundationResampler(
            _sourceBuffer,
            new WaveFormat(target.SampleRate, target.BitsPerSample, target.Channels))
        {
            ResamplerQuality = 60,
        };
    }

    public byte[] Process(ReadOnlySpan<byte> pcmChunk)
    {
        if (_sourceIsFloat)
        {
            pcmChunk = ConvertFloat32ToPcm16(pcmChunk);
        }

        if (pcmChunk.Length == 0)
        {
            return Array.Empty<byte>();
        }

        _sourceBuffer.AddSamples(pcmChunk.ToArray(), 0, pcmChunk.Length);
        return ReadAvailable(drainTail: false);
    }

    public byte[] Flush() => ReadAvailable(drainTail: true);

    private byte[] ReadAvailable(bool drainTail)
    {
        if (!drainTail && _sourceBuffer.BufferedBytes == 0)
        {
            return Array.Empty<byte>(); // never hand an exhausted source to the MFT mid-stream
        }

        using var output = new MemoryStream();
        var scratch = new byte[16_384];
        int read;
        while ((read = _resampler.Read(scratch, 0, scratch.Length)) > 0)
        {
            output.Write(scratch, 0, read);
        }
        return output.ToArray();
    }

    private ReadOnlySpan<byte> ConvertFloat32ToPcm16(ReadOnlySpan<byte> floatChunk)
    {
        ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(floatChunk);
        var pcm = new short[floats.Length];
        for (int i = 0; i < floats.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(Math.Round(floats[i] * 32768f), short.MinValue, short.MaxValue);
        }
        return MemoryMarshal.Cast<short, byte>(pcm);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _resampler.Dispose();
    }
}

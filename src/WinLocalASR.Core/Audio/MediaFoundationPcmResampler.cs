using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WinLocalASR.Core.Audio;

public sealed class MediaFoundationResamplerFactory : IPcmResamplerFactory
{
    public IPcmResampler Create(AudioFormat sourceFormat, AudioFormat targetFormat)
        => new MediaFoundationPcmResampler(sourceFormat, targetFormat);
}

/// <summary>
/// MediaFoundation-backed resampler (pure DSP: no audio hardware needed, but Windows-only
/// at runtime). Float32 input is converted to PCM16 in managed code first, so
/// MediaFoundation only resamples PCM16 -> PCM16 (rate/channels).
///
/// BATCH DESIGN (why Process returns no bytes): NAudio's MediaFoundationTransform.Read
/// treats a transiently EMPTY pull source as end-of-stream -- it drains the MFT and
/// resets its sample-time bookkeeping every time its source read returns zero
/// (NAudio 2.2.1, MediaFoundationTransform.Read: ReadFromSource() == null ->
/// EndStreamAndDrain). With a real-time chunk cadence (capture buffers arriving every
/// ~10-100 ms, consumed faster than they arrive) that fires between nearly all chunks;
/// the transform is then fed post-drain input with reset sample timestamps and
/// eventually dies with a native access violation (0xC0000005 in ProcessOutput, first
/// observed on the windows-latest e2e at the stop-recording drain; see docs/working.md
/// 2026-09-17). Chunks are therefore only converted and buffered here; the SINGLE MFT
/// pass happens in <see cref="Flush"/> over the whole recording, where the source has
/// one real end -- the canonical, battle-tested NAudio file-resampling shape.
/// </summary>
internal sealed class MediaFoundationPcmResampler : IPcmResampler
{
    private readonly WaveFormat _sourceWaveFormat; // PCM16 view of the source
    private readonly WaveFormat _targetWaveFormat;
    private readonly bool _sourceIsFloat;
    private MemoryStream _pending = new();
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

        _sourceWaveFormat = new WaveFormat(source.SampleRate, 16, source.Channels);
        _targetWaveFormat = new WaveFormat(target.SampleRate, target.BitsPerSample, target.Channels);
    }

    public byte[] Process(ReadOnlySpan<byte> pcmChunk)
    {
        if (_sourceIsFloat)
        {
            pcmChunk = ConvertFloat32ToPcm16(pcmChunk);
        }

        if (pcmChunk.Length > 0)
        {
            _pending.Write(pcmChunk);
        }

        return Array.Empty<byte>(); // batch: all output arrives once, at Flush
    }

    public byte[] Flush()
    {
        if (_pending.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] input = _pending.ToArray();
        _pending.Dispose();
        _pending = new MemoryStream();

        using var sourceStream = new RawSourceWaveStream(new MemoryStream(input), _sourceWaveFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, _targetWaveFormat)
        {
            ResamplerQuality = 60,
        };
        using var output = new MemoryStream();
        var scratch = new byte[64 * 1024];
        int read;
        while ((read = resampler.Read(scratch, 0, scratch.Length)) > 0)
        {
            output.Write(scratch, 0, read);
        }

        return output.ToArray();
    }

    private static ReadOnlySpan<byte> ConvertFloat32ToPcm16(ReadOnlySpan<byte> floatChunk)
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
        _pending.Dispose();
        _pending = new MemoryStream();
    }
}

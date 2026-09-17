namespace WinLocalASR.Core.Audio;

/// <summary>
/// Streaming PCM converter: feeds chunks in the source format, gets back bytes in the
/// target format. Abstracts MediaFoundationResampler so the pipeline is testable.
/// </summary>
public interface IPcmResampler : IDisposable
{
    /// <summary>Converts one chunk; may return fewer/more bytes than the input ratio implies.</summary>
    byte[] Process(ReadOnlySpan<byte> pcmChunk);

    /// <summary>Drains internally buffered samples; call after the last chunk.</summary>
    byte[] Flush();
}

public interface IPcmResamplerFactory
{
    IPcmResampler Create(AudioFormat sourceFormat, AudioFormat targetFormat);
}

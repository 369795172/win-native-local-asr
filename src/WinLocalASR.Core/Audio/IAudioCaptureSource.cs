namespace WinLocalASR.Core.Audio;

/// <summary>
/// A started capture device delivering raw PCM chunks in <see cref="Format"/>.
/// Chunks are interleaved, frame-aligned, and raised on the capture thread.
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    AudioFormat Format { get; }

    event EventHandler<ReadOnlyMemory<byte>>? PcmChunkReceived;

    void Start();

    /// <summary>Synchronously stops capture; after it returns no more chunks are raised.</summary>
    void Stop();
}

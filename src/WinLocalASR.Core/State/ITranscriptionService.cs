namespace WinLocalASR.Core.State;

/// <summary>
/// Narrow slice of <c>Inference.LlamaServerClient</c> consumed by the state machine (Swift
/// ASRBridgeClient). <see cref="TranscribeAsync"/> carries the explicit timeout that Swift
/// computes via transcriptionTimeoutSeconds(for:) and passes to transcribe(timeout:).
/// </summary>
public interface ITranscriptionService
{
    bool IsReady { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    Task<string> TranscribeAsync(string wavPath, string? context, CancellationToken cancellationToken, TimeSpan timeout);
}

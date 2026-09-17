namespace WinLocalASR.Core.Inference;

/// <summary>
/// Per-transcription timeout, ported from the pinned anchor decision (docs/rfc.md §Inference
/// Contract): max(90, 3 × durationSeconds + 20) seconds.
/// </summary>
public static class TranscriptionTimeout
{
    public static TimeSpan Compute(TimeSpan audioDuration)
    {
        double seconds = Math.Max(90, 3 * audioDuration.TotalSeconds + 20);
        return TimeSpan.FromSeconds(seconds);
    }
}

namespace WinLocalASR.Core.Hud;

/// <summary>
/// Pure value transforms for the HUD level bars and the recording progress capsule,
/// ported from Swift <c>HUDLevelBars.barHeight(index:count:time:maximum:)</c>
/// (DictationHUD.swift lines 218-234) and <c>recordingProgress</c> (lines 156-159).
/// The WinForms view owns the 20 fps repaint; these functions are stateless.
/// </summary>
public static class HudMetrics
{
    /// <summary>Swift bar count.</summary>
    public const int BarCount = 9;

    /// <summary>Swift minimum bar height (points; scaled by DPI in the view).</summary>
    public const double MinimumBarHeight = 4;

    /// <summary>Swift recording-mode amplitude floor: max(0.12, min(1, level)).</summary>
    public const double MinimumAmplitude = 0.12;

    /// <summary>Swift processing sweep speed: position advances 6 bars per second.</summary>
    public const double ProcessingBarsPerSecond = 6;

    /// <summary>Swift processing falloff distance in bars.</summary>
    public const double ProcessingFalloffBars = 2.5;

    private static readonly double[] RecordingPattern =
        { 0.35, 0.55, 0.8, 0.62, 1, 0.68, 0.86, 0.52, 0.38 };

    /// <summary>
    /// Recording bars: static per-bar pattern scaled by the clamped audio level.
    /// Amplitude = clamp(level, 0.12, 1); height = max(4, maximum × amplitude × pattern[i]).
    /// </summary>
    public static double[] RecordingBarHeights(float level, double maximum, int count = BarCount)
    {
        double amplitude = Math.Clamp((double)level, MinimumAmplitude, 1d);
        double[] heights = new double[count];
        for (int i = 0; i < count; i++)
        {
            heights[i] = Math.Max(MinimumBarHeight, maximum * amplitude * RecordingPattern[i % RecordingPattern.Length]);
        }

        return heights;
    }

    /// <summary>
    /// Processing bars: a sweeping "spinner" — a bright position travels through the
    /// ring of bars at <see cref="ProcessingBarsPerSecond"/> bars/second with a linear
    /// falloff over <see cref="ProcessingFalloffBars"/> bars. Distance wraps around the
    /// ring; intensity = max(0, 1 - distance / 2.5); height = max(4, maximum × (0.2 + intensity × 0.8)).
    /// </summary>
    public static double[] ProcessingBarHeights(double elapsedSeconds, double maximum, int count = BarCount)
    {
        double[] heights = new double[count];
        double position = (elapsedSeconds * ProcessingBarsPerSecond) % count;
        for (int i = 0; i < count; i++)
        {
            double distance = Math.Abs(i - position);
            distance = Math.Min(distance, count - distance); // ring wraparound
            double intensity = Math.Max(0d, 1d - distance / ProcessingFalloffBars);
            heights[i] = Math.Max(MinimumBarHeight, maximum * (0.2 + intensity * 0.8));
        }

        return heights;
    }

    /// <summary>Swift recordingProgress: clamp(duration / limit, 0, 1); 0 when limit &lt;= 0.</summary>
    public static double RecordingProgress(double durationSeconds, double limitSeconds)
    {
        if (limitSeconds <= 0)
        {
            return 0;
        }

        return Math.Clamp(durationSeconds / limitSeconds, 0d, 1d);
    }
}

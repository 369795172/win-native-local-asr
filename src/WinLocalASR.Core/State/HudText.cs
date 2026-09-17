using System.Globalization;

namespace WinLocalASR.Core.State;

/// <summary>Swift formatHUDDuration (AppState.swift lines 5-8): mm:ss, clamped at zero.</summary>
public static class HudText
{
    public static string FormatHudDuration(TimeSpan duration)
    {
        long totalSeconds = Math.Max(0, (long)duration.TotalSeconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}",
            totalSeconds / 60,
            totalSeconds % 60);
    }
}

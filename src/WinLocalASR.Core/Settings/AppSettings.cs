namespace WinLocalASR.Core.Settings;

/// <summary>
/// User-facing application settings, serialized as JSON at
/// <c>%APPDATA%\WinLocalASR\settings.json</c> (base directory injectable via
/// <see cref="SettingsStore"/>). This file is the SSOT for autostart: the HKCU Run
/// key is derived state; the installer never edits the JSON (see
/// <see cref="SettingsStore.MergePendingAutoStartFlag"/>).
/// </summary>
public sealed record AppSettings
{
    public const string DefaultHotkey = "Ctrl+Shift+Space";
    public const int DefaultRecordingLimitSeconds = 120;
    public const int MinRecordingLimitSeconds = 10;
    public const int MaxRecordingLimitSeconds = 120;

    /// <summary>Serialized hotkey string (format owned by the hotkey manager).</summary>
    public string Hotkey { get; init; } = DefaultHotkey;

    /// <summary>Selected input device id; null = system default device.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Optional context/hotwords prompt passed to the recognizer.</summary>
    public string? ContextPrompt { get; init; }

    /// <summary>Recording limit in seconds, clamped to [10, 120].
    /// The 120 s cap is a deliberate guardrail (llama.cpp #21847, >2 min audio).</summary>
    public int RecordingLimitSeconds { get; init; } = DefaultRecordingLimitSeconds;

    /// <summary>Launch at login. SSOT here; HKCU Run key is derived.</summary>
    public bool AutoStart { get; init; }

    /// <summary>First-run setup (model download) completed.</summary>
    public bool Configured { get; init; }

    public static AppSettings Clamp(AppSettings settings) => settings with
    {
        RecordingLimitSeconds = ClampRecordingLimit(settings.RecordingLimitSeconds),
    };

    public static int ClampRecordingLimit(int seconds) =>
        Math.Clamp(seconds, MinRecordingLimitSeconds, MaxRecordingLimitSeconds);
}

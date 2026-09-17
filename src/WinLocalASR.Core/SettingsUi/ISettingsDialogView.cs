using WinLocalASR.Core.Audio;

namespace WinLocalASR.Core.SettingsUi;

/// <summary>
/// Semantic hint kinds the presenter raises to the view. Deliberately
/// payload-less: Core stays free of user-visible text and the view maps each
/// kind to a localized string in WinLocalASR.Resources.
/// </summary>
public enum SettingsHint
{
    /// <summary>Capture started: press a combination; Esc cancels.</summary>
    HotkeyCaptureActive,

    /// <summary>Capture was cancelled; the previous combination stays in effect.</summary>
    HotkeyCaptureCancelled,

    /// <summary>A pressed key had no modifier; capture continues.</summary>
    HotkeyNeedsModifier,

    /// <summary>The stored hotkey string could not be parsed; the default was restored.</summary>
    HotkeyInvalidReset,

    /// <summary>The requested recording limit was clamped to the 10-120 s guardrail.</summary>
    RecordingLimitClamped,

    /// <summary>Apply completed successfully.</summary>
    Applied,

    /// <summary>Apply threw; the detail carries the exception message.</summary>
    ApplyFailed,
}

/// <summary>
/// View surface implemented by the WinForms SettingsDialog. All methods are
/// invoked synchronously on the UI thread (the presenter has no background
/// work), following the ISetupDialogView pattern.
/// </summary>
public interface ISettingsDialogView
{
    void SetHotkey(string hotkey);

    void SetContextPrompt(string contextPrompt);

    void SetRecordingLimit(int seconds);

    void SetAutoStart(bool enabled);

    /// <summary>Replaces the device list; null/empty selection = system default.</summary>
    void SetDevices(IReadOnlyList<AudioDeviceInfo> devices, string? selectedDeviceId);

    void ShowHint(SettingsHint hint, string? detail = null);

    void ClearHint();
}

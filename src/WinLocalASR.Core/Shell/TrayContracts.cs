using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Shell;

/// <summary>Tray icon variants, swapped on phase changes (Swift menuBarSymbol mapping).</summary>
public enum TrayIconKind
{
    /// <summary>Loading and Idle share the neutral glyph (Swift: both use waveform.circle).</summary>
    Idle,

    Recording,

    Processing,

    Error,
}

/// <summary>Localized-equivalent of Swift statusText's phase word.</summary>
public enum TrayPhaseStatus
{
    Loading,

    Ready,

    Recording,

    Transcribing,

    Error,
}

/// <summary>Localized-equivalent of Swift modelStatusText's engine state.</summary>
public enum TrayEngineStatus
{
    LoadingModel,

    ModelLoaded,

    NotConfigured,

    Error,
}

/// <summary>
/// Structured tray status: the disabled menu row shows phase + engine state combined,
/// the tooltip shows the phase part (Swift statusText / modelStatusText). Core stays
/// text-free — the WinForms view maps these to WinLocalASR.Resources strings.
/// </summary>
public sealed record TrayStatus(TrayPhaseStatus Phase, string? PhaseDetail, TrayEngineStatus Engine, string? EngineDetail);

/// <summary>
/// Live registration state of the user global hotkey (Task 8 hotkey manager).
/// The settings presenter reads it to render a conflict; the tray balloons on the
/// transition into Conflict.
/// </summary>
public enum HotkeyRegistrationStatus
{
    /// <summary>No registration attempt yet, or no hotkey manager wired (headless/tests).</summary>
    Pending,

    /// <summary>The user hotkey is registered and live.</summary>
    Registered,

    /// <summary>Registration failed — the combination is already held by another
    /// program. The previously live combination (if any) is kept.</summary>
    Conflict,
}

/// <summary>Shell logging seam. The tray app has no console; the real implementation writes a
/// best-effort log file. The GUI-degrade hard requirement needs this: degradation must be
/// observable, not silent.
/// </summary>
public interface IShellLog
{
    void Info(string message);

    void Warn(string message);
}

/// <summary>View contract implemented by the WinForms TrayShell; fakes record calls in tests.</summary>
public interface ITrayShell : IDisposable
{
    event Action? SetupRequested;

    event Action? SettingsRequested;

    event Action? CopyLastTranscriptRequested;

    event Action? RestartEngineRequested;

    event Action? ExitRequested;

    /// <summary>Fires when the context menu is about to open — the presenter refreshes polled state.</summary>
    event Action? MenuOpening;

    void SetIcon(TrayIconKind icon);

    void SetPhaseTooltip(TrayPhaseStatus phase, string? detail);

    void SetStatus(TrayStatus status);

    void SetSetupHighlighted(bool highlighted);

    void SetCopyLastTranscriptEnabled(bool enabled);

    void ShowErrorBalloon(string message);
}

/// <summary>Opens the six-step first-run setup dialog (Task 10) bound to a controller.</summary>
public interface ISetupDialogFactory
{
    void Open(AppController controller);
}

/// <summary>Opens the settings dialog (Task 11) bound to a controller.</summary>
public interface ISettingsDialogFactory
{
    void Open(AppController controller);
}

/// <summary>
/// Creates and wires the tray. Implementations may throw when the environment cannot host
/// one (e.g. a headless CI runner with no Explorer shell) — the bootstrapper catches that
/// and keeps running headless, which is the GUI-degrade hard requirement.
/// </summary>
public interface ITrayShellFactory
{
    IDisposable CreateTray(
        AppController controller,
        ISetupDialogFactory setupDialogFactory,
        ISettingsDialogFactory settingsDialogFactory,
        Action requestExit);
}

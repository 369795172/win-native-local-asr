using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Shell;

/// <summary>
/// Tray menu logic (Task 7): subscribes <see cref="AppController.PhaseChanged"/> and drives
/// the <see cref="ITrayShell"/> view — icon swap, tooltip (Swift statusText), the disabled
/// status row (phase + engine state), the Setup highlight while unconfigured, copy-enabled
/// following the last transcript, and an error balloon on transitions INTO the Error phase.
/// Every menu action routes to the controller: Setup/Settings via the dialog factories,
/// copy-last via <see cref="AppController.CopyLastTranscript"/>, restart via
/// <see cref="AppController.RestartBridge"/>, Exit via <see cref="AppController.ShutdownAsync"/>
/// then the injected exit callback. Presentation text lives in the WinForms view
/// (Core stays text-free, Task 11 precedent); the ported Swift helpers statusText /
/// modelStatusText / menuBarSymbol surface here as the Tray* enums and <see cref="TrayStatus"/>.
/// </summary>
public sealed class TrayShellPresenter : IDisposable
{
    private readonly AppController _controller;
    private readonly ITrayShell _tray;
    private readonly ISetupDialogFactory _setupDialogFactory;
    private readonly ISettingsDialogFactory _settingsDialogFactory;
    private readonly Action _requestExit;
    private readonly IShellLog _log;

    public TrayShellPresenter(
        AppController controller,
        ITrayShell tray,
        ISetupDialogFactory setupDialogFactory,
        ISettingsDialogFactory settingsDialogFactory,
        Action requestExit,
        IShellLog log)
    {
        _controller = controller;
        _tray = tray;
        _setupDialogFactory = setupDialogFactory;
        _settingsDialogFactory = settingsDialogFactory;
        _requestExit = requestExit;
        _log = log;

        _tray.SetupRequested += OnSetupRequested;
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.CopyLastTranscriptRequested += OnCopyLastTranscriptRequested;
        _tray.RestartEngineRequested += OnRestartEngineRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.MenuOpening += OnMenuOpening;
        _controller.PhaseChanged += OnPhaseChanged;

        // Apply the current state without a transition balloon (mirrors the Mac menu label
        // rendering whatever phase exists at tray creation).
        Refresh();
    }

    /// <summary>Re-applies icon/tooltip/status/polled menu state (menu-open hook).</summary>
    public void Refresh() => ApplyState();

    public void Dispose()
    {
        _tray.SetupRequested -= OnSetupRequested;
        _tray.SettingsRequested -= OnSettingsRequested;
        _tray.CopyLastTranscriptRequested -= OnCopyLastTranscriptRequested;
        _tray.RestartEngineRequested -= OnRestartEngineRequested;
        _tray.ExitRequested -= OnExitRequested;
        _tray.MenuOpening -= OnMenuOpening;
        _controller.PhaseChanged -= OnPhaseChanged;
        _tray.Dispose();
    }

    private void OnPhaseChanged(AppPhase phase)
    {
        ApplyState();
        if (phase is AppPhase.ErrorPhase error)
        {
            _tray.ShowErrorBalloon(error.Message);
        }
    }

    private void ApplyState()
    {
        AppPhase phase = _controller.Phase;
        _tray.SetIcon(IconFor(phase));
        _tray.SetPhaseTooltip(PhaseFor(phase), PhaseDetailFor(phase));
        _tray.SetStatus(BuildStatus(phase));
        _tray.SetSetupHighlighted(!_controller.IsConfigured);
        _tray.SetCopyLastTranscriptEnabled(_controller.LastTranscript.Length > 0);
    }

    private void OnSetupRequested()
    {
        try
        {
            _setupDialogFactory.Open(_controller);
        }
        catch (Exception ex)
        {
            _log.Warn($"setup dialog failed to open: {ex.Message}");
        }
    }

    private void OnSettingsRequested()
    {
        try
        {
            _settingsDialogFactory.Open(_controller);
        }
        catch (Exception ex)
        {
            _log.Warn($"settings dialog failed to open: {ex.Message}");
        }
    }

    private void OnCopyLastTranscriptRequested() => _controller.CopyLastTranscript();

    private void OnRestartEngineRequested() => _controller.RestartBridge();

    private void OnExitRequested() => _ = ShutdownAndExitAsync();

    private async Task ShutdownAndExitAsync()
    {
        try
        {
            await _controller.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"engine shutdown failed: {ex.Message}");
        }
        finally
        {
            _requestExit();
        }
    }

    private void OnMenuOpening() => Refresh();

    // ---- Swift presentation helpers, enum-shaped (view maps to localized strings) ----

    private static TrayIconKind IconFor(AppPhase phase) => phase switch
    {
        AppPhase.RecordingPhase => TrayIconKind.Recording,
        AppPhase.ProcessingPhase => TrayIconKind.Processing,
        AppPhase.ErrorPhase => TrayIconKind.Error,
        _ => TrayIconKind.Idle, // Loading + Idle (Swift menuBarSymbol shares the glyph)
    };

    private static TrayPhaseStatus PhaseFor(AppPhase phase) => phase switch
    {
        AppPhase.LoadingPhase => TrayPhaseStatus.Loading,
        AppPhase.IdlePhase => TrayPhaseStatus.Ready,
        AppPhase.RecordingPhase => TrayPhaseStatus.Recording,
        AppPhase.ProcessingPhase => TrayPhaseStatus.Transcribing,
        AppPhase.ErrorPhase => TrayPhaseStatus.Error,
        _ => throw new InvalidOperationException($"unreachable phase: {phase}"),
    };

    /// <summary>Swift statusText: Idle shows lastAction when present, else the plain ready word.</summary>
    private string? PhaseDetailFor(AppPhase phase) => phase switch
    {
        AppPhase.IdlePhase => _controller.LastAction.Length == 0 ? null : _controller.LastAction,
        AppPhase.ErrorPhase error => error.Message,
        _ => null,
    };

    private TrayStatus BuildStatus(AppPhase phase) => new(
        PhaseFor(phase),
        PhaseDetailFor(phase),
        EngineStatusFor(phase),
        phase is AppPhase.ErrorPhase error ? error.Message : null);

    /// <summary>Swift modelStatusText: Loading shows the loading word; Error carries its message;
    /// everything else reflects bridge readiness. No IsBridgeReady event exists — the value is
    /// read here on every refresh (phase change + menu open), the documented polling pattern.</summary>
    private TrayEngineStatus EngineStatusFor(AppPhase phase) => phase switch
    {
        AppPhase.LoadingPhase => TrayEngineStatus.LoadingModel,
        AppPhase.ErrorPhase => TrayEngineStatus.Error,
        _ => _controller.IsBridgeReady ? TrayEngineStatus.ModelLoaded : TrayEngineStatus.NotConfigured,
    };
}

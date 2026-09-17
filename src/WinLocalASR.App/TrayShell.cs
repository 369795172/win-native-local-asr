using WinLocalASR.Core.Shell;
using WinLocalASR.Resources;

namespace WinLocalASR.App;

/// <summary>
/// WinForms tray shell (Task 7): NotifyIcon + ContextMenuStrip with the menu surface
/// ported from the macOS MenuView — a disabled status row (phase + engine state), Setup…
/// (bold while unconfigured, opens the Task 10 dialog), Settings… (Task 11 dialog),
/// Copy last transcript, Restart engine, Exit. Implements the Core
/// <see cref="ITrayShell"/> contract; <see cref="TrayShellPresenter"/> owns the logic.
/// This class maps Tray* enums to localized <see cref="Strings"/> resources and owns the
/// four phase icons (embedded Assets\tray-*.ico) — the icon swap, tooltip (= Swift
/// statusText) and error balloon happen here on presenter command.
/// </summary>
internal sealed class TrayShell : IDisposable, ITrayShell
{
    private const int MaxTooltipLength = 63; // NotifyIcon.Text limit

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _setupItem;
    private readonly ToolStripMenuItem _copyItem;
    private readonly IReadOnlyDictionary<TrayIconKind, Icon> _icons;
    private bool _disposed;

    public event Action? SetupRequested;
    public event Action? SettingsRequested;
    public event Action? CopyLastTranscriptRequested;
    public event Action? RestartEngineRequested;
    public event Action? ExitRequested;
    public event Action? MenuOpening;

    public TrayShell(IShellLog log)
    {
        _icons = TrayIconSet.Load(log);

        ContextMenuStrip menu = new();
        _statusItem = new ToolStripMenuItem { Enabled = false };
        ToolStripMenuItem settingsItem = new(Strings.Tray_Settings);
        _setupItem = new ToolStripMenuItem(Strings.Tray_Setup);
        _copyItem = new ToolStripMenuItem(Strings.Tray_CopyLastTranscript);
        ToolStripMenuItem restartItem = new(Strings.Tray_RestartEngine);
        ToolStripMenuItem exitItem = new(Strings.Tray_Exit);

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_setupItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_copyItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _setupItem.Click += (_, _) => SetupRequested?.Invoke();
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        _copyItem.Click += (_, _) => CopyLastTranscriptRequested?.Invoke();
        restartItem.Click += (_, _) => RestartEngineRequested?.Invoke();
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Opening += (_, _) => MenuOpening?.Invoke();

        _statusItem.Text = Strings.Tray_LoadingModel;

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _icons[TrayIconKind.Idle],
            Text = "WinLocalASR",
            Visible = true,
        };
    }

    public void SetIcon(TrayIconKind icon) => _notifyIcon.Icon = _icons[icon];

    public void SetPhaseTooltip(TrayPhaseStatus phase, string? detail)
    {
        string text = PhaseText(phase, detail);
        _notifyIcon.Text = text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    public void SetStatus(TrayStatus status) =>
        _statusItem.Text = $"{PhaseText(status.Phase, status.PhaseDetail)} · {EngineText(status.Engine, status.EngineDetail)}";

    public void SetSetupHighlighted(bool highlighted) =>
        _setupItem.Font = new Font(_setupItem.Font, highlighted ? FontStyle.Bold : FontStyle.Regular);

    public void SetCopyLastTranscriptEnabled(bool enabled) => _copyItem.Enabled = enabled;

    public void ShowErrorBalloon(string message) =>
        _notifyIcon.ShowBalloonTip(3000, "WinLocalASR", message, ToolTipIcon.Error);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        foreach (Icon icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    // ---- enum → localized text (Task 6 left these presentation helpers to the shell) ----

    private static string PhaseText(TrayPhaseStatus phase, string? detail) => phase switch
    {
        TrayPhaseStatus.Loading => Strings.Tray_LoadingModel,
        TrayPhaseStatus.Ready => detail ?? Strings.Tray_Ready,
        TrayPhaseStatus.Recording => Strings.Tray_Recording,
        TrayPhaseStatus.Transcribing => Strings.Tray_Transcribing,
        TrayPhaseStatus.Error => detail is null ? Strings.Tray_Error : $"{Strings.Tray_Error}: {detail}",
        _ => Strings.Tray_Error,
    };

    private static string EngineText(TrayEngineStatus engine, string? detail) => engine switch
    {
        TrayEngineStatus.LoadingModel => Strings.Tray_LoadingModel,
        TrayEngineStatus.ModelLoaded => Strings.Tray_ModelLoaded,
        TrayEngineStatus.NotConfigured => Strings.Tray_NotConfigured,
        TrayEngineStatus.Error => detail is null ? Strings.Tray_Error : $"{Strings.Tray_Error}: {detail}",
        _ => Strings.Tray_NotConfigured,
    };
}

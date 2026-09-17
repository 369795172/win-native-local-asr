using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Hotkeys;

/// <summary>
/// Port of the MacLocalASR hotkey flow (AppState.swift lines 113-124 +
/// HotkeyManager.swift): RegisterHotKey id=1 is the user toggle hotkey (settings
/// string parsed by <see cref="HotkeyString"/>; the <see cref="HotkeyModifiers"/>
/// flags ARE the MOD_* constants and <see cref="HotkeyKey"/> values ARE the VK
/// codes, so both cast straight to the registration parameters). Id=2 is a bare
/// Esc that is registered ONLY while a recording is active and unregistered the
/// instant it ends — replicating <c>setCancelShortcutActive</c>: a permanently
/// registered Esc would be swallowed system-wide while the app is idle.
///
/// Esc availability comes exclusively from
/// <see cref="AppController.CancelHotkeyAvailabilityChanged"/> (wired by the
/// shell) — never inferred from the phase here; future phases like Processing
/// stay unregistered by construction. WM_HOTKEY messages arrive on the
/// message-loop thread; controller calls always go through <see cref="IDispatcher"/>
/// (the @MainActor stand-in), never directly from the WndProc thread.
///
/// Threading contract: single-threaded like the Swift @MainActor original — every
/// method runs on the UI thread (registration, controller events and the
/// settings-Apply hook all arrive there).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const int UserHotkeyId = 1;
    public const int CancelHotkeyId = 2;

    /// <summary>VK_ESCAPE — the cancel key is hardcoded, not user-configurable.</summary>
    public const uint CancelVirtualKey = 0x1B;

    /// <summary>Bare Esc: no modifiers.</summary>
    public const uint CancelModifiers = 0;

    private static readonly HotkeyCombination DefaultCombination =
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyKey.Space);

    private readonly IHotkeyRegistrar _registrar;
    private readonly IDispatcher _dispatcher;
    private readonly Func<string> _hotkeyStringProvider;
    private readonly Action _toggleRecording;
    private readonly Action _cancelActiveRecording;
    private readonly IShellLog _log;
    private readonly Action<string>? _showErrorBalloon;

    private HotkeyCombination _liveCombination = DefaultCombination;
    private bool _userHotkeyLive;
    private bool _cancelLive;
    private HotkeyRegistrationStatus _status = HotkeyRegistrationStatus.Pending;
    private bool _disposed;

    public HotkeyManager(
        IHotkeyRegistrar registrar,
        IDispatcher dispatcher,
        Func<string> hotkeyStringProvider,
        Action toggleRecording,
        Action cancelActiveRecording,
        IShellLog log,
        Action<string>? showErrorBalloon = null)
    {
        _registrar = registrar;
        _dispatcher = dispatcher;
        _hotkeyStringProvider = hotkeyStringProvider;
        _toggleRecording = toggleRecording;
        _cancelActiveRecording = cancelActiveRecording;
        _log = log;
        _showErrorBalloon = showErrorBalloon;
    }

    /// <summary>Live state of the USER hotkey registration; the settings dialog reads
    /// this to render a conflict (settings presenter contract).</summary>
    public HotkeyRegistrationStatus Status => _status;

    public event Action<HotkeyRegistrationStatus>? StatusChanged;

    /// <summary>True while the recording-scoped Esc is registered (test observability).</summary>
    public bool IsCancelHotkeyRegistered => _cancelLive;

    /// <summary>
    /// Startup registration of the user hotkey from the settings string. A hotkey
    /// held by another program surfaces as <see cref="HotkeyRegistrationStatus.Conflict"/>
    /// plus a tray balloon — never an exception (QA-: registration failure must not
    /// crash the shell).
    /// </summary>
    public void Initialize()
    {
        HotkeyCombination requested = ResolveCombination();
        if (_registrar.Register(UserHotkeyId, (uint)requested.Modifiers, (uint)(int)requested.Key))
        {
            _liveCombination = requested;
            _userHotkeyLive = true;
            SetStatus(HotkeyRegistrationStatus.Registered);
            _log.Info($"global hotkey registered: {HotkeyString.Serialize(requested)}");
            return;
        }

        _userHotkeyLive = false;
        ReportUserHotkeyFailure(requested);
    }

    /// <summary>
    /// One WM_HOTKEY from the message window: id=1 posts the toggle action, id=2 the
    /// cancel action, unknown ids are ignored. The WndProc thread never touches the
    /// controller directly — both route through the dispatcher seam.
    /// </summary>
    public void HandleWmHotkey(int id)
    {
        switch (id)
        {
            case UserHotkeyId:
                _dispatcher.Post(_toggleRecording);
                break;
            case CancelHotkeyId:
                _dispatcher.Post(_cancelActiveRecording);
                break;
        }
    }

    /// <summary>
    /// Swift <c>setCancelShortcutActive</c>: registers or releases the bare-Esc
    /// cancel hotkey. Deduplicated — a permanently-registered Esc would eat the key
    /// system-wide; availability events are only ever true/false pairs around a
    /// recording. Esc registration failure is logged but not ballooned: mid-recording
    /// balloons would stomp the HUD, and the toggle hotkey remains a working exit.
    /// </summary>
    public void SetCancelHotkeyActive(bool active)
    {
        if (_disposed || active == _cancelLive)
        {
            return;
        }

        if (active)
        {
            if (_registrar.Register(CancelHotkeyId, CancelModifiers, CancelVirtualKey))
            {
                _cancelLive = true;
            }
            else
            {
                _log.Warn("cancel hotkey (Esc) registration failed; Esc will not cancel the active recording");
            }

            return;
        }

        // A false return means "not registered" — treat the bookkeeping as released.
        if (!_registrar.Unregister(CancelHotkeyId))
        {
            _log.Warn("cancel hotkey (Esc) unregister reported not-registered; assuming released");
        }

        _cancelLive = false;
    }

    /// <summary>
    /// Settings-Apply path: re-registers id=1 with the (already persisted) settings
    /// combination. Graceful ordering: unregister the old combination, register the
    /// new one; on conflict the OLD combination is re-registered so the user keeps a
    /// working hotkey, and the conflict is surfaced (status + balloon + log).
    /// </summary>
    public void ApplyHotkeyFromSettings()
    {
        if (_disposed)
        {
            return;
        }

        HotkeyCombination requested = ResolveCombination();
        if (_userHotkeyLive && requested.Equals(_liveCombination))
        {
            return; // unchanged — the live registration already matches settings
        }

        HotkeyCombination previous = _liveCombination;
        bool hadPrevious = _userHotkeyLive;
        if (hadPrevious)
        {
            _registrar.Unregister(UserHotkeyId);
            _userHotkeyLive = false;
        }

        if (_registrar.Register(UserHotkeyId, (uint)requested.Modifiers, (uint)(int)requested.Key))
        {
            _liveCombination = requested;
            _userHotkeyLive = true;
            SetStatus(HotkeyRegistrationStatus.Registered);
            _log.Info($"global hotkey re-registered: {HotkeyString.Serialize(requested)}");
            return;
        }

        if (hadPrevious
            && _registrar.Register(UserHotkeyId, (uint)previous.Modifiers, (uint)(int)previous.Key))
        {
            // The requested combination is occupied; the previous one is live again.
            _liveCombination = previous;
            _userHotkeyLive = true;
        }

        ReportUserHotkeyFailure(requested);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_cancelLive)
        {
            _registrar.Unregister(CancelHotkeyId);
            _cancelLive = false;
        }

        if (_userHotkeyLive)
        {
            _registrar.Unregister(UserHotkeyId);
            _userHotkeyLive = false;
        }
    }

    private HotkeyCombination ResolveCombination()
    {
        string raw;
        try
        {
            raw = _hotkeyStringProvider();
        }
        catch (Exception ex)
        {
            _log.Warn($"settings read for the hotkey failed, using the default: {ex.Message}");
            return DefaultCombination;
        }

        if (HotkeyString.TryParse(raw, out HotkeyCombination combination))
        {
            return combination;
        }

        // Hand-edited/corrupt string: the settings dialog also falls back visibly;
        // the manager degrades to the default instead of failing registration.
        _log.Warn($"hotkey string '{raw}' is unparseable, using the default");
        return DefaultCombination;
    }

    private void ReportUserHotkeyFailure(HotkeyCombination requested)
    {
        SetStatus(HotkeyRegistrationStatus.Conflict);
        string message =
            $"'{HotkeyString.Serialize(requested)}' is already in use by another program";
        _log.Warn($"hotkey registration failed: {message}");
        _showErrorBalloon?.Invoke(message);
    }

    private void SetStatus(HotkeyRegistrationStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(status);
    }
}

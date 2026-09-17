using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Shell;

namespace WinLocalASR.Core.SettingsUi;

/// <summary>
/// Presenter between the settings dialog view and the persistence/runtime
/// surfaces (SetupDialogPresenter pattern: thin view, all logic here,
/// synchronous on the UI thread, no WinForms dependency).
///
/// Owns: the hotkey capture state machine (Esc = cancel and restore, never
/// clear), recording-limit clamping to the 10-120 s guardrail (llama.cpp
/// #21847), device-list refresh with dropped-selection fallback, and the
/// Apply path — which writes the autostart SSOT pair together (HKCU Run key
/// via <see cref="AutoStartHelper"/> AND <c>AutoStart</c> in settings.json via
/// <see cref="SettingsStore.Save"/>) and then invokes the injected
/// apply-without-reload callback (the shell wires
/// <c>AppController.ApplySettingsWithoutReload</c>: hotwords/limit/device
/// changes take effect without a model reload).
/// </summary>
public sealed class SettingsDialogPresenter
{
    private readonly SettingsStore _store;
    private readonly IRegistryKeyFactory _registryFactory;
    private readonly string _exePath;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _deviceLister;
    private readonly Action? _applyWithoutReload;
    private readonly Func<HotkeyRegistrationStatus>? _hotkeyStatusProvider;
    private readonly ISettingsDialogView _view;

    private string _hotkey = AppSettings.DefaultHotkey;
    private string? _contextPrompt;
    private string? _selectedDeviceId;
    private int _recordingLimitSeconds = AppSettings.DefaultRecordingLimitSeconds;
    private bool _autoStart;
    private bool _capturingHotkey;

    public SettingsDialogPresenter(
        SettingsStore store,
        IRegistryKeyFactory registryFactory,
        string exePath,
        Func<IReadOnlyList<AudioDeviceInfo>> deviceLister,
        ISettingsDialogView view,
        Action? applyWithoutReload = null,
        Func<HotkeyRegistrationStatus>? hotkeyStatusProvider = null)
    {
        _store = store;
        _registryFactory = registryFactory;
        _exePath = exePath;
        _deviceLister = deviceLister;
        _view = view;
        _applyWithoutReload = applyWithoutReload;
        _hotkeyStatusProvider = hotkeyStatusProvider;
    }

    public bool IsCapturingHotkey => _capturingHotkey;

    public string Hotkey => _hotkey;

    /// <summary>Normalized: null means the system default device.</summary>
    public string? SelectedDeviceId => string.IsNullOrEmpty(_selectedDeviceId) ? null : _selectedDeviceId;

    public string? ContextPrompt => string.IsNullOrEmpty(_contextPrompt) ? null : _contextPrompt;

    public int RecordingLimitSeconds => _recordingLimitSeconds;

    public bool AutoStart => _autoStart;

    /// <summary>Live user-hotkey registration state (Task 8). No provider wired →
    /// Pending (tests / hotkey shell degraded).</summary>
    public HotkeyRegistrationStatus HotkeyRegistrationStatus =>
        _hotkeyStatusProvider?.Invoke() ?? HotkeyRegistrationStatus.Pending;

    /// <summary>Populates every view field from SettingsStore + the device list.</summary>
    public void Load()
    {
        AppSettings settings = _store.Load();
        if (HotkeyString.TryParse(settings.Hotkey, out HotkeyCombination _))
        {
            _hotkey = settings.Hotkey;
        }
        else
        {
            // Hand-edited or corrupted hotkey string: fall back to the default,
            // visibly, instead of persisting an unparseable value.
            _hotkey = AppSettings.DefaultHotkey;
            _view.ShowHint(SettingsHint.HotkeyInvalidReset);
        }

        _contextPrompt = settings.ContextPrompt;
        _selectedDeviceId = string.IsNullOrEmpty(settings.DeviceId) ? null : settings.DeviceId;
        _recordingLimitSeconds = settings.RecordingLimitSeconds;
        _autoStart = settings.AutoStart;

        _view.SetHotkey(_hotkey);
        _view.SetContextPrompt(_contextPrompt ?? "");
        _view.SetRecordingLimit(_recordingLimitSeconds);
        _view.SetAutoStart(_autoStart);
        RefreshDevices();

        if (HotkeyRegistrationStatus == HotkeyRegistrationStatus.Conflict)
        {
            // The conflict may originate at startup, not in this dialog session.
            _view.ShowHint(SettingsHint.HotkeyConflict);
        }
    }

    /// <summary>Re-enumerates input devices; a stored selection that no longer
    /// exists falls back to the system default rather than a dead id.</summary>
    public void RefreshDevices()
    {
        IReadOnlyList<AudioDeviceInfo> devices = _deviceLister();
        if (_selectedDeviceId is not null && !devices.Any(d => d.Id == _selectedDeviceId))
        {
            _selectedDeviceId = null;
        }

        _view.SetDevices(devices, _selectedDeviceId);
    }

    // ---- Hotkey capture state machine ----

    public void BeginHotkeyCapture()
    {
        _capturingHotkey = true;
        _view.ShowHint(SettingsHint.HotkeyCaptureActive);
    }

    /// <summary>Explicit cancel (Esc key or focus loss while capturing):
    /// restores the previous combination — never clears it.</summary>
    public void CancelHotkeyCapture()
    {
        if (!_capturingHotkey)
        {
            return;
        }

        _capturingHotkey = false;
        _view.SetHotkey(_hotkey);
        _view.ShowHint(SettingsHint.HotkeyCaptureCancelled);
    }

    /// <summary>One KeyDown during capture. Escape cancels; a key without any
    /// modifier is rejected (hint) while capture continues; modifiers + key
    /// commits the serialized combination. Key presses while not capturing are
    /// ignored.</summary>
    public void HotkeyKeyPressed(HotkeyModifiers modifiers, HotkeyKey key)
    {
        if (!_capturingHotkey)
        {
            return;
        }

        if (key == HotkeyKey.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            _view.ShowHint(SettingsHint.HotkeyNeedsModifier);
            return;
        }

        _capturingHotkey = false;
        _hotkey = HotkeyString.Serialize(modifiers, key);
        _view.SetHotkey(_hotkey);
        _view.ClearHint();
    }

    // ---- Field changes pushed by the view ----

    public void ContextPromptChanged(string value) => _contextPrompt = value;

    public void RecordingLimitChanged(int seconds)
    {
        int clamped = AppSettings.ClampRecordingLimit(seconds);
        if (clamped != seconds)
        {
            _view.ShowHint(SettingsHint.RecordingLimitClamped);
            _view.SetRecordingLimit(clamped);
        }

        _recordingLimitSeconds = clamped;
    }

    public void DeviceSelected(string? deviceId) =>
        _selectedDeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;

    public void AutoStartChanged(bool enabled) => _autoStart = enabled;

    // ---- Apply ----

    /// <summary>
    /// Persists settings and applies them without a model reload. Ordering:
    /// Run key first (the flakier write — on failure nothing is saved and the
    /// old state stays consistent), then settings.json (SSOT, preserving
    /// fields the dialog does not own, e.g. <c>Configured</c>), then the
    /// apply-without-reload callback. Returns false with an ApplyFailed hint
    /// on error.
    /// </summary>
    public bool Apply()
    {
        try
        {
            AutoStartHelper.Apply(_registryFactory, _autoStart, _exePath);

            AppSettings current = _store.Load();
            _store.Save(current with
            {
                Hotkey = _hotkey,
                DeviceId = SelectedDeviceId,
                ContextPrompt = ContextPrompt,
                RecordingLimitSeconds = _recordingLimitSeconds,
                AutoStart = _autoStart,
            });

            _applyWithoutReload?.Invoke();
        }
        catch (Exception ex)
        {
            _view.ShowHint(SettingsHint.ApplyFailed, ex.Message);
            return false;
        }

        _view.ShowHint(SettingsHint.Applied);
        return true;
    }
}

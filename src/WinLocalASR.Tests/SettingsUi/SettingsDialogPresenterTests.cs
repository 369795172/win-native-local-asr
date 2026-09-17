using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.SettingsUi;
using Xunit;

namespace WinLocalASR.Tests.SettingsUi;

/// <summary>
/// Presenter logic tests (view-side state machine, no WinForms): load/clamp/
/// capture/apply semantics against a real SettingsStore over a temp base dir
/// (never real %APPDATA%) and an in-memory registry fake.
/// </summary>
public class SettingsDialogPresenterTests : IDisposable
{
    private readonly string _baseDir = NewTempDir();
    private readonly FakeRegistryFactory _registry = new();
    private readonly DeviceListerFake _devices = new(
        new AudioDeviceInfo("dev-1", "Mic A"),
        new AudioDeviceInfo("dev-2", "Headset"),
        new AudioDeviceInfo("dev-3", "USB Mic"));
    private readonly FakeSettingsView _view = new();
    private int _applyWithoutReloadCalls;

    private const string ExePath = @"C:\somewhere\WinLocalASR.exe";

    private SettingsDialogPresenter NewPresenter() => new(
        new SettingsStore(_baseDir),
        _registry,
        ExePath,
        _devices.List,
        _view,
        () => _applyWithoutReloadCalls++);

    [Fact]
    public void Load_populates_every_view_field_from_settings()
    {
        new SettingsStore(_baseDir).Save(new AppSettings
        {
            Hotkey = "Ctrl+Alt+K",
            DeviceId = "dev-2",
            ContextPrompt = "热词 hello",
            RecordingLimitSeconds = 45,
            AutoStart = true,
            Configured = true,
        });

        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        Assert.Equal(new[] { "Ctrl+Alt+K" }, _view.Hotkeys);
        Assert.Equal(new[] { "热词 hello" }, _view.ContextPrompts);
        Assert.Equal(new[] { 45 }, _view.RecordingLimits);
        Assert.Equal(new[] { true }, _view.AutoStarts);
        (IReadOnlyList<AudioDeviceInfo> devices, string? selected) = _view.DeviceSets.Single();
        Assert.Equal(3, devices.Count);
        Assert.Equal("dev-2", selected);
        Assert.Empty(_view.Hints);
    }

    [Fact]
    public void Load_restores_the_default_hotkey_with_a_hint_when_stored_string_is_invalid()
    {
        new SettingsStore(_baseDir).Save(new AppSettings { Hotkey = "Nonsense" });

        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        Assert.Equal(AppSettings.DefaultHotkey, presenter.Hotkey);
        Assert.Equal(AppSettings.DefaultHotkey, _view.Hotkeys.Single());
        Assert.Contains(SettingsHint.HotkeyInvalidReset, _view.Hints.Select(h => h.Hint));
    }

    [Fact]
    public void Apply_roundtrips_every_field_through_the_settings_store()
    {
        new SettingsStore(_baseDir).Save(new AppSettings { Configured = true });

        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        presenter.BeginHotkeyCapture();
        presenter.HotkeyKeyPressed(HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyKey.F7);
        presenter.ContextPromptChanged(" llama.cpp 语音识别 ");
        presenter.RecordingLimitChanged(30);
        presenter.DeviceSelected("dev-3");
        presenter.AutoStartChanged(true);

        Assert.True(presenter.Apply());
        Assert.Equal(1, _applyWithoutReloadCalls);
        Assert.Contains(SettingsHint.Applied, _view.Hints.Select(h => h.Hint));

        AppSettings reloaded = new SettingsStore(_baseDir).Load();
        Assert.Equal("Ctrl+Alt+F7", reloaded.Hotkey);
        Assert.Equal("dev-3", reloaded.DeviceId);
        Assert.Equal(" llama.cpp 语音识别 ", reloaded.ContextPrompt);
        Assert.Equal(30, reloaded.RecordingLimitSeconds);
        Assert.True(reloaded.AutoStart);
        Assert.True(reloaded.Configured); // fields the dialog does not own survive

        Assert.Equal(ExePath, _registry.Values[AutoStartHelper.RunValueName]); // SSOT pair: Run key written too
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(999, 120)]
    [InlineData(-5, 10)]
    [InlineData(121, 120)]
    public void Out_of_range_limits_clamp_to_the_guardrail_and_raise_a_hint(int requested, int expected)
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        presenter.RecordingLimitChanged(requested);

        Assert.Equal(expected, presenter.RecordingLimitSeconds);
        Assert.Equal(expected, _view.RecordingLimits.Last());
        Assert.DoesNotContain(requested, _view.RecordingLimits); // only the clamped value reaches the view
        Assert.Contains(SettingsHint.RecordingLimitClamped, _view.Hints.Select(h => h.Hint));
    }

    [Fact]
    public void In_range_limits_do_not_raise_hints()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        presenter.RecordingLimitChanged(10);
        presenter.RecordingLimitChanged(120);
        presenter.RecordingLimitChanged(60);

        Assert.Equal(60, presenter.RecordingLimitSeconds);
        Assert.DoesNotContain(SettingsHint.RecordingLimitClamped, _view.Hints.Select(h => h.Hint));
    }

    [Fact]
    public void Esc_during_capture_cancels_and_preserves_the_previous_hotkey()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();
        Assert.Equal(AppSettings.DefaultHotkey, presenter.Hotkey);

        presenter.BeginHotkeyCapture();
        presenter.HotkeyKeyPressed(HotkeyModifiers.None, HotkeyKey.Escape);

        Assert.False(presenter.IsCapturingHotkey);
        Assert.Equal(AppSettings.DefaultHotkey, presenter.Hotkey);
        Assert.Equal(AppSettings.DefaultHotkey, _view.Hotkeys.Last()); // restored, never cleared
        Assert.Contains(SettingsHint.HotkeyCaptureCancelled, _view.Hints.Select(h => h.Hint));

        // A key press after the cancelled capture is ignored (not capturing).
        presenter.HotkeyKeyPressed(HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyKey.B);
        Assert.Equal(AppSettings.DefaultHotkey, presenter.Hotkey);
    }

    [Fact]
    public void Captured_combination_serializes_into_the_settings_string()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        presenter.BeginHotkeyCapture();
        presenter.HotkeyKeyPressed(HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyKey.Space);

        Assert.False(presenter.IsCapturingHotkey);
        Assert.Equal("Ctrl+Shift+Space", presenter.Hotkey);
        Assert.Equal("Ctrl+Shift+Space", _view.Hotkeys.Last());
    }

    [Fact]
    public void Modifierless_key_is_rejected_with_a_hint_while_capture_continues()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        presenter.BeginHotkeyCapture();
        presenter.HotkeyKeyPressed(HotkeyModifiers.None, HotkeyKey.B);

        Assert.True(presenter.IsCapturingHotkey);
        Assert.Contains(SettingsHint.HotkeyNeedsModifier, _view.Hints.Select(h => h.Hint));

        presenter.HotkeyKeyPressed(HotkeyModifiers.Control, HotkeyKey.B);
        Assert.False(presenter.IsCapturingHotkey);
        Assert.Equal("Ctrl+B", presenter.Hotkey);
    }

    [Fact]
    public void CancelHotkeyCapture_without_an_active_capture_is_a_noop()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();

        presenter.CancelHotkeyCapture();

        Assert.False(presenter.IsCapturingHotkey);
        Assert.DoesNotContain(SettingsHint.HotkeyCaptureCancelled, _view.Hints.Select(h => h.Hint));
    }

    [Fact]
    public void RefreshDevices_reenumerates_and_drops_a_missing_selection()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();
        presenter.DeviceSelected("dev-2");

        _devices.Set(new AudioDeviceInfo("dev-1", "Mic A"));
        presenter.RefreshDevices();

        Assert.Equal(2, _devices.CallCount);
        Assert.Null(presenter.SelectedDeviceId);
        (_, string? selected) = _view.DeviceSets.Last();
        Assert.Null(selected);
    }

    [Fact]
    public void RefreshDevices_keeps_the_selection_when_the_device_still_exists()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();
        presenter.DeviceSelected("dev-2");

        _devices.Set(
            new AudioDeviceInfo("dev-2", "Headset"),
            new AudioDeviceInfo("dev-4", "New Mic"));
        presenter.RefreshDevices();

        Assert.Equal("dev-2", presenter.SelectedDeviceId);
        (_, string? selected) = _view.DeviceSets.Last();
        Assert.Equal("dev-2", selected);
    }

    [Fact]
    public void Empty_device_selection_normalizes_to_null_in_persisted_settings()
    {
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();
        presenter.DeviceSelected("");

        Assert.True(presenter.Apply());

        Assert.Null(new SettingsStore(_baseDir).Load().DeviceId);
    }

    [Fact]
    public void Apply_with_autostart_unchecked_deletes_the_run_key_value()
    {
        _registry.Values[AutoStartHelper.RunValueName] = ExePath;
        new SettingsStore(_baseDir).Save(new AppSettings { AutoStart = true });

        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();
        presenter.AutoStartChanged(false);

        Assert.True(presenter.Apply());

        Assert.False(_registry.Values.ContainsKey(AutoStartHelper.RunValueName));
        Assert.False(new SettingsStore(_baseDir).Load().AutoStart);
    }

    [Fact]
    public void Apply_failure_returns_false_surfaces_the_hint_and_preserves_persisted_state()
    {
        _registry.ThrowOnWrite = true;
        SettingsDialogPresenter presenter = NewPresenter();
        presenter.Load();
        presenter.AutoStartChanged(true);

        Assert.False(presenter.Apply());
        Assert.Equal(0, _applyWithoutReloadCalls);
        (SettingsHint hint, string? detail) = _view.Hints.Last();
        Assert.Equal(SettingsHint.ApplyFailed, hint);
        Assert.NotNull(detail);

        // Run key first: on failure nothing was saved, the old state stays consistent.
        Assert.False(new SettingsStore(_baseDir).Load().AutoStart);
    }

    public void Dispose() => DeleteDir(_baseDir);

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winlocalasr-settingsui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class DeviceListerFake(params AudioDeviceInfo[] initial)
    {
        private IReadOnlyList<AudioDeviceInfo> _devices = initial;

        public int CallCount { get; private set; }

        public void Set(params AudioDeviceInfo[] devices) => _devices = devices;

        public IReadOnlyList<AudioDeviceInfo> List()
        {
            CallCount++;
            return _devices;
        }
    }

    private sealed class FakeRegistryFactory : IRegistryKeyFactory
    {
        public Dictionary<string, object?> Values { get; } = new();

        public bool ThrowOnWrite { get; set; }

        public IRegistryKey OpenRunKey() => new FakeKey(this);

        private sealed class FakeKey(FakeRegistryFactory owner) : IRegistryKey
        {
            public object? GetValue(string name) =>
                owner.Values.TryGetValue(name, out object? value) ? value : null;

            public void SetValue(string name, object value)
            {
                if (owner.ThrowOnWrite)
                {
                    throw new IOException("registry unavailable (fake)");
                }

                owner.Values[name] = value;
            }

            public void DeleteValue(string name, bool throwOnMissingValue)
            {
                if (!owner.Values.Remove(name) && throwOnMissingValue)
                {
                    throw new ArgumentException("value not found (fake)");
                }
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSettingsView : ISettingsDialogView
    {
        private readonly object _gate = new();
        private List<string> _hotkeys = new();
        private List<string> _contextPrompts = new();
        private List<int> _recordingLimits = new();
        private List<bool> _autoStarts = new();
        private List<(IReadOnlyList<AudioDeviceInfo> Devices, string? Selected)> _deviceSets = new();
        private List<(SettingsHint Hint, string? Detail)> _hints = new();

        public IReadOnlyList<string> Hotkeys
        {
            get { lock (_gate) return _hotkeys.ToList(); }
        }

        public IReadOnlyList<string> ContextPrompts
        {
            get { lock (_gate) return _contextPrompts.ToList(); }
        }

        public IReadOnlyList<int> RecordingLimits
        {
            get { lock (_gate) return _recordingLimits.ToList(); }
        }

        public IReadOnlyList<bool> AutoStarts
        {
            get { lock (_gate) return _autoStarts.ToList(); }
        }

        public IReadOnlyList<(IReadOnlyList<AudioDeviceInfo> Devices, string? Selected)> DeviceSets
        {
            get { lock (_gate) return _deviceSets.ToList(); }
        }

        public IReadOnlyList<(SettingsHint Hint, string? Detail)> Hints
        {
            get { lock (_gate) return _hints.ToList(); }
        }

        public void SetHotkey(string hotkey)
        {
            lock (_gate) _hotkeys.Add(hotkey);
        }

        public void SetContextPrompt(string contextPrompt)
        {
            lock (_gate) _contextPrompts.Add(contextPrompt);
        }

        public void SetRecordingLimit(int seconds)
        {
            lock (_gate) _recordingLimits.Add(seconds);
        }

        public void SetAutoStart(bool enabled)
        {
            lock (_gate) _autoStarts.Add(enabled);
        }

        public void SetDevices(IReadOnlyList<AudioDeviceInfo> devices, string? selectedDeviceId)
        {
            lock (_gate) _deviceSets.Add((devices.ToList(), selectedDeviceId));
        }

        public void ShowHint(SettingsHint hint, string? detail = null)
        {
            lock (_gate) _hints.Add((hint, detail));
        }

        public void ClearHint()
        {
        }
    }
}

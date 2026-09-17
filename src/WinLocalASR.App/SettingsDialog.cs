using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.SettingsUi;
using WinLocalASR.Core.Shell;
using WinLocalASR.Resources;

namespace WinLocalASR.App;

/// <summary>
/// Thin WinForms shell for the settings dialog: hotkey capture box, input
/// device dropdown with refresh, hotwords textbox, recording-limit
/// NumericUpDown (10-120), autostart checkbox, Apply/Close. All logic lives
/// in Core (SettingsDialogPresenter); this class only renders and maps
/// KeyEventArgs into the presenter's capture state machine. Every visible
/// string flows through WinLocalASR.Resources. Task 7's shell constructs it
/// with the real dependencies and wires applyWithoutReload to
/// AppController.ApplySettingsWithoutReload.
/// </summary>
internal sealed class SettingsDialog : Form, ISettingsDialogView
{
    private readonly SettingsDialogPresenter _presenter;
    private readonly TextBox _hotkeyBox;
    private readonly ComboBox _deviceBox;
    private readonly Button _refreshButton;
    private readonly NumericUpDown _limitBox;
    private readonly CheckBox _autoStartBox;
    private readonly TextBox _contextPromptBox;
    private readonly Label _hintLabel;
    private readonly Button _applyButton;
    private readonly Button _closeButton;

    // Index-aligned with _deviceBox items; entry 0 = system default (null id).
    private List<string?> _deviceIds = new() { null };

    public SettingsDialog(
        SettingsStore store,
        IRegistryKeyFactory registryFactory,
        string exePath,
        Action? applyWithoutReload,
        Func<HotkeyRegistrationStatus>? hotkeyStatusProvider = null)
    {
        Text = Strings.Settings_Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(560, 430);

        var hotkeyLabel = new Label
        {
            Left = 12, Top = 16, Width = 130, Text = Strings.Settings_HotkeyLabel,
        };
        _hotkeyBox = new TextBox { Left = 150, Top = 12, Width = 260, ReadOnly = true };
        var hotkeyHint = new Label
        {
            Left = 150, Top = 38, Width = 390, Height = 28, Text = Strings.Settings_HotkeyHint,
            ForeColor = System.Drawing.Color.Gray,
        };

        var deviceLabel = new Label { Left = 12, Top = 78, Width = 130, Text = Strings.Settings_DeviceLabel };
        _deviceBox = new ComboBox
        {
            Left = 150, Top = 74, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _refreshButton = new Button
        {
            Left = 410, Top = 72, Width = 80, Height = 27, Text = Strings.Settings_RefreshDevices,
        };

        var limitLabel = new Label { Left = 12, Top = 120, Width = 130, Text = Strings.Settings_LimitLabel };
        _limitBox = new NumericUpDown
        {
            Left = 150, Top = 116, Width = 80, Minimum = AppSettings.MinRecordingLimitSeconds,
            Maximum = AppSettings.MaxRecordingLimitSeconds, Value = AppSettings.DefaultRecordingLimitSeconds,
        };
        var limitHint = new Label
        {
            Left = 245, Top = 120, Width = 300, Height = 32, Text = Strings.Settings_LimitHint,
            ForeColor = System.Drawing.Color.Gray,
        };

        _autoStartBox = new CheckBox { Left = 150, Top = 154, Width = 390, Text = Strings.Settings_AutoStart };

        var contextLabel = new Label { Left = 12, Top = 196, Width = 130, Text = Strings.Settings_ContextPromptLabel };
        _contextPromptBox = new TextBox
        {
            Left = 150, Top = 192, Width = 390, Height = 80, Multiline = true,
            PlaceholderText = Strings.Settings_ContextPromptPlaceholder,
        };
        var contextHint = new Label
        {
            Left = 150, Top = 278, Width = 390, Height = 28, Text = Strings.Settings_ContextPromptHint,
            ForeColor = System.Drawing.Color.Gray,
        };

        _hintLabel = new Label { Left = 12, Top = 322, Width = 530, Height = 20, Text = "" };

        _applyButton = new Button { Left = 384, Top = 356, Width = 76, Height = 28, Text = Strings.Settings_Apply };
        _closeButton = new Button { Left = 468, Top = 356, Width = 76, Height = 28, Text = Strings.Settings_Close };
        CancelButton = _closeButton;

        Controls.AddRange(new Control[]
        {
            hotkeyLabel, _hotkeyBox, hotkeyHint, deviceLabel, _deviceBox, _refreshButton,
            limitLabel, _limitBox, limitHint, _autoStartBox, contextLabel, _contextPromptBox,
            contextHint, _hintLabel, _applyButton, _closeButton,
        });

        // Before the event wiring: the ctor only stores references (view calls start at Load()).
        _presenter = new SettingsDialogPresenter(
            store, registryFactory, exePath, ListDevices, this, applyWithoutReload, hotkeyStatusProvider);

        _hotkeyBox.GotFocus += OnHotkeyGotFocus;
        _hotkeyBox.MouseClick += OnHotkeyMouseClick;
        _hotkeyBox.LostFocus += OnHotkeyLostFocus;
        _hotkeyBox.KeyDown += OnHotkeyKeyDown;
        _refreshButton.Click += OnRefreshClicked;
        _limitBox.ValueChanged += OnLimitValueChanged;
        _autoStartBox.CheckedChanged += OnAutoStartChanged;
        _contextPromptBox.TextChanged += OnContextPromptChanged;
        _deviceBox.SelectedIndexChanged += OnDeviceSelectedIndexChanged;
        _applyButton.Click += OnApplyClicked;
        _closeButton.Click += OnCloseClicked;
    }

    private void OnHotkeyGotFocus(object? sender, EventArgs e) => _presenter.BeginHotkeyCapture();

    private void OnHotkeyMouseClick(object? sender, MouseEventArgs e) => _presenter.BeginHotkeyCapture();

    private void OnHotkeyLostFocus(object? sender, EventArgs e) => _presenter.CancelHotkeyCapture();

    private void OnRefreshClicked(object? sender, EventArgs e) => _presenter.RefreshDevices();

    private void OnLimitValueChanged(object? sender, EventArgs e) =>
        _presenter.RecordingLimitChanged((int)_limitBox.Value);

    private void OnAutoStartChanged(object? sender, EventArgs e) =>
        _presenter.AutoStartChanged(_autoStartBox.Checked);

    private void OnContextPromptChanged(object? sender, EventArgs e) =>
        _presenter.ContextPromptChanged(_contextPromptBox.Text);

    private void OnDeviceSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_deviceBox.SelectedIndex >= 0)
        {
            _presenter.DeviceSelected(_deviceIds[_deviceBox.SelectedIndex]);
        }
    }

    private void OnApplyClicked(object? sender, EventArgs e) => _presenter.Apply();

    private void OnCloseClicked(object? sender, EventArgs e)
    {
        // macOS Settings parity: Close applies the settings first.
        if (_presenter.Apply())
        {
            Close();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _presenter.Load();
    }

    private static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        try
        {
            return new WindowsAudioDeviceFactory().ListInputDevices();
        }
        catch (Exception)
        {
            // Audio service unavailable (headless runner, service stopped):
            // an empty list leaves the system default selected.
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_presenter.IsCapturingHotkey && (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Escape))
        {
            return; // not capturing: let Tab navigate and Esc reach the form's Cancel button
        }

        // While capturing, the box is a recording surface: swallow everything,
        // forward only mappable keys (bare-modifier presses stay ignored).
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (WinKeyMapper.TryMapKey(e.KeyCode, out HotkeyKey key))
        {
            _presenter.HotkeyKeyPressed(WinKeyMapper.MapModifiers(e.Modifiers), key);
        }
    }

    void ISettingsDialogView.SetHotkey(string hotkey)
    {
        if (_hotkeyBox.Text != hotkey)
        {
            _hotkeyBox.Text = hotkey;
        }
    }

    void ISettingsDialogView.SetContextPrompt(string contextPrompt)
    {
        if (_contextPromptBox.Text != contextPrompt)
        {
            _contextPromptBox.Text = contextPrompt;
        }
    }

    void ISettingsDialogView.SetRecordingLimit(int seconds)
    {
        if ((int)_limitBox.Value != seconds)
        {
            _limitBox.Value = seconds;
        }
    }

    void ISettingsDialogView.SetAutoStart(bool enabled) => _autoStartBox.Checked = enabled;

    void ISettingsDialogView.SetDevices(IReadOnlyList<AudioDeviceInfo> devices, string? selectedDeviceId)
    {
        _deviceIds = new List<string?> { null };
        _deviceBox.Items.Clear();
        _deviceBox.Items.Add(Strings.Settings_SystemDefaultDevice);

        int selected = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            _deviceIds.Add(devices[i].Id);
            _deviceBox.Items.Add(devices[i].Name);
            if (devices[i].Id == selectedDeviceId)
            {
                selected = i + 1;
            }
        }

        _deviceBox.SelectedIndex = selected;
    }

    void ISettingsDialogView.ShowHint(SettingsHint hint, string? detail)
    {
        string text = HintText(hint);
        _hintLabel.Text = detail is null ? text : $"{text}: {detail}";
        _hintLabel.ForeColor = hint == SettingsHint.HotkeyConflict
            ? System.Drawing.Color.Firebrick
            : System.Drawing.SystemColors.ControlText;
    }

    void ISettingsDialogView.ClearHint()
    {
        _hintLabel.Text = "";
        _hintLabel.ForeColor = System.Drawing.SystemColors.ControlText;
    }

    private static string HintText(SettingsHint hint) => hint switch
    {
        SettingsHint.HotkeyCaptureActive => Strings.Settings_HotkeyCaptureActive,
        SettingsHint.HotkeyCaptureCancelled => Strings.Settings_HotkeyCaptureCancelled,
        SettingsHint.HotkeyNeedsModifier => Strings.Settings_HotkeyNeedsModifier,
        SettingsHint.HotkeyInvalidReset => Strings.Settings_HotkeyInvalidReset,
        SettingsHint.HotkeyConflict => Strings.Settings_HotkeyConflict,
        SettingsHint.RecordingLimitClamped => Strings.Settings_LimitClamped,
        SettingsHint.Applied => Strings.Settings_Applied,
        _ => Strings.Settings_ApplyFailed,
    };
}

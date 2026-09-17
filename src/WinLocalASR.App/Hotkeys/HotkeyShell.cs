using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.App;

/// <summary>
/// The Task 8 global-hotkey composition: the message-only window, the Core
/// <see cref="HotkeyManager"/>, and the controller wiring — WM_HOTKEY routing via
/// the dispatcher seam, Esc availability via
/// <see cref="AppController.CancelHotkeyAvailabilityChanged"/> (the single source
/// of truth; the manager never infers from the phase), and the toggle/cancel
/// actions bound to <see cref="AppController.ToggleRecording"/> /
/// <see cref="AppController.CancelActiveRecording"/>. Constructed on the UI thread
/// (RegisterHotKey requires the window-owning thread); hotkeys share the tray's
/// lifetime and degrade with it (a headless runner has no user input to lose).
/// </summary>
internal sealed class HotkeyShell : IDisposable
{
    private readonly HotkeyMessageWindow _window;
    private readonly HotkeyManager _manager;
    private readonly AppController _controller;

    private HotkeyShell(HotkeyMessageWindow window, HotkeyManager manager, AppController controller)
    {
        _window = window;
        _manager = manager;
        _controller = controller;
    }

    public HotkeyRegistrationStatus Status => _manager.Status;

    /// <summary>Settings-Apply hook: re-registers the user hotkey from the persisted string.</summary>
    public void ApplyHotkeyFromSettings() => _manager.ApplyHotkeyFromSettings();

    public static HotkeyShell Create(AppController controller, ITrayShell tray, IShellLog log)
    {
        HotkeyMessageWindow window = new();
        SettingsStore settings = new();
        HotkeyManager manager = new(
            new Win32HotkeyRegistrar(window.Handle),
            new SynchronizationContextDispatcher(), // captures the WinForms UI context
            () => settings.Load().Hotkey,
            controller.ToggleRecording,
            controller.CancelActiveRecording,
            log,
            tray.ShowErrorBalloon);

        window.HotkeyReceived = manager.HandleWmHotkey;
        controller.CancelHotkeyAvailabilityChanged += manager.SetCancelHotkeyActive;
        manager.Initialize();
        return new HotkeyShell(window, manager, controller);
    }

    public void Dispose()
    {
        _controller.CancelHotkeyAvailabilityChanged -= _manager.SetCancelHotkeyActive;
        _manager.Dispose();
        _window.Dispose();
    }
}

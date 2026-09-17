using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.Setup;
using WinLocalASR.Core.State;

namespace WinLocalASR.App;

/// <summary>
/// Opens the Task 10 setup dialog. Builds the SetupRunner with the completion delegate
/// wired to <see cref="AppController.InitializeAsync"/> (Task 6 made init explicit for
/// exactly this flow). The dialog is modeless so first-run downloads never block tray
/// creation. A missing/unreadable manifest template (dev tree, broken install) degrades
/// to a logged warning instead of crashing the shell.
/// </summary>
internal sealed class WinSetupDialogFactory : ISetupDialogFactory
{
    private readonly IShellLog _log;
    private readonly string _manifestTemplatePath;

    public WinSetupDialogFactory(IShellLog log, string? manifestTemplatePath = null)
    {
        _log = log;
        _manifestTemplatePath = manifestTemplatePath ?? Path.Combine(AppContext.BaseDirectory, "versions.json");
    }

    public void Open(AppController controller)
    {
        SetupRunner runner;
        try
        {
            runner = new SetupRunner(
                _manifestTemplatePath,
                new SetupRunnerOptions
                {
                    Completion = () => controller.InitializeAsync(),
                });
        }
        catch (Exception ex)
        {
            _log.Warn($"setup manifest unavailable ('{_manifestTemplatePath}'): {ex.Message}");
            return;
        }

        SetupDialog dialog = new(runner);
        dialog.Show(); // modeless: OnShown starts the presenter
    }
}

/// <summary>
/// Opens the Task 11 settings dialog bound to the controller: Apply routes through
/// <see cref="AppController.ApplySettingsWithoutReload"/> so hotwords/limit/device changes
/// apply without a model reload, then (when the Task 8 hotkey shell is up) re-registers
/// the user hotkey from the just-persisted settings string. The hotkey provider is
/// resolved at Open time because the manager is built with the tray, after this factory.
/// </summary>
internal sealed class WinSettingsDialogFactory : ISettingsDialogFactory
{
    private readonly Func<HotkeyShell?>? _hotkeys;

    public WinSettingsDialogFactory(Func<HotkeyShell?>? hotkeys = null) => _hotkeys = hotkeys;

    public void Open(AppController controller)
    {
        HotkeyShell? hotkeys = _hotkeys?.Invoke();
        if (hotkeys is null)
        {
            SettingsDialog plain = new(
                new SettingsStore(),
                new RegistryKeyFactory(),
                Environment.ProcessPath ?? "",
                controller.ApplySettingsWithoutReload);
            plain.Show();
            return;
        }

        SettingsDialog dialog = new(
            new SettingsStore(),
            new RegistryKeyFactory(),
            Environment.ProcessPath ?? "",
            () =>
            {
                controller.ApplySettingsWithoutReload();
                hotkeys.ApplyHotkeyFromSettings();
            },
            () => hotkeys.Status);
        dialog.Show();
    }
}

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
/// apply without a model reload.
/// </summary>
internal sealed class WinSettingsDialogFactory : ISettingsDialogFactory
{
    public void Open(AppController controller)
    {
        SettingsDialog dialog = new(
            new SettingsStore(),
            new RegistryKeyFactory(),
            Environment.ProcessPath ?? "",
            controller.ApplySettingsWithoutReload);
        dialog.Show();
    }
}

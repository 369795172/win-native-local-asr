using WinLocalASR.Core.Shell;

namespace WinLocalASR.App;

/// <summary>
/// Entry point: builds <see cref="AppBootstrapper"/> with the WinForms concretions and
/// runs it. Every UI construct reaches the (net10.0, macOS-testable) bootstrapper through
/// a factory seam; this file is the only place that knows about WinForms types.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // The dispatcher must post onto the WinForms context before any control exists
        // (WinForms installs its SynchronizationContext lazily otherwise).
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        FileShellLog log = new();
        Application.ThreadException += (_, e) => log.Warn($"UI thread exception: {e.Exception.Message}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Warn($"unhandled exception: {(e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject}");

        WinTrayShellFactory trayFactory = new(log);
        AppBootstrapper bootstrapper = new(new AppBootstrapperDependencies
        {
            MutexFactory = new SystemMutexFactory(),
            ServiceGraphFactory = new DefaultAppServiceGraphFactory(log),
            TrayShellFactory = trayFactory,
            SetupDialogFactory = new WinSetupDialogFactory(log),
            SettingsDialogFactory = new WinSettingsDialogFactory(() => trayFactory.Hotkeys),
            MessageLoop = new WinFormsMessageLoop(),
            Log = log,
        });

        // Blocking wait is deliberate: the UI thread is done once the loop stops, and the
        // bootstrapper's post-loop awaits use ConfigureAwait(false) so its continuations
        // finish on the thread pool instead of the dead UI context.
        return bootstrapper.RunAsync(args).GetAwaiter().GetResult();
    }
}

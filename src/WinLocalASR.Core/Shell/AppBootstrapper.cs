using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Shell;

/// <summary>
/// The controller plus everything the shell polled from settings before constructing it.
/// </summary>
public sealed record AppServiceGraph(AppController Controller, bool InitiallyConfigured);

/// <summary>
/// Builds the real object graph (SettingsStore → LlamaServerClient + AudioCaptureManager +
/// ClipboardWriter adapters → AppController with the BCL timing defaults). Behind a seam so
/// macOS bootstrapper tests inject Task 6's fakes and never touch Windows modules.
/// </summary>
public interface IAppServiceGraphFactory
{
    AppServiceGraph Create();
}

/// <summary>
/// Platform message loop: <see cref="Run"/> blocks the startup thread until
/// <see cref="Stop"/> is invoked (from any thread). Keeps the process alive headless,
/// which the GUI-degrade requirement demands.
/// </summary>
public interface IMessageLoop
{
    void Run();

    void Stop();
}

/// <summary>
/// Composition-root dependencies. Every UI construct sits behind a factory so the
/// bootstrapper itself is net10.0-testable; the WinForms concretions live in the App
/// project and are the only implementations that ever touch WinForms types.
/// </summary>
public sealed class AppBootstrapperDependencies
{
    public required IMutexFactory MutexFactory { get; init; }

    public required IAppServiceGraphFactory ServiceGraphFactory { get; init; }

    public required ITrayShellFactory TrayShellFactory { get; init; }

    public required ISetupDialogFactory SetupDialogFactory { get; init; }

    public required ISettingsDialogFactory SettingsDialogFactory { get; init; }

    public required IMessageLoop MessageLoop { get; init; }

    public required IShellLog Log { get; init; }
}

/// <summary>
/// Application composition root (Task 7): single-instance gate → service graph →
/// first-run setup offer when unconfigured → controller initialization → tray (degrading
/// to a logged warning + headless survival when the factory throws) → message loop →
/// clean shutdown. Awaits after the loop use ConfigureAwait(false) so continuations never
/// depend on the (now dead) UI synchronization context.
/// </summary>
public sealed class AppBootstrapper
{
    private readonly AppBootstrapperDependencies _dependencies;

    public AppBootstrapper(AppBootstrapperDependencies dependencies) => _dependencies = dependencies;

    /// <summary>Flag bag from the most recent <see cref="RunAsync"/> (Task 12 ControlServer seam).</summary>
    public CommandLineOptions Options { get; private set; } = CommandLineOptions.Empty;

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        Options = CommandLineOptions.Parse(args);

        using SingleInstanceGuard guard = new(_dependencies.MutexFactory);
        if (!guard.IsPrimary)
        {
            _dependencies.Log.Info("another instance is already running; exiting silently");
            return 0; // contract: second launch exits quietly, code 0, no second tray
        }

        AppServiceGraph graph = _dependencies.ServiceGraphFactory.Create();
        AppController controller = graph.Controller;

        if (!graph.InitiallyConfigured)
        {
            OpenSetupDegrading(controller); // first-run offer (non-blocking dialog)
        }

        _ = InitializeDegradingAsync(controller);

        IDisposable? tray = null;
        try
        {
            tray = _dependencies.TrayShellFactory.CreateTray(
                controller,
                _dependencies.SetupDialogFactory,
                _dependencies.SettingsDialogFactory,
                _dependencies.MessageLoop.Stop);
        }
        catch (Exception ex)
        {
            // GUI-degrade HARD REQUIREMENT: no tray (e.g. headless CI runner) must not kill
            // the process — the ControlServer stays reachable and the loop keeps running.
            _dependencies.Log.Warn($"tray shell unavailable, continuing headless: {ex.Message}");
        }

        try
        {
            _dependencies.MessageLoop.Run();
        }
        finally
        {
            tray?.Dispose();
            try
            {
                await controller.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _dependencies.Log.Warn($"engine shutdown failed: {ex.Message}");
            }
        }

        return 0;
    }

    private void OpenSetupDegrading(AppController controller)
    {
        try
        {
            _dependencies.SetupDialogFactory.Open(controller);
        }
        catch (Exception ex)
        {
            _dependencies.Log.Warn($"setup dialog unavailable, continuing without first-run setup: {ex.Message}");
        }
    }

    private async Task InitializeDegradingAsync(AppController controller)
    {
        try
        {
            await controller.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The controller funnels expected failures into the Error phase; anything else
            // still must not take the process down.
            _dependencies.Log.Warn($"initialization failed: {ex.Message}");
        }
    }
}

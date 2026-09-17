using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using Xunit;

namespace WinLocalASR.Tests.Shell;

/// <summary>
/// Bootstrapper flow, both QA- hard gates, and the headless survival semantics:
/// (1) a throwing tray factory must degrade to a logged warning with the loop still
/// running and a clean exit; (2) a held single-instance mutex must exit code 0 before
/// ANY shell construction.
/// </summary>
public class AppBootstrapperTests
{
    [Fact]
    public async Task QA_Throwing_tray_factory_degrades_to_headless_and_bootstrap_completes()
    {
        WinLocalASR.Tests.State.ControllerHarness harness = new();
        FakeShellLog log = new();
        ThrowingTrayFactory throwingFactory = new();
        FakeAppServiceGraphFactory graphFactory =
            new(new AppServiceGraph(harness.Controller, InitiallyConfigured: true));

        AppBootstrapper bootstrapper = ShellTestHost.CreateBootstrapper(
            graphFactory, out FakeMessageLoop loop, throwingFactory, log: log);

        int exitCode = await bootstrapper.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exitCode);
        Assert.Equal(1, throwingFactory.Calls);
        Assert.Equal(1, loop.RunCount); // loop still ran: process-alive semantics preserved
        Assert.Contains(log.Warnings, w => w.Contains("tray") && w.Contains("headless"));
        Assert.Equal(1, harness.Transcription.StartCalls); // engine initialized despite no tray
        Assert.Equal(1, harness.Transcription.StopCalls);  // teardown shut it down
    }

    [Fact]
    public async Task QA_Held_single_instance_mutex_exits_zero_without_building_any_shell()
    {
        FakeShellLog log = new();
        FakeAppServiceGraphFactory graphFactory =
            new(new AppServiceGraph(new WinLocalASR.Tests.State.ControllerHarness().Controller, true));
        ThrowingTrayFactory trayFactory = new();
        FakeSetupDialogFactory setupDialogFactory = new();

        AppBootstrapper bootstrapper = ShellTestHost.CreateBootstrapper(
            graphFactory, out FakeMessageLoop loop, trayFactory, setupDialogFactory, log, mutexAcquire: false);

        int exitCode = await bootstrapper.RunAsync(new[] { "--enable-control-server" });

        Assert.Equal(0, exitCode);
        Assert.Equal(0, graphFactory.CreateCalls);
        Assert.Equal(0, trayFactory.Calls);
        Assert.Empty(setupDialogFactory.OpenedControllers);
        Assert.Equal(0, loop.RunCount);
        Assert.Contains(log.Infos, m => m.Contains("another instance"));
    }

    [Fact]
    public async Task Unconfigured_first_run_offers_setup_then_surfaces_setup_required()
    {
        WinLocalASR.Tests.State.ControllerHarness harness = new(configured: false);
        FakeSetupDialogFactory setupDialogFactory = new();
        FakeShellLog log = new();
        FakeAppServiceGraphFactory graphFactory =
            new(new AppServiceGraph(harness.Controller, InitiallyConfigured: false));
        FakeTrayShellFactory trayFactory = new(log);

        AppBootstrapper bootstrapper = ShellTestHost.CreateBootstrapper(
            graphFactory, out _, trayFactory, setupDialogFactory, log);

        int exitCode = await bootstrapper.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exitCode);
        Assert.Same(harness.Controller, Assert.Single(setupDialogFactory.OpenedControllers));
        Assert.Equal(1, trayFactory.Calls); // the tray still comes up for the unconfigured user
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == StateStrings.SetupRequired);
    }

    [Fact]
    public async Task Throwing_setup_factory_degrades_to_a_warning_and_bootstrap_completes()
    {
        WinLocalASR.Tests.State.ControllerHarness harness = new(configured: false);
        FakeSetupDialogFactory setupDialogFactory = new() { ThrowOnOpen = new InvalidOperationException("headless") };
        FakeShellLog log = new();
        FakeAppServiceGraphFactory graphFactory =
            new(new AppServiceGraph(harness.Controller, InitiallyConfigured: false));

        AppBootstrapper bootstrapper = ShellTestHost.CreateBootstrapper(
            graphFactory, out FakeMessageLoop loop, setupDialogFactory: setupDialogFactory, log: log);

        int exitCode = await bootstrapper.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exitCode);
        Assert.Equal(1, loop.RunCount);
        Assert.Contains(log.Warnings, w => w.Contains("setup dialog unavailable"));
    }

    [Fact]
    public async Task Tray_exit_menu_runs_the_loop_until_shutdown_then_disposes_and_exits_zero()
    {
        WinLocalASR.Tests.State.ControllerHarness harness = new();
        FakeShellLog log = new();
        FakeTrayShellFactory trayFactory = new(log);
        FakeAppServiceGraphFactory graphFactory =
            new(new AppServiceGraph(harness.Controller, InitiallyConfigured: true));

        FakeMessageLoop loop;
        AppBootstrapper bootstrapper = ShellTestHost.CreateBootstrapper(graphFactory, out loop, trayFactory, log: log);
        loop.BlockUntilStop = true;

        Task<int> run = Task.Run(() => bootstrapper.RunAsync(Array.Empty<string>()));
        Assert.True(loop.Entered.Wait(2000), "message loop never started");

        trayFactory.LastShell!.RaiseExit(); // the menu Exit click end-to-end

        int exitCode = await run;
        Assert.Equal(0, exitCode);
        Assert.Equal(1, loop.RunCount);
        Assert.True(loop.StopCount >= 1);
        Assert.True(trayFactory.LastShell.Disposed);
        Assert.Equal(2, harness.Transcription.StopCalls); // presenter shutdown + bootstrapper teardown
    }
}

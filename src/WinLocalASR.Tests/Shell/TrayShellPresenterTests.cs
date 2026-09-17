using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.State;
using Xunit;

namespace WinLocalASR.Tests.Shell;

/// <summary>
/// Tray menu wiring over the real AppController (Task 6 fakes): every menu action must be
/// live (no dead items), the icon must follow PhaseChanged, the status row carries the
/// ported statusText/modelStatusText semantics, and the Setup highlight tracks the
/// configured state.
/// </summary>
public class TrayShellPresenterTests
{
    private static TrayShellPresenter Create(
        ControllerHarness harness,
        FakeTrayShell tray,
        FakeSetupDialogFactory? setup = null,
        FakeSettingsDialogFactory? settings = null)
    {
        FakeSetupDialogFactory setupFactory = setup ?? new FakeSetupDialogFactory();
        FakeSettingsDialogFactory settingsFactory = settings ?? new FakeSettingsDialogFactory();
        return new TrayShellPresenter(harness.Controller, tray, setupFactory, settingsFactory, () => { }, new FakeShellLog());
    }

    private static void InitializeCompleted(ControllerHarness harness)
    {
        Task init = harness.Controller.InitializeAsync();
        Assert.True(init.IsCompletedSuccessfully); // completed-fake chains run inline
    }

    [Fact]
    public void Phase_changes_drive_the_icon_selection()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        using TrayShellPresenter presenter = Create(harness, tray);

        Assert.Equal(TrayIconKind.Idle, tray.Icons[0]); // ctor applies Loading with the idle glyph

        InitializeCompleted(harness);
        Assert.Equal(TrayIconKind.Idle, tray.Icons[^1]); // Idle

        harness.Controller.ToggleRecording();
        Assert.Equal(TrayIconKind.Recording, tray.Icons[^1]);

        harness.Controller.ToggleRecording(); // stop → Processing → (inline fake) → Idle
        Assert.Equal(TrayIconKind.Processing, tray.Icons[^2]); // both transitions recorded
        Assert.Equal(TrayIconKind.Idle, tray.Icons[^1]);
    }

    [Fact]
    public void Error_transition_shows_a_balloon_and_refreshes_do_not_repeat_it()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        using TrayShellPresenter presenter = Create(harness, tray);

        InitializeCompleted(harness);
        Assert.Empty(tray.Balloons);

        harness.Controller.ToggleRecording();
        Assert.Empty(tray.Balloons);

        harness.Transcription.TranscribeException = new InvalidOperationException("boom");
        harness.Controller.ToggleRecording(); // → Error("boom")
        Assert.Equal(TrayIconKind.Error, tray.Icons[^1]);
        Assert.Single(tray.Balloons, "boom");

        presenter.Refresh(); // menu re-open must not re-notify
        Assert.Single(tray.Balloons);
    }

    [Fact]
    public void Status_row_and_tooltip_follow_statusText_and_modelStatusText_semantics()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        using TrayShellPresenter presenter = Create(harness, tray);

        InitializeCompleted(harness);
        TrayStatus ready = tray.Statuses[^1];
        Assert.Equal(TrayPhaseStatus.Ready, ready.Phase);
        Assert.Null(ready.PhaseDetail); // empty lastAction → plain ready word
        Assert.Equal(TrayEngineStatus.ModelLoaded, ready.Engine);
        Assert.Null(ready.EngineDetail);

        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // "hello world" transcript → copied → Idle
        TrayStatus afterCopy = tray.Statuses[^1];
        Assert.Equal(TrayPhaseStatus.Ready, afterCopy.Phase);
        Assert.Equal(StateStrings.CopiedToClipboard, afterCopy.PhaseDetail); // Swift: idle shows lastAction
        Assert.Equal(TrayEngineStatus.ModelLoaded, afterCopy.Engine);
        Assert.Equal(TrayPhaseStatus.Ready, tray.Tooltips[^1].Phase);
        Assert.Equal(StateStrings.CopiedToClipboard, tray.Tooltips[^1].Detail);

        harness.Controller.ToggleRecording();
        harness.Transcription.TranscribeException = new InvalidOperationException("boom");
        harness.Controller.ToggleRecording(); // → Error("boom")
        TrayStatus error = tray.Statuses[^1];
        Assert.Equal(TrayPhaseStatus.Error, error.Phase);
        Assert.Equal("boom", error.PhaseDetail);
        Assert.Equal(TrayEngineStatus.Error, error.Engine);
        Assert.Equal("boom", error.EngineDetail);
        Assert.Equal(TrayPhaseStatus.Error, tray.Tooltips[^1].Phase);
        Assert.Equal("boom", tray.Tooltips[^1].Detail);
    }

    [Fact]
    public void Setup_highlight_tracks_the_configured_state_in_both_directions()
    {
        ControllerHarness harness = new(configured: false);
        FakeTrayShell tray = new();
        using TrayShellPresenter presenter = Create(harness, tray);

        InitializeCompleted(harness); // unconfigured → Error(SetupRequired)
        Assert.True(tray.SetupHighlights[^1]);

        harness.Settings.Current = harness.Settings.Current with { Configured = true };
        InitializeCompleted(harness); // setup-completion path: Loading → Idle, configured
        Assert.False(tray.SetupHighlights[^1]);
    }

    [Fact]
    public async Task Every_menu_action_routes_to_its_controller_call()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        FakeSetupDialogFactory setupFactory = new();
        FakeSettingsDialogFactory settingsFactory = new();
        bool exited = false;
        using TrayShellPresenter presenter = new(
            harness.Controller, tray, setupFactory, settingsFactory, () => exited = true, new FakeShellLog());

        InitializeCompleted(harness);

        tray.RaiseSetup();
        Assert.Same(harness.Controller, Assert.Single(setupFactory.OpenedControllers));

        tray.RaiseSettings();
        Assert.Same(harness.Controller, Assert.Single(settingsFactory.OpenedControllers));

        Assert.DoesNotContain(true, tray.CopyEnabled); // no transcript yet → never enabled
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // transcript "hello world" lands on the clipboard
        Assert.Single(harness.Clipboard.Texts);
        Assert.True(tray.CopyEnabled[^1]);

        tray.RaiseCopyLastTranscript();
        Assert.Equal(2, harness.Clipboard.Texts.Count);
        Assert.Equal("hello world", harness.Clipboard.LastText);

        int startsBefore = harness.Transcription.StartCalls;
        tray.RaiseRestartEngine();
        Assert.Equal(startsBefore + 1, harness.Transcription.StartCalls);
        Assert.Equal(1, harness.Transcription.StopCalls);

        tray.RaiseExit();
        Assert.True(SpinWait.SpinUntil(() => exited, 2000), "exit callback not invoked");
        Assert.Equal(2, harness.Transcription.StopCalls); // Exit → ShutdownAsync
    }

    [Fact]
    public void Menu_opening_refreshes_polled_state()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        using TrayShellPresenter presenter = Create(harness, tray);

        int statusesBefore = tray.Statuses.Count;
        tray.RaiseMenuOpening();
        Assert.Equal(statusesBefore + 1, tray.Statuses.Count);
    }

    [Fact]
    public void Failing_dialog_factories_degrade_to_logged_warnings()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        FakeShellLog log = new();
        FakeSetupDialogFactory setupFactory = new() { ThrowOnOpen = new InvalidOperationException("headless") };
        FakeSettingsDialogFactory settingsFactory = new() { ThrowOnOpen = new InvalidOperationException("headless") };
        using TrayShellPresenter presenter = new(
            harness.Controller, tray, setupFactory, settingsFactory, () => { }, log);

        tray.RaiseSetup();
        tray.RaiseSettings();

        Assert.Contains(log.Warnings, w => w.Contains("setup dialog failed"));
        Assert.Contains(log.Warnings, w => w.Contains("settings dialog failed"));
    }

    [Fact]
    public void Dispose_unsubscribes_and_disposes_the_view()
    {
        ControllerHarness harness = new();
        FakeTrayShell tray = new();
        TrayShellPresenter presenter = Create(harness, tray);

        presenter.Dispose();

        Assert.True(tray.Disposed);
        InitializeCompleted(harness); // PhaseChanged must no longer reach the dead tray
        Assert.Single(tray.Icons); // only the ctor application
    }
}

using WinLocalASR.Core.Hud;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.State;
using Xunit;

namespace WinLocalASR.Tests.Hud;

/// <summary>
/// Task 9 presenter tests. Two layers: the pure <see cref="HudShellPresenter.BuildState"/>
/// mapping (every presentation → the exact view state) and integration over the REAL
/// <see cref="AppController"/> with Task 6's fakes (event wiring, show/hide dedup,
/// timer ticks, level flow, feedback lifetimes).
/// </summary>
public sealed class HudPresenterTests
{
    private static readonly DateTime ProcessingStart =
        new(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);

    // ---- Pure mapping: all six presentations → exact view state ----

    [Fact]
    public void Hidden_maps_to_all_off()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Hidden, 12.3, 120, 0.8f, ProcessingStart);
        Assert.Equal(HudViewState.Hidden, state);
    }

    [Fact]
    public void Recording_maps_red_accent_timer_level_bar_progress()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Recording, 5.4, 120, 0.75f, ProcessingStart);
        Assert.Equal(new HudViewState(
            Visible: true,
            Accent: HudAccent.Red,
            Glyph: HudGlyph.None,
            LabelKind: HudLabelKind.Recording,
            TimerVisible: true,
            TimerText: "00:05 / 02:00", // Swift truncation: Int(5.4) = 5
            ProcessingStartedUtc: null,
            LevelBarVisible: true,
            LevelBarSpinnerMode: false,
            Level: 0.75f,
            ProgressVisible: true,
            Progress: 5.4 / 120,
            Message: null), state);
    }

    [Fact]
    public void Processing_maps_amber_spinner_with_start_timestamp()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Processing, 5.4, 120, 0.75f, ProcessingStart);
        Assert.Equal(new HudViewState(
            Visible: true,
            Accent: HudAccent.Amber,
            Glyph: HudGlyph.None,
            LabelKind: HudLabelKind.Transcribing,
            TimerVisible: true,
            TimerText: null, // live text: view ticks FormatProcessingDuration per frame
            ProcessingStartedUtc: ProcessingStart,
            LevelBarVisible: true,
            LevelBarSpinnerMode: true,
            Level: 0f,
            ProgressVisible: false,
            Progress: 0d,
            Message: null), state);
    }

    [Fact]
    public void Copied_maps_green_check_without_live_elements()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Copied, 0, 120, 0f, null);
        Assert.Equal(new HudViewState(
            Visible: true,
            Accent: HudAccent.Green,
            Glyph: HudGlyph.Check,
            LabelKind: HudLabelKind.Copied,
            TimerVisible: false,
            TimerText: null,
            ProcessingStartedUtc: null,
            LevelBarVisible: false,
            LevelBarSpinnerMode: false,
            Level: 0f,
            ProgressVisible: false,
            Progress: 0d,
            Message: null), state);
    }

    [Fact]
    public void Cancelled_maps_grey_cancel_glyph()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Cancelled, 0, 120, 0f, null);
        Assert.Equal(new HudViewState(
            Visible: true,
            Accent: HudAccent.Grey,
            Glyph: HudGlyph.Cancel,
            LabelKind: HudLabelKind.Cancelled,
            TimerVisible: false,
            TimerText: null,
            ProcessingStartedUtc: null,
            LevelBarVisible: false,
            LevelBarSpinnerMode: false,
            Level: 0f,
            ProgressVisible: false,
            Progress: 0d,
            Message: null), state);
    }

    [Fact]
    public void Error_maps_red_error_glyph_with_message()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Error("engine exploded"), 0, 120, 0f, null);
        Assert.Equal(new HudViewState(
            Visible: true,
            Accent: HudAccent.Red,
            Glyph: HudGlyph.Error,
            LabelKind: HudLabelKind.None,
            TimerVisible: false,
            TimerText: null,
            ProcessingStartedUtc: null,
            LevelBarVisible: false,
            LevelBarSpinnerMode: false,
            Level: 0f,
            ProgressVisible: false,
            Progress: 0d,
            Message: "engine exploded"), state);
    }

    /// <summary>
    /// QA+ mapping rule: the clipboard-failure sub-case (Swift resolve maps
    /// ClipboardOutputState.failed → error(clipboardCopyFailed)) renders as a plain
    /// error — red ✗ glyph + message, no separate "failed" presentation.
    /// </summary>
    [Fact]
    public void Clipboard_failure_message_maps_to_red_error_glyph()
    {
        HudViewState state = HudShellPresenter.BuildState(
            DictationHudPresentation.Error(StateStrings.ClipboardCopyFailed), 0, 120, 0f, null);
        Assert.Equal(HudGlyph.Error, state.Glyph);
        Assert.Equal(HudAccent.Red, state.Accent);
        Assert.Equal(StateStrings.ClipboardCopyFailed, state.Message);
        Assert.Equal("error", DictationHudPresentation.Error(StateStrings.ClipboardCopyFailed).ControlValue);
    }

    // ---- mm:ss formatting (Swift AppState lines 5-8 semantics) ----

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(5.4, "00:05")] // Int(5.4) truncation
    [InlineData(65, "01:05")]
    [InlineData(600, "10:00")]
    public void FormatProcessingDuration_formats_mmss(double elapsedSeconds, string expected)
    {
        DateTime now = DateTime.UtcNow;
        Assert.Equal(
            expected,
            HudShellPresenter.FormatProcessingDuration(now.AddSeconds(-elapsedSeconds), now));
    }

    [Fact]
    public void FormatProcessingDuration_clamps_negative_elapsed_to_zero()
    {
        DateTime start = DateTime.UtcNow;
        Assert.Equal("00:00", HudShellPresenter.FormatProcessingDuration(start, start.AddSeconds(-3)));
    }

    // ---- Integration over the real controller ----

    private static (ControllerHarness Harness, HudShellPresenter Presenter, FakeHudView View) Wired(
        ControllerHarness harness)
    {
        FakeHudView view = new();
        HudShellPresenter presenter = new(harness.Controller, view);
        return (harness, presenter, view);
    }

    private static ControllerHarness Initialized()
    {
        ControllerHarness harness = new();
        Task init = harness.Controller.InitializeAsync();
        Assert.True(init.IsCompletedSuccessfully); // fake bridge starts synchronously → Idle
        return harness;
    }

    [Fact]
    public void Fresh_presenter_applies_hidden_without_showing()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        Assert.Single(view.Applies);
        Assert.Equal(HudViewState.Hidden, view.LastState);
        Assert.Equal(0, view.Shows);
        Assert.Equal(0, view.Hides);
    }

    [Fact]
    public void Recording_flow_shows_exact_state_and_advances_with_ticks()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);

        harness.Controller.ToggleRecording();
        Assert.Equal(1, view.Shows);
        Assert.NotNull(view.LastState);
        Assert.Equal(HudAccent.Red, view.LastState!.Accent);
        Assert.Equal("00:00 / 02:00", view.LastState.TimerText);
        Assert.Equal(HudLabelKind.Recording, view.LastState.LabelKind);
        Assert.True(view.LastState.LevelBarVisible);
        Assert.False(view.LastState.LevelBarSpinnerMode);
        Assert.True(view.LastState.ProgressVisible);

        harness.AdvanceMs(5400);
        Assert.Equal("00:05 / 02:00", view.LastState.TimerText);
        Assert.Equal(5.4 / 120, view.LastState.Progress, 5);

        harness.Audio.OnAudioLevel!.Invoke(0.75f);
        Assert.Equal(0.75f, view.LastState.Level);
    }

    [Fact]
    public void Level_updates_while_hidden_do_not_paint()
    {
        ControllerHarness harness = Initialized();
        (_, HudShellPresenter presenter, FakeHudView view) = Wired(harness);
        presenter.Dispose();

        // Stray level events after teardown must not resurrect state (unsubscribed).
        int applies = view.Applies.Count;
        harness.Audio.OnAudioLevel!.Invoke(0.9f);
        Assert.Equal(applies, view.Applies.Count);
    }

    [Fact]
    public void Processing_flow_maps_spinner_state_from_controller_timestamp()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        harness.Controller.ToggleRecording();

        harness.Transcription.PendingResult = new TaskCompletionSource<string>();
        harness.Controller.ToggleRecording(); // stop → Processing (transcription pending)
        Assert.NotNull(view.LastState);
        Assert.Equal(HudAccent.Amber, view.LastState!.Accent);
        Assert.Equal(HudLabelKind.Transcribing, view.LastState.LabelKind);
        Assert.Null(view.LastState.TimerText);
        Assert.Equal(harness.Controller.ProcessingStartedAt, view.LastState.ProcessingStartedUtc);
        Assert.True(view.LastState.LevelBarSpinnerMode);
        Assert.False(view.LastState.ProgressVisible);
        Assert.Equal(1, view.Shows); // dedup: still visible, no second Show
    }

    [Fact]
    public void Copied_flow_green_check_then_hidden_after_controller_lifetime()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // stop → transcribe "hello world" → Copied

        harness.Settle(() => view.LastState?.Glyph == HudGlyph.Check);
        Assert.Equal(HudAccent.Green, view.LastState!.Accent);
        Assert.Equal(HudLabelKind.Copied, view.LastState.LabelKind);
        Assert.False(view.LastState.TimerVisible);
        Assert.Equal(1, view.Shows);

        harness.AdvanceMs(1500); // controller-owned 1.5 s feedback window
        Assert.Equal(1, view.Hides);
    }

    [Fact]
    public void Cancelled_flow_grey_glyph_then_hidden()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        harness.Controller.ToggleRecording();

        harness.Controller.CancelActiveRecording();
        Assert.Equal(HudAccent.Grey, view.LastState!.Accent);
        Assert.Equal(HudGlyph.Cancel, view.LastState.Glyph);
        Assert.Equal(HudLabelKind.Cancelled, view.LastState.LabelKind);
        Assert.Equal(1, view.Shows);

        harness.AdvanceMs(1500);
        Assert.Equal(1, view.Hides);
    }

    [Fact]
    public void Error_flow_carries_message_then_hides_after_three_seconds()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        harness.Controller.ToggleRecording();

        harness.Transcription.TranscribeException = new InvalidOperationException("engine exploded");
        harness.Controller.ToggleRecording();
        harness.Settle(() => view.LastState?.Message == "engine exploded");
        Assert.Equal(HudGlyph.Error, view.LastState!.Glyph);
        Assert.Equal(HudAccent.Red, view.LastState.Accent);
        Assert.Equal(1, view.Shows);

        harness.AdvanceMs(3000); // error return: bridge ready → Idle → hidden
        Assert.Equal(1, view.Hides);
    }

    /// <summary>QA- clipboard-failure sub-case through the real controller event surface.</summary>
    [Fact]
    public void Clipboard_failure_renders_error_presentation_with_red_cross()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        harness.Clipboard.Succeed = false;
        harness.Controller.ToggleRecording();

        harness.Controller.ToggleRecording();
        harness.Settle(() => view.LastState?.Glyph == HudGlyph.Error);
        Assert.Equal(StateStrings.ClipboardCopyFailed, view.LastState!.Message);
        Assert.Equal(HudAccent.Red, view.LastState.Accent);

        harness.AdvanceMs(3000); // failed-clipboard feedback lifetime is 3 s
        Assert.Equal(1, view.Hides);
    }

    [Fact]
    public void Re_record_during_feedback_window_hides_then_shows_without_stale_ticks()
    {
        ControllerHarness harness = Initialized();
        (_, _, FakeHudView view) = Wired(harness);
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // → Copied (1.5 s window still open)
        harness.Settle(() => view.LastState?.Glyph == HudGlyph.Check);

        // BeginRecording clears the clipboard feedback first (→ hidden), then resets
        // duration/level while the presentation is still Hidden — those events must
        // not repaint — then Recording shows again with a clean timer.
        harness.Controller.ToggleRecording();
        Assert.Equal(2, view.Shows);
        Assert.Equal(1, view.Hides);
        Assert.Equal(HudLabelKind.Recording, view.LastState!.LabelKind);
        Assert.Equal("00:00 / 02:00", view.LastState.TimerText);
        Assert.Equal(0f, view.LastState.Level);
    }

    [Fact]
    public void Dispose_hides_and_unsubscribes()
    {
        ControllerHarness harness = Initialized();
        (_, HudShellPresenter presenter, FakeHudView view) = Wired(harness);
        harness.Controller.ToggleRecording();

        presenter.Dispose();
        Assert.Equal(1, view.Hides);
        int applies = view.Applies.Count;

        harness.Controller.CancelActiveRecording();
        Assert.Equal(applies, view.Applies.Count); // no more event delivery
    }
}

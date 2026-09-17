using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.State;
using Xunit;

namespace WinLocalASR.Tests.State;

// ---------------------------------------------------------------------------
// Swift transition inventory (AppState.swift @ main of mac-native-local-asr).
// Every Phase transition / observable behavior in the Swift file maps to at least
// one test below. Line numbers refer to AppState.swift.
//
//   Swift source                                → covering test
//   ---------------------------------------------------------------------
//   L88   initial phase = .loading              → Constructor_initial_phase_is_loading_and_hud_hidden
//   L142  initialize(): configured → startBridge→ Initialize_configured_reaches_idle_when_bridge_starts
//   L147  initialize(): !configured → .error    → Initialize_unconfigured_shows_setup_required_error
//   L320  startBridge → .loading                → Initialize_bridge_start_failure / RestartBridge_*
//   L334  startBridge ok → .idle                → Initialize_configured_reaches_idle_when_bridge_starts
//   L335  startBridge throw → .error (persist)  → Initialize_bridge_start_failure_shows_persistent_error
//   L247  toggle idle → beginRecording          → Toggle_from_idle_starts_recording
//   L249  toggle recording → stopAndTranscribe  → E2E_happy_path_* (QA+)
//   L253  toggle loading/processing/error → no-op→ Toggle_during_loading/processing/error_is_ignored
//         (double-toggle race, cf. L358-365)    → Toggle_during_processing_is_ignored_double_toggle_race
//   L344  begin: not ready → .error(engine)     → Toggle_when_engine_not_ready_shows_error_that_stays
//   L354  begin: startRecording ok → .recording → Toggle_from_idle_starts_recording
//   L366  begin: startRecording throw → .error  → BeginRecording_audio_failure_shows_error_then_idle
//   L361  recording timer 0.1 s ticks           → Recording_duration_advances_via_timer
//   L374  stopAndTranscribe → .processing       → E2E_happy_path_* (QA+)
//   L385  timeout from recordingDuration        → Transcription_timeout_computed_from_recording_duration
//   L383  temp WAV removed (defer)              → Temp_wav_deleted_after_transcription_outcomes
//   L392  empty transcript → AppError.empty     → Empty_transcript_shows_dedicated_error_then_idle (QA-)
//   L396  success → clipboard + feedback + idle → E2E_happy_path_* / Clipboard_failure_shows_failed_*
//   L398  clipboard copy failed → .failed (3 s) → Clipboard_failure_shows_failed_feedback_as_error_hud_3s
//   L402  catch → showError                     → Transcription_runtime_error_shows_error_then_returns_to_idle (QA-)
//   L407  showError replaces pending error task → New_error_window_replaces_the_pending_one
//   L412  error 3 s → ready → .idle             → Transcription_runtime_error_* / BeginRecording_audio_failure_*
//   L416  error 3 s → !ready → .error(engine)   → Toggle_when_engine_not_ready_shows_error_that_stays
//   L276  cancel guard: recording only          → Cancel_ignored_outside_recording_phase
//   L277  cancel → hotkey off, discard, .idle   → Cancel_from_recording_discards_and_returns_to_idle
//   L285  cancel → presentCancelFeedback        → Cancel_feedback_visible_1_5s_then_hidden
//   L293  syncCancelShortcutAvailability        → Cancel_hotkey_availability_lifecycle / ApplySettings_resyncs_*
//   L298  copyLastTranscript (empty/copied/failed)→ CopyLastTranscript_* (3 tests)
//   L305  shutdown: cancel all, phase unchanged → Shutdown_cancels_everything_and_keeps_phase
//   L428  feedback lifetimes 1.5 s / 3 s        → E2E (copied) / Clipboard_failure_* (failed)
//   L444  cancel feedback 1.5 s                 → Cancel_feedback_visible_1_5s_then_hidden
//   L344/L236 clear feedbacks on new recording  → New_recording_clears_stale_clipboard_feedback
//   L126  onMaximumDuration → stopAndTranscribe → Max_duration_callback_auto_stops_and_transcribes
//   L131  onAudioLevel → audioLevel             → Audio_level_updates_publish_event
//   L230  processingStartedAt / duration        → Processing_duration_uses_clock_and_clears_on_exit
//   L457  applyRecordingLimit + device          → ApplySettings_updates_limit_and_device_without_reload
//   L269  applySettings device (empty → nil)    → ApplySettings_updates_limit_and_device_without_reload
//   L258  restartBridge → stop + startBridge    → RestartBridge_stops_and_restarts_engine
//                                                  RestartBridge_failure_persists_error
// ---------------------------------------------------------------------------
public sealed class AppControllerTests
{
    private static ControllerHarness InitializedHarness()
    {
        ControllerHarness harness = new();
        Task init = harness.Controller.InitializeAsync();
        Assert.True(init.IsCompletedSuccessfully); // completed fakes keep the whole chain synchronous
        return harness;
    }

    private static void Record(ControllerHarness harness, double seconds) =>
        harness.AdvanceMs((long)(seconds * 1000));

    // ---- Initial state & initialize() ----

    [Fact]
    public void Constructor_initial_phase_is_loading_and_hud_hidden()
    {
        ControllerHarness harness = new();

        Assert.True(harness.Controller.Phase is AppPhase.LoadingPhase);
        Assert.False(harness.Controller.IsConfigured);
        Assert.False(harness.Controller.IsBridgeReady);
        Assert.Equal(string.Empty, harness.Controller.LastTranscript);
        Assert.True(harness.Controller.HudPresentation is DictationHudPresentation.HiddenPhase);
        Assert.Equal("hidden", harness.Controller.HudPresentation.ControlValue);
        Assert.False(harness.Controller.IsCancelHotkeyActive);
        // Swift init(): applyRecordingLimit(SettingsStore.recordingLimit()) — default 120.
        Assert.Equal(AppSettings.DefaultRecordingLimitSeconds, harness.Controller.RecordingLimit);
        Assert.Equal(harness.Controller.RecordingLimit, harness.Audio.MaximumDuration);
    }

    [Fact]
    public void Initialize_unconfigured_shows_setup_required_error()
    {
        ControllerHarness harness = new(configured: false);

        Task init = harness.Controller.InitializeAsync();
        Assert.True(init.IsCompletedSuccessfully); // fake chain is fully synchronous — no blocking wait

        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == StateStrings.SetupRequired);
        Assert.False(harness.Controller.IsConfigured);
        Assert.False(harness.Controller.IsBridgeReady);
        Assert.Equal("error", harness.Controller.HudPresentation.ControlValue);
    }

    [Fact]
    public void Initialize_configured_reaches_idle_when_bridge_starts()
    {
        ControllerHarness harness = new();

        Task init = harness.Controller.InitializeAsync();
        Assert.True(init.IsCompletedSuccessfully);

        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.True(harness.Controller.IsConfigured);
        Assert.True(harness.Controller.IsBridgeReady);
        Assert.Equal(1, harness.Transcription.StartCalls);
        Assert.Equal("phase:idle", Assert.Single(harness.Events));
    }

    [Fact]
    public void Initialize_bridge_start_failure_shows_persistent_error()
    {
        ControllerHarness harness = new();
        harness.Transcription.StartException = new ProcessExitedException("spawn failed");

        Task init = harness.Controller.InitializeAsync();
        Assert.True(init.IsCompletedSuccessfully);

        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error && error.Message == "spawn failed");
        Assert.False(harness.Controller.IsBridgeReady);
        Assert.False(harness.Controller.IsConfigured);
        // returnsToIdle: false — the error persists even after the 3 s window passes.
        harness.AdvanceMs(10_000);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase e2 && e2.Message == "spawn failed");
    }

    // ---- toggle: happy paths ----

    [Fact]
    public void Toggle_from_idle_starts_recording()
    {
        ControllerHarness harness = InitializedHarness();

        harness.Controller.ToggleRecording();

        Assert.True(harness.Controller.Phase is AppPhase.RecordingPhase);
        Assert.True(harness.Controller.IsRecording);
        Assert.True(harness.Controller.IsCancelHotkeyActive);
        Assert.Equal(1, harness.Audio.StartCalls);
        Assert.Equal(0.0, harness.Controller.RecordingDuration, 5);
        Assert.Equal("recording", harness.Controller.HudPresentation.ControlValue);
        Assert.Equal(
            new[] { "phase:idle", "phase:recording", "hud:recording", "esc:true" },
            harness.Events);
    }

    /// <summary>
    /// QA+ fake-chain e2e: Recording (2 s via fake timer) → fake client returns the text →
    /// fake clipboard receives it → Phase Idle → copied feedback visible → fast-forward
    /// 1.5 s → hidden. Asserts the full observable (phase, controlValue) sequence.
    /// </summary>
    [Fact]
    public async Task E2E_happy_path_recording_transcribe_clipboard_feedback()
    {
        ControllerHarness harness = new();
        await harness.Controller.InitializeAsync();
        harness.Controller.ToggleRecording();
        Record(harness, 2.0); // 20 ticks × 0.1 s
        Assert.InRange(harness.Controller.RecordingDuration, 1.999, 2.001);
        harness.Transcription.NextTranscript = "  你好世界  "; // trimmed before the clipboard

        harness.Controller.ToggleRecording();

        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal("你好世界", harness.Controller.LastTranscript);
        Assert.Equal("你好世界", harness.Clipboard.LastText);
        Assert.Single(harness.Clipboard.Texts);
        Assert.Equal(StateStrings.CopiedToClipboard, harness.Controller.LastAction);
        Assert.Null(harness.Controller.ProcessingStartedAt);
        Assert.Equal(TimeSpan.Zero, harness.Controller.ProcessingDuration);
        Assert.False(harness.Controller.IsCancelHotkeyActive);
        Assert.Equal("copied", harness.Controller.HudPresentation.ControlValue);
        Assert.Equal(
            new[]
            {
                "phase:idle",
                "phase:recording", "hud:recording", "esc:true",
                "esc:false", "phase:processing", "hud:processing",
                "phase:idle", "hud:copied",
            },
            harness.Events);

        // Feedback window is exactly 1.5 s (Swift presentClipboardFeedback(.copied)).
        harness.AdvanceMs(1499);
        Assert.True(harness.Controller.HudPresentation is DictationHudPresentation.CopiedPhase);
        harness.AdvanceMs(1);
        Assert.True(harness.Controller.HudPresentation is DictationHudPresentation.HiddenPhase);
        Assert.Equal("hud:hidden", harness.Events[^1]);
    }

    [Fact]
    public void Transcription_timeout_computed_from_recording_duration()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        Record(harness, 2.0); // max(90, 3×2+20) = 90
        harness.Controller.ToggleRecording();
        Assert.NotNull(harness.Transcription.LastTimeout);
        Assert.InRange(harness.Transcription.LastTimeout!.Value.TotalSeconds, 89.99, 90.01);

        ControllerHarness longHarness = InitializedHarness();
        longHarness.Controller.ToggleRecording();
        Record(longHarness, 45.0); // max(90, 3×45+20) = 155
        longHarness.Controller.ToggleRecording();
        Assert.NotNull(longHarness.Transcription.LastTimeout);
        Assert.InRange(longHarness.Transcription.LastTimeout!.Value.TotalSeconds, 154.99, 155.01);
    }

    [Fact]
    public void Temp_wav_deleted_after_transcription_outcomes()
    {
        string wav = Path.Combine(Path.GetTempPath(), "winlocalasr-test-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllText(wav, "pcm");

        ControllerHarness harness = InitializedHarness();
        harness.Audio.StopResult = wav;
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording();
        Assert.False(File.Exists(wav)); // success path deletes

        File.WriteAllText(wav, "pcm");
        harness.Transcription.TranscribeException = new RuntimeException(500, "boom");
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording();
        Assert.False(File.Exists(wav)); // failure path deletes too (Swift defer)
    }

    // ---- toggle: ignored phases (double-toggle race) ----

    [Fact]
    public void Toggle_during_processing_is_ignored_double_toggle_race()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        TaskCompletionSource<string> pending = new();
        harness.Transcription.PendingResult = pending;

        harness.Controller.ToggleRecording(); // → Processing, awaiting the fake client
        Assert.True(harness.Controller.Phase is AppPhase.ProcessingPhase);
        Assert.Equal(1, harness.Transcription.TranscribeCalls);

        harness.Controller.ToggleRecording(); // second toggle: IGNORED, no re-entry
        Assert.True(harness.Controller.Phase is AppPhase.ProcessingPhase);
        Assert.Equal(1, harness.Transcription.TranscribeCalls);
        Assert.Equal(1, harness.Audio.StartCalls);

        pending.SetResult("survived the race");
        harness.Settle(() => harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal("survived the race", harness.Controller.LastTranscript);
    }

    [Fact]
    public void Toggle_during_loading_is_ignored()
    {
        ControllerHarness harness = new();
        harness.Transcription.StartGate = new TaskCompletionSource();
        _ = harness.Controller.InitializeAsync();
        Assert.True(harness.Controller.Phase is AppPhase.LoadingPhase);

        harness.Controller.ToggleRecording();
        Assert.Equal(0, harness.Audio.StartCalls);

        harness.Transcription.StartGate.SetResult();
        harness.Settle(() => harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
    }

    [Fact]
    public void Toggle_during_error_is_ignored()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Transcription.TranscribeException = new RuntimeException(500, "boom");
        harness.Controller.ToggleRecording();
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase);

        harness.Controller.ToggleRecording();
        int starts = harness.Audio.StartCalls;
        Assert.Equal(starts, harness.Audio.StartCalls); // no new recording attempt
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase);
    }

    // ---- begin recording failure paths ----

    [Fact]
    public void Toggle_when_engine_not_ready_shows_error_that_stays()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Transcription.IsReady = false;

        harness.Controller.ToggleRecording();

        Assert.False(harness.Controller.IsBridgeReady);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == StateStrings.EngineNotReady);
        Assert.Equal(0, harness.Audio.StartCalls);

        // 3 s later readiness is re-read: still not ready → Error(engineNotReady), no loop.
        harness.AdvanceMs(3000);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase e2
            && e2.Message == StateStrings.EngineNotReady);
        Assert.False(harness.Controller.IsBridgeReady);
        harness.AdvanceMs(10_000);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase);
    }

    [Fact]
    public void BeginRecording_audio_failure_shows_error_then_idle()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Audio.StartException = new NoInputDeviceException();

        harness.Controller.ToggleRecording();

        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == new NoInputDeviceException().Message);
        Assert.True(harness.Controller.IsBridgeReady); // re-checked after the failure (Swift L367)
        Assert.False(harness.Controller.IsCancelHotkeyActive);

        harness.AdvanceMs(3000);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
    }

    // ---- transcription error paths (QA-) ----

    [Fact]
    public void Transcription_runtime_error_shows_error_then_returns_to_idle()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Transcription.TranscribeException = new RuntimeException(500, "boom from fake server");

        harness.Controller.ToggleRecording();

        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == "llama-server returned HTTP 500: boom from fake server");
        Assert.Equal("error", harness.Controller.HudPresentation.ControlValue);
        Assert.Null(harness.Controller.ProcessingStartedAt);
        Assert.Equal(string.Empty, harness.Controller.LastTranscript); // failure never reaches clipboard
        Assert.Empty(harness.Clipboard.Texts);

        harness.AdvanceMs(3000); // engine ready → back to Idle (Swift showError returnsToIdle)
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal("hud:hidden", harness.Events[^1]);
    }

    [Fact]
    public void Empty_transcript_shows_dedicated_error_then_idle()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Transcription.NextTranscript = "   \t\n  "; // trims to empty

        harness.Controller.ToggleRecording();

        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == StateStrings.EmptyTranscript);
        Assert.Empty(harness.Clipboard.Texts);
        Assert.False(harness.Controller.IsCancelHotkeyActive);

        harness.AdvanceMs(3000);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
    }

    [Fact]
    public void Clipboard_failure_shows_failed_feedback_as_error_hud_3s()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Clipboard.Succeed = false;

        harness.Controller.ToggleRecording();

        // Phase is Idle (transcription succeeded); the FAILURE renders through the HUD only,
        // as error(clipboardCopyFailed) — controlValue "error", no separate failed value.
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal("hello world", harness.Controller.LastTranscript);
        Assert.Equal(StateStrings.ClipboardCopyFailed, harness.Controller.LastAction);
        DictationHudPresentation.ErrorPhase hud =
            Assert.IsType<DictationHudPresentation.ErrorPhase>(harness.Controller.HudPresentation);
        Assert.Equal(StateStrings.ClipboardCopyFailed, hud.Message);
        Assert.Equal("error", harness.Controller.HudPresentation.ControlValue);

        // Failed feedback lives 3 s (not 1.5 s).
        harness.AdvanceMs(1500);
        Assert.Equal("error", harness.Controller.HudPresentation.ControlValue);
        harness.AdvanceMs(1500);
        Assert.Equal("hidden", harness.Controller.HudPresentation.ControlValue);
    }

    [Fact]
    public void New_error_window_replaces_the_pending_one()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Transcription.TranscribeException = new RuntimeException(500, "first failure");
        harness.Controller.ToggleRecording(); // error #1: 3 s window would end at t≈3000

        harness.AdvanceMs(2000);
        harness.Transcription.StartException = new ProcessExitedException("spawn failed");
        harness.Controller.RestartBridge(); // startBridge replaces the error window (Swift errorTask?.cancel())

        // Past the OLD deadline: the replaced timer must not fire (it would return to Idle).
        harness.AdvanceMs(1100);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error
            && error.Message == "spawn failed");
        Assert.Contains("phase:loading", harness.Events);

        // The replacement error is persistent (returnsToIdle: false).
        harness.AdvanceMs(10_000);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase e2 && e2.Message == "spawn failed");
    }

    // ---- cancel (Esc) ----

    [Fact]
    public void Cancel_ignored_outside_recording_phase()
    {
        ControllerHarness harness = InitializedHarness();

        harness.Controller.CancelActiveRecording(); // Idle: no-op
        Assert.Equal(0, harness.Audio.CancelCalls);

        TaskCompletionSource<string> pending = new();
        harness.Transcription.PendingResult = pending;
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // Processing
        harness.Controller.CancelActiveRecording();
        Assert.Equal(0, harness.Audio.CancelCalls);
        Assert.True(harness.Controller.Phase is AppPhase.ProcessingPhase);
        pending.SetResult("ok");
        harness.Settle(() => harness.Controller.Phase is AppPhase.IdlePhase);

        harness.Transcription.TranscribeException = new RuntimeException(500, "boom");
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // Error
        harness.Controller.CancelActiveRecording();
        Assert.Equal(0, harness.Audio.CancelCalls);
    }

    [Fact]
    public void Cancel_from_recording_discards_and_returns_to_idle()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        Record(harness, 1.2);

        harness.Controller.CancelActiveRecording();

        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal(1, harness.Audio.CancelCalls);
        Assert.Equal(0, harness.Audio.StopCalls); // discard path never finalizes a WAV
        Assert.Equal(0, harness.Transcription.TranscribeCalls);
        Assert.Empty(harness.Clipboard.Texts);
        Assert.Equal(0.0, harness.Controller.RecordingDuration, 5);
        Assert.Equal(0.0f, harness.Controller.AudioLevel);
        Assert.Equal(StateStrings.RecordingCancelled, harness.Controller.LastAction);
        Assert.False(harness.Controller.IsCancelHotkeyActive);
        Assert.Equal(
            new[]
            {
                "phase:idle",
                "phase:recording", "hud:recording", "esc:true",
                "esc:false", "phase:idle", "hud:cancelled",
            },
            harness.Events);
    }

    [Fact]
    public void Cancel_feedback_visible_1_5s_then_hidden()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Controller.CancelActiveRecording();
        Assert.Equal("cancelled", harness.Controller.HudPresentation.ControlValue);

        harness.AdvanceMs(1499);
        Assert.Equal("cancelled", harness.Controller.HudPresentation.ControlValue);
        harness.AdvanceMs(1);
        Assert.Equal("hidden", harness.Controller.HudPresentation.ControlValue);
        Assert.False(harness.Controller.CancelFeedbackActive);
    }

    [Fact]
    public void Cancel_hotkey_availability_lifecycle()
    {
        ControllerHarness harness = InitializedHarness();

        harness.Controller.ToggleRecording(); // begin → active
        Assert.True(harness.Controller.IsCancelHotkeyActive);

        harness.Transcription.NextTranscript = "stop";
        harness.Controller.ToggleRecording(); // stop → inactive
        Assert.False(harness.Controller.IsCancelHotkeyActive);

        harness.Controller.ToggleRecording(); // begin again → active
        Assert.True(harness.Controller.IsCancelHotkeyActive);
        harness.Controller.CancelActiveRecording(); // cancel → inactive
        Assert.False(harness.Controller.IsCancelHotkeyActive);

        Assert.Equal(
            new[] { "esc:true", "esc:false", "esc:true", "esc:false" },
            harness.Events.Where(e => e.StartsWith("esc:", StringComparison.Ordinal)).ToArray());
    }

    // ---- max-duration auto-stop & timer ----

    [Fact]
    public void Max_duration_callback_auto_stops_and_transcribes()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        Assert.Equal(harness.Controller.RecordingLimit, harness.Audio.MaximumDuration);

        harness.Audio.OnMaximumDuration!.Invoke(); // Task 3 manager: reports only, state machine stops

        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal(1, harness.Audio.StopCalls);
        Assert.Equal(1, harness.Transcription.TranscribeCalls);
        Assert.False(harness.Controller.IsCancelHotkeyActive);
        Assert.Equal(
            new[]
            {
                "phase:idle",
                "phase:recording", "hud:recording", "esc:true",
                "esc:false", "phase:processing", "hud:processing",
                "phase:idle", "hud:copied",
            },
            harness.Events);
    }

    [Fact]
    public void Recording_duration_advances_via_timer()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        int ticks = 0;
        harness.Controller.RecordingDurationChanged += _ => ticks++;

        harness.AdvanceMs(1000);

        Assert.InRange(harness.Controller.RecordingDuration, 0.99, 1.01);
        Assert.Equal(10, ticks);

        harness.Controller.CancelActiveRecording();
        harness.AdvanceMs(5000); // timer invalidated by cancel — no further ticks
        Assert.Equal(0.0, harness.Controller.RecordingDuration, 5);
        Assert.Equal(11, ticks); // 10 ticks + the cancel path's duration reset to 0
    }

    [Fact]
    public void New_recording_clears_stale_clipboard_feedback()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // copied feedback now visible
        Assert.Equal("copied", harness.Controller.HudPresentation.ControlValue);

        harness.Controller.ToggleRecording(); // Swift L344: clearClipboardFeedback() on begin

        Assert.True(harness.Controller.Phase is AppPhase.RecordingPhase);
        Assert.Equal(ClipboardOutputState.None, harness.Controller.ClipboardState);
        Assert.Equal("recording", harness.Controller.HudPresentation.ControlValue);
        harness.AdvanceMs(5000); // the old 1.5 s timer was cancelled — no late hides
        Assert.Equal("recording", harness.Controller.HudPresentation.ControlValue);
    }

    // ---- applySettingsWithoutReload ----

    [Fact]
    public void ApplySettings_updates_limit_and_device_without_reload()
    {
        ControllerHarness harness = InitializedHarness();
        int startsAfterInit = harness.Transcription.StartCalls;

        harness.Settings.Current = harness.Settings.Current with
        {
            RecordingLimitSeconds = 60,
            DeviceId = "mic-1",
            ContextPrompt = "names: Ada, Linus",
        };
        harness.Controller.ApplySettingsWithoutReload();

        Assert.Equal(60, harness.Controller.RecordingLimit);
        Assert.Equal(60, harness.Audio.MaximumDuration);
        Assert.Equal("mic-1", harness.Audio.SelectedDeviceId);
        Assert.Equal(startsAfterInit, harness.Transcription.StartCalls); // no model reload

        // Limit is clamped (Swift normalizedRecordingLimit) and empty device id → null.
        harness.Settings.Current = harness.Settings.Current with { RecordingLimitSeconds = 999, DeviceId = "" };
        harness.Controller.ApplySettingsWithoutReload();
        Assert.Equal(AppSettings.MaxRecordingLimitSeconds, harness.Controller.RecordingLimit);
        Assert.Null(harness.Audio.SelectedDeviceId);

        // Hotwords flow into the next transcription (context parameter).
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording();
        Assert.Equal("names: Ada, Linus", harness.Transcription.LastContext);
    }

    [Fact]
    public void ApplySettings_resyncs_cancel_hotkey_with_phase()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        Assert.True(harness.Controller.IsCancelHotkeyActive);

        harness.Controller.ApplySettingsWithoutReload(); // while Recording → stays active
        Assert.True(harness.Controller.IsCancelHotkeyActive);

        harness.Controller.CancelActiveRecording();
        Assert.False(harness.Controller.IsCancelHotkeyActive);

        harness.Controller.ApplySettingsWithoutReload(); // while Idle → stays inactive
        Assert.False(harness.Controller.IsCancelHotkeyActive);
    }

    // ---- restartBridge ----

    [Fact]
    public void RestartBridge_stops_and_restarts_engine()
    {
        ControllerHarness harness = InitializedHarness();

        harness.Controller.RestartBridge();

        Assert.True(harness.Controller.IsBridgeReady); // cleared transiently, ready again after restart
        Assert.Equal(1, harness.Transcription.StopCalls);
        Assert.Equal(2, harness.Transcription.StartCalls);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Equal(
            new[] { "phase:idle", "phase:loading", "phase:idle" },
            harness.Events.Where(e => e.StartsWith("phase:", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void RestartBridge_failure_persists_error()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Transcription.StartException = new ProcessExitedException("engine died");

        harness.Controller.RestartBridge();

        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase error && error.Message == "engine died");
        Assert.False(harness.Controller.IsBridgeReady);
        harness.AdvanceMs(10_000);
        Assert.True(harness.Controller.Phase is AppPhase.ErrorPhase); // returnsToIdle: false
    }

    // ---- copyLastTranscript ----

    [Fact]
    public void CopyLastTranscript_empty_returns_false_without_feedback()
    {
        ControllerHarness harness = InitializedHarness();

        Assert.False(harness.Controller.CopyLastTranscript());
        Assert.Empty(harness.Clipboard.Texts);
        Assert.Equal("hidden", harness.Controller.HudPresentation.ControlValue);
    }

    [Fact]
    public void CopyLastTranscript_success_copied_feedback_1_5s()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Transcription.NextTranscript = "reusable text";
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording();

        Assert.True(harness.Controller.CopyLastTranscript());
        Assert.Equal(2, harness.Clipboard.Texts.Count); // transcription + manual copy
        Assert.Equal("reusable text", harness.Clipboard.LastText);
        Assert.Equal("copied", harness.Controller.HudPresentation.ControlValue);

        harness.AdvanceMs(1500);
        Assert.Equal("hidden", harness.Controller.HudPresentation.ControlValue);
    }

    [Fact]
    public void CopyLastTranscript_clipboard_failure_shows_failed_feedback()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Transcription.NextTranscript = "text";
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording();
        harness.Clipboard.Succeed = false;

        Assert.False(harness.Controller.CopyLastTranscript());
        Assert.Equal(StateStrings.ClipboardCopyFailed, harness.Controller.LastAction);
        Assert.Equal("error", harness.Controller.HudPresentation.ControlValue);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase); // no phase impact

        harness.AdvanceMs(3000);
        Assert.Equal("hidden", harness.Controller.HudPresentation.ControlValue);
    }

    // ---- misc observable behavior ----

    [Fact]
    public void Audio_level_updates_publish_event()
    {
        ControllerHarness harness = InitializedHarness();
        float seen = -1;
        harness.Controller.AudioLevelChanged += level => seen = level;

        harness.Audio.OnAudioLevel!.Invoke(0.75f);

        Assert.Equal(0.75f, seen);
        Assert.Equal(0.75f, harness.Controller.AudioLevel);
    }

    [Fact]
    public void Processing_duration_uses_clock_and_clears_on_exit()
    {
        ControllerHarness harness = InitializedHarness();
        harness.Controller.ToggleRecording();
        TaskCompletionSource<string> pending = new();
        harness.Transcription.PendingResult = pending;
        harness.Controller.ToggleRecording(); // → Processing

        Assert.NotNull(harness.Controller.ProcessingStartedAt);
        Assert.Equal(TimeSpan.Zero, harness.Controller.ProcessingDuration);

        harness.Time.NowMs += 5000;
        Assert.Equal(TimeSpan.FromSeconds(5), harness.Controller.ProcessingDuration);

        pending.SetResult("done");
        harness.Settle(() => harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.True(harness.Controller.Phase is AppPhase.IdlePhase);
        Assert.Null(harness.Controller.ProcessingStartedAt);
        Assert.Equal(TimeSpan.Zero, harness.Controller.ProcessingDuration);
    }

    [Fact]
    public async Task Shutdown_cancels_everything_and_keeps_phase()
    {
        ControllerHarness harness = new();
        await harness.Controller.InitializeAsync();
        harness.Controller.ToggleRecording();
        Assert.True(harness.Controller.Phase is AppPhase.RecordingPhase);

        await harness.Controller.ShutdownAsync();

        Assert.True(harness.Controller.Phase is AppPhase.RecordingPhase); // Swift leaves the phase alone
        Assert.Equal(1, harness.Audio.CancelCalls);
        Assert.Equal(1, harness.Transcription.StopCalls);
        Assert.False(harness.Controller.IsBridgeReady);
        Assert.False(harness.Controller.IsCancelHotkeyActive);

        double frozen = harness.Controller.RecordingDuration;
        harness.AdvanceMs(5000); // recording timer disposed — no orphaned ticks
        Assert.Equal(frozen, harness.Controller.RecordingDuration, 5);
    }

    [Fact]
    public async Task Shutdown_freezes_pending_clipboard_feedback()
    {
        ControllerHarness harness = new();
        await harness.Controller.InitializeAsync();
        harness.Controller.ToggleRecording();
        harness.Controller.ToggleRecording(); // copied feedback visible
        Assert.Equal("copied", harness.Controller.HudPresentation.ControlValue);

        await harness.Controller.ShutdownAsync();
        harness.AdvanceMs(5000);

        // Swift cancels the feedback Task: the state stays frozen instead of auto-hiding.
        Assert.Equal(ClipboardOutputState.Copied, harness.Controller.ClipboardState);
        Assert.Equal("copied", harness.Controller.HudPresentation.ControlValue);
    }
}

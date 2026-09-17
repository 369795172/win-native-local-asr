using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Settings;

namespace WinLocalASR.Core.State;

/// <summary>
/// Port of MacLocalASR <c>AppState.swift</c> (the @MainActor observable state machine).
/// Pure logic — zero WinForms — with every timing dependency behind injectable seams
/// (<see cref="ITimer"/>, <see cref="IDispatcher"/>, <see cref="IClock"/>), so tests
/// fast-forward time instead of sleeping.
///
/// Threading model: synchronous entry points (ToggleRecording, CancelActiveRecording,
/// ApplySettingsWithoutReload, CopyLastTranscript, RestartBridge kick-off, Initialize /
/// StopAndTranscribe prologues) run on the caller's thread — the shell calls them from its
/// UI thread. Async continuations, timer callbacks and audio callbacks are marshalled
/// through <see cref="IDispatcher"/>. With a synchronous dispatcher (unit tests) every
/// operation completes inline and deterministically; internal awaits use
/// ConfigureAwait(false) so pending-task completion keeps executing inline in tests.
///
/// Swift deviations (deliberate, documented):
/// - <c>initialize()</c> is not auto-run from the constructor; the shell calls
///   <see cref="InitializeAsync"/> explicitly so startup order is testable and Task 10 can
///   wire it after setup.
/// - <c>runSetup()</c> lives with the setup module (Task 10), not here.
/// - statusText / modelStatusText / menuBarSymbol presentation helpers live in the shell
///   (Task 7) which owns localized resources.
/// </summary>
public sealed class AppController
{
    private const int RecordingTickMs = 100;
    private static readonly TimeSpan CopiedFeedbackLifetime = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FailedFeedbackLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ErrorReturnDelay = TimeSpan.FromSeconds(3);

    private readonly IAudioCaptureService _audioCapture;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsProvider _settingsProvider;
    private readonly ITimer _timer;
    private readonly IDispatcher _dispatcher;
    private readonly IClock _clock;

    private IDisposable? _errorTimer;
    private IDisposable? _clipboardFeedbackTimer;
    private IDisposable? _cancelFeedbackTimer;
    private IDisposable? _recordingTickTimer;

    private AppPhase _phase = AppPhase.Loading;
    private ClipboardOutputState _clipboardState = ClipboardOutputState.None;
    private bool _cancelFeedbackActive;
    private DictationHudPresentation _lastNotifiedHud = DictationHudPresentation.Hidden;
    private string _lastTranscript = "";
    private string _lastAction = "";
    private bool _isBridgeReady;
    private bool _isConfigured;
    private double _recordingDuration;
    private float _audioLevel;
    private double _recordingLimit = AppSettings.DefaultRecordingLimitSeconds;
    private DateTime? _processingStartedAt;
    private bool _cancelHotkeyActive;

    public AppController(
        IAudioCaptureService audioCapture,
        ITranscriptionService transcriptionService,
        IClipboardService clipboard,
        ISettingsProvider settingsProvider,
        ITimer timer,
        IDispatcher dispatcher,
        IClock clock)
    {
        _audioCapture = audioCapture;
        _transcriptionService = transcriptionService;
        _clipboard = clipboard;
        _settingsProvider = settingsProvider;
        _timer = timer;
        _dispatcher = dispatcher;
        _clock = clock;

        // Swift init(): applyRecordingLimit(SettingsStore.recordingLimit()) + wire the
        // audio capture callbacks (onMaximumDuration / onAudioLevel).
        ApplyRecordingLimit(_settingsProvider.Load().RecordingLimitSeconds);
        _audioCapture.OnMaximumDuration = () =>
            _dispatcher.Post(() => _ = StopAndTranscribeAsync());
        _audioCapture.OnAudioLevel = level =>
            _dispatcher.Post(() => SetAudioLevel(level));
    }

    // ---- Observable state (Swift @Published properties) ----

    public AppPhase Phase => _phase;

    public string LastTranscript => _lastTranscript;

    public string LastAction => _lastAction;

    public bool IsBridgeReady => _isBridgeReady;

    public bool IsConfigured => _isConfigured;

    public double RecordingDuration => _recordingDuration;

    public float AudioLevel => _audioLevel;

    public double RecordingLimit => _recordingLimit;

    public bool CancelFeedbackActive => _cancelFeedbackActive;

    public ClipboardOutputState ClipboardState => _clipboardState;

    public DateTime? ProcessingStartedAt => _processingStartedAt;

    public bool IsRecording => _phase is AppPhase.RecordingPhase;

    /// <summary>True while the Esc cancel hotkey should be registered (Swift setCancelShortcutActive).</summary>
    public bool IsCancelHotkeyActive => _cancelHotkeyActive;

    public DictationHudPresentation HudPresentation =>
        DictationHudPresentation.Resolve(_phase, _clipboardState, _cancelFeedbackActive);

    /// <summary>Swift processingDuration: elapsed since ProcessingStartedAt, 0 outside Processing.</summary>
    public TimeSpan ProcessingDuration => _phase is AppPhase.ProcessingPhase && _processingStartedAt is { } started
        ? TimeSpan.FromTicks(Math.Max(0, (_clock.UtcNow - started).Ticks))
        : TimeSpan.Zero;

    // ---- Event surface (inter-module contract: tray / HUD / hotkeys / ControlServer) ----

    /// <summary>Fires on every phase change (deduplicated); ControlServer appends phaseHistory from it.</summary>
    public event Action<AppPhase>? PhaseChanged;

    /// <summary>
    /// Fires whenever the resolved HUD presentation changes — the ControlServer controlValue
    /// and the Task 9 HUD window subscribe to this (mirrors the Swift HUD's
    /// CombineLatest3($phase, $clipboardState, $cancelFeedbackActive) + removeDuplicates).
    /// </summary>
    public event Action<DictationHudPresentation>? HudPresentationChanged;

    /// <summary>Mirrors Swift HotkeyManager.setCancelShortcutActive — only Recording wants Esc.</summary>
    public event Action<bool>? CancelHotkeyAvailabilityChanged;

    public event Action<string>? LastTranscriptChanged;

    /// <summary>0.1 s recording-tick updates for the HUD timer text.</summary>
    public event Action<double>? RecordingDurationChanged;

    /// <summary>Normalized [0,1] level updates for the HUD level bars.</summary>
    public event Action<float>? AudioLevelChanged;

    // ---- Operations (Swift API parity) ----

    /// <summary>Swift initialize(): check configured; start the bridge or surface the setup-required error.</summary>
    public async Task InitializeAsync()
    {
        _isConfigured = _settingsProvider.Load().Configured;
        if (_isConfigured)
        {
            await StartBridgeAsync().ConfigureAwait(false);
        }
        else
        {
            SetPhase(AppPhase.Error(StateStrings.SetupRequired));
        }
    }

    /// <summary>
    /// Swift toggleRecording(): idle → begin recording; recording → stop and transcribe;
    /// loading / processing / error → ignored (no re-entry — the double-toggle race guard).
    /// </summary>
    public void ToggleRecording()
    {
        switch (_phase)
        {
            case AppPhase.IdlePhase:
                BeginRecording();
                break;
            case AppPhase.RecordingPhase:
                _ = StopAndTranscribeAsync();
                break;
        }
    }

    /// <summary>Swift cancelActiveRecording(): discard the active recording. Recording-only.</summary>
    public void CancelActiveRecording()
    {
        if (_phase is not AppPhase.RecordingPhase)
        {
            return;
        }

        SetCancelHotkeyAvailable(false);
        StopRecordingTimer();
        _audioCapture.CancelRecording();
        SetAudioLevel(0);
        SetRecordingDuration(0);
        _processingStartedAt = null;
        _lastAction = StateStrings.RecordingCancelled;
        PresentCancelFeedback();
        SetPhase(AppPhase.Idle);
    }

    /// <summary>Swift stopAndTranscribe(): audio stop → WAV → transcribe → clipboard → feedback.</summary>
    public Task StopAndTranscribeAsync() => StopAndTranscribeCoreAsync();

    /// <summary>Swift restartBridge(): stop the engine and bring it back up.</summary>
    public void RestartBridge()
    {
        _isBridgeReady = false;
        _ = RestartBridgeAsync();
    }

    /// <summary>Swift applySettingsWithoutReloadingModel(): hotwords/limit/device changes, no model reload.</summary>
    public void ApplySettingsWithoutReload()
    {
        AppSettings settings = _settingsProvider.Load();
        ApplyRecordingLimit(settings.RecordingLimitSeconds);
        _audioCapture.SelectedDeviceId = string.IsNullOrEmpty(settings.DeviceId) ? null : settings.DeviceId;
        SyncCancelShortcutAvailability();
    }

    /// <summary>Swift copyLastTranscript(): re-copy the last transcript, with clipboard feedback.</summary>
    public bool CopyLastTranscript()
    {
        if (_lastTranscript.Length == 0)
        {
            return false;
        }

        bool copied = _clipboard.SetText(_lastTranscript);
        PresentClipboardFeedback(copied ? ClipboardOutputState.Copied : ClipboardOutputState.Failed);
        return copied;
    }

    /// <summary>Swift syncCancelShortcutAvailability(): realign Esc registration with the current phase.</summary>
    public void SyncCancelShortcutAvailability() => SetCancelHotkeyAvailable(_phase is AppPhase.RecordingPhase);

    /// <summary>
    /// Swift shutdown(): cancel every pending task and the active recording, stop the
    /// engine. The phase is deliberately left unchanged (Swift does not touch it either);
    /// pending feedback stays frozen because its timer is disposed.
    /// </summary>
    public async Task ShutdownAsync()
    {
        DisposeTimer(ref _errorTimer);
        DisposeTimer(ref _clipboardFeedbackTimer);
        DisposeTimer(ref _cancelFeedbackTimer);
        SetCancelHotkeyAvailable(false);
        StopRecordingTimer();
        _audioCapture.CancelRecording();
        await _transcriptionService.StopAsync().ConfigureAwait(false);
        _isBridgeReady = false;
    }

    // ---- Engine / recording internals ----

    private async Task RestartBridgeAsync()
    {
        await _transcriptionService.StopAsync().ConfigureAwait(false);
        _dispatcher.Post(() => _ = StartBridgeAsync());
    }

    /// <summary>Swift startBridge(): reload settings, (re)start the engine, Loading → Idle/Error.</summary>
    private async Task StartBridgeAsync()
    {
        DisposeTimer(ref _errorTimer);
        ClearClipboardFeedback();
        SetPhase(AppPhase.Loading);
        _lastAction = "";

        string? failure = null;
        try
        {
            AppSettings settings = _settingsProvider.Load();
            _audioCapture.SelectedDeviceId = string.IsNullOrEmpty(settings.DeviceId) ? null : settings.DeviceId;
            ApplyRecordingLimit(settings.RecordingLimitSeconds);
            await _transcriptionService.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = UserMessage(ex);
        }

        string? capturedFailure = failure;
        _dispatcher.Post(() =>
        {
            if (capturedFailure is null)
            {
                _isBridgeReady = true;
                _isConfigured = true;
                SetPhase(AppPhase.Idle);
            }
            else
            {
                _isBridgeReady = false;
                _isConfigured = false;
                ShowError(capturedFailure, returnsToIdle: false);
            }
        });
    }

    private void BeginRecording()
    {
        if (_phase is not AppPhase.IdlePhase)
        {
            return;
        }

        ClearClipboardFeedback();
        ClearCancelFeedback();
        if (!_transcriptionService.IsReady)
        {
            _isBridgeReady = false;
            ShowError(StateStrings.EngineNotReady);
            return;
        }

        try
        {
            SetAudioLevel(0);
            _audioCapture.StartRecording();
            SetRecordingDuration(0);
            SetPhase(AppPhase.Recording);
            SetCancelHotkeyAvailable(true);
            // A stale reference can survive a double-toggle race; drop it so no orphaned
            // timer keeps advancing recordingDuration (Swift comment, AppState.swift
            // lines 358-365). Re-arming repeats via self-rescheduling one-shots.
            StartRecordingTimer();
        }
        catch (Exception ex)
        {
            _isBridgeReady = _transcriptionService.IsReady;
            ShowError(UserMessage(ex));
        }
    }

    private async Task StopAndTranscribeCoreAsync()
    {
        if (_phase is not AppPhase.RecordingPhase)
        {
            return;
        }

        SetCancelHotkeyAvailable(false);
        _processingStartedAt = _clock.UtcNow;
        SetPhase(AppPhase.Processing);
        StopRecordingTimer();
        SetAudioLevel(0);

        string wavPath;
        try
        {
            wavPath = _audioCapture.StopRecording();
        }
        catch (Exception ex)
        {
            ShowError(UserMessage(ex));
            return;
        }

        TranscriptionOutcome outcome;
        try
        {
            // Swift line 385: timeout = transcriptionTimeoutSeconds(for: recordingDuration),
            // ported as the explicit TranscribeAsync override (Task 4 implements the formula).
            TimeSpan timeout = TranscriptionTimeout.Compute(TimeSpan.FromSeconds(_recordingDuration));
            string transcript = (await _transcriptionService.TranscribeAsync(
                wavPath,
                _settingsProvider.Load().ContextPrompt,
                CancellationToken.None,
                timeout).ConfigureAwait(false)).Trim();
            if (transcript.Length == 0)
            {
                throw new EmptyTranscriptException();
            }

            outcome = TranscriptionOutcome.Ok(transcript);
        }
        catch (Exception ex)
        {
            outcome = TranscriptionOutcome.Fail(UserMessage(ex));
        }
        finally
        {
            // Swift defer: remove the temp WAV in every outcome.
            TryDeleteFile(wavPath);
        }

        TranscriptionOutcome captured = outcome;
        _dispatcher.Post(() => ApplyTranscriptionOutcome(captured));
    }

    private void ApplyTranscriptionOutcome(TranscriptionOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            ShowError(outcome.ErrorMessage!);
            return;
        }

        SetLastTranscript(outcome.Transcript!);
        bool copied = _clipboard.SetText(outcome.Transcript!);
        PresentClipboardFeedback(copied ? ClipboardOutputState.Copied : ClipboardOutputState.Failed);
        _processingStartedAt = null;
        SetPhase(AppPhase.Idle);
    }

    /// <summary>
    /// Swift showError(message:returnsToIdle:): after 3 s, readiness is re-read; the
    /// not-ready branch sets Error directly without scheduling another return (no loop) —
    /// bug-for-bug parity.
    /// </summary>
    private void ShowError(string message, bool returnsToIdle = true)
    {
        DisposeTimer(ref _errorTimer);
        _processingStartedAt = null;
        SetPhase(AppPhase.Error(message));
        if (!returnsToIdle)
        {
            return;
        }

        _errorTimer = _timer.Schedule(ErrorReturnDelay, () => _dispatcher.Post(() =>
        {
            _isBridgeReady = _transcriptionService.IsReady;
            SetPhase(_isBridgeReady
                ? AppPhase.Idle
                : AppPhase.Error(StateStrings.EngineNotReady));
        }));
    }

    private void PresentClipboardFeedback(ClipboardOutputState state)
    {
        DisposeTimer(ref _clipboardFeedbackTimer);
        SetClipboardState(state);
        _lastAction = state == ClipboardOutputState.Copied
            ? StateStrings.CopiedToClipboard
            : StateStrings.ClipboardCopyFailed;

        ClipboardOutputState captured = state;
        _clipboardFeedbackTimer = _timer.Schedule(
            state == ClipboardOutputState.Copied ? CopiedFeedbackLifetime : FailedFeedbackLifetime,
            () => _dispatcher.Post(() =>
            {
                if (_clipboardState == captured) // Swift guard: a newer feedback owns the state
                {
                    SetClipboardState(ClipboardOutputState.None);
                }
            }));
    }

    private void ClearClipboardFeedback()
    {
        DisposeTimer(ref _clipboardFeedbackTimer);
        SetClipboardState(ClipboardOutputState.None);
    }

    private void PresentCancelFeedback()
    {
        DisposeTimer(ref _cancelFeedbackTimer);
        SetCancelFeedbackActive(true);
        _cancelFeedbackTimer = _timer.Schedule(CopiedFeedbackLifetime, () => _dispatcher.Post(() =>
        {
            if (_cancelFeedbackActive)
            {
                SetCancelFeedbackActive(false);
            }
        }));
    }

    private void ClearCancelFeedback()
    {
        DisposeTimer(ref _cancelFeedbackTimer);
        SetCancelFeedbackActive(false);
    }

    private void StartRecordingTimer()
    {
        StopRecordingTimer();
        ScheduleNextRecordingTick();
    }

    private void ScheduleNextRecordingTick()
    {
        _recordingTickTimer = _timer.Schedule(TimeSpan.FromMilliseconds(RecordingTickMs), () =>
            _dispatcher.Post(() =>
            {
                SetRecordingDuration(_recordingDuration + RecordingTickMs / 1000.0);
                ScheduleNextRecordingTick();
            }));
    }

    private void StopRecordingTimer() => DisposeTimer(ref _recordingTickTimer);

    private void ApplyRecordingLimit(int seconds)
    {
        int normalized = AppSettings.ClampRecordingLimit(seconds);
        _recordingLimit = normalized;
        _audioCapture.MaximumDuration = normalized;
    }

    // ---- State mutation helpers (raise the event surface) ----

    private void SetPhase(AppPhase phase)
    {
        if (_phase.Equals(phase))
        {
            return;
        }

        _phase = phase;
        PhaseChanged?.Invoke(phase);
        NotifyHud();
    }

    private void SetClipboardState(ClipboardOutputState state)
    {
        if (_clipboardState == state)
        {
            return;
        }

        _clipboardState = state;
        NotifyHud();
    }

    private void SetCancelFeedbackActive(bool active)
    {
        if (_cancelFeedbackActive == active)
        {
            return;
        }

        _cancelFeedbackActive = active;
        NotifyHud();
    }

    private void NotifyHud()
    {
        DictationHudPresentation current = HudPresentation;
        if (current.Equals(_lastNotifiedHud))
        {
            return;
        }

        _lastNotifiedHud = current;
        HudPresentationChanged?.Invoke(current);
    }

    private void SetCancelHotkeyAvailable(bool active)
    {
        if (_cancelHotkeyActive == active)
        {
            return;
        }

        _cancelHotkeyActive = active;
        CancelHotkeyAvailabilityChanged?.Invoke(active);
    }

    private void SetLastTranscript(string transcript)
    {
        _lastTranscript = transcript;
        LastTranscriptChanged?.Invoke(transcript);
    }

    private void SetRecordingDuration(double value)
    {
        _recordingDuration = value;
        RecordingDurationChanged?.Invoke(value);
    }

    private void SetAudioLevel(float value)
    {
        _audioLevel = value;
        AudioLevelChanged?.Invoke(value);
    }

    /// <summary>
    /// Deliberate exception → Error-phase message mapping. NOTE:
    /// <c>WinLocalASR.Core.Inference.TimeoutException</c> shadows
    /// <c>System.TimeoutException</c> — never catch a bare TimeoutException here; the
    /// <see cref="LlamaServerException"/> base covers that case.
    /// </summary>
    private static string UserMessage(Exception ex) => ex switch
    {
        EmptyTranscriptException => StateStrings.EmptyTranscript,
        AudioCaptureException => ex.Message, // NoInputDevice / NoAudioCaptured / … carry user-facing messages
        LlamaServerException => ex.Message,  // NotReady / ProcessExited / Timeout / Runtime / RestartFailed
        _ => ex.Message,
    };

    private static void DisposeTimer(ref IDisposable? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct TranscriptionOutcome(bool Succeeded, string? Transcript, string? ErrorMessage)
    {
        public static TranscriptionOutcome Ok(string transcript) => new(true, transcript, null);

        public static TranscriptionOutcome Fail(string message) => new(false, null, message);
    }
}

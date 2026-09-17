using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Hud;

/// <summary>
/// Port of Swift <c>DictationHUDController</c> (DictationHUD.swift lines 237-360):
/// subscribes to the controller's HUD event surface, resolves each
/// <see cref="DictationHudPresentation"/> into a renderable
/// <see cref="HudViewState"/>, and shows/hides the view with dedup (the Swift panel
/// keeps <c>alphaValue = 1</c> when already visible). Timing of the copied 1.5 s /
/// cancelled 1.5 s / error 3 s feedback windows is controller-owned (Task 6) — this
/// presenter only renders.
/// </summary>
public sealed class HudShellPresenter : IDisposable
{
    private readonly AppController _controller;
    private readonly IHudView _view;

    private DictationHudPresentation _presentation = DictationHudPresentation.Hidden;
    private double _recordingDuration;
    private float _level;
    private bool _shown;
    private bool _disposed;

    public HudShellPresenter(AppController controller, IHudView view)
    {
        _controller = controller;
        _view = view;
        _view.Apply(CurrentState());
        controller.HudPresentationChanged += OnPresentationChanged;
        controller.RecordingDurationChanged += OnRecordingDurationChanged;
        controller.AudioLevelChanged += OnAudioLevelChanged;
    }

    /// <summary>
    /// Swift <c>formatHUDDuration</c> applied to the processing elapsed time
    /// (<c>processingDuration(at:)</c>, DictationHUD.swift lines 165-168): the view
    /// calls this each animation frame with its clock.
    /// </summary>
    public static string FormatProcessingDuration(DateTime startedUtc, DateTime nowUtc) =>
        HudText.FormatHudDuration(nowUtc - startedUtc);

    /// <summary>
    /// The pure presentation → view-state mapping (the port of Swift's
    /// <c>content(at:)</c> switch, lines 44-78). Exposed static so the exact mapping
    /// is unit-testable without a controller.
    /// </summary>
    public static HudViewState BuildState(
        DictationHudPresentation presentation,
        double recordingDurationSeconds,
        double recordingLimitSeconds,
        float level,
        DateTime? processingStartedUtc) => presentation switch
    {
        DictationHudPresentation.RecordingPhase => new(
            Visible: true,
            Accent: HudAccent.Red,
            Glyph: HudGlyph.None,
            LabelKind: HudLabelKind.Recording,
            TimerVisible: true,
            TimerText: $"{HudText.FormatHudDuration(TimeSpan.FromSeconds(recordingDurationSeconds))} / "
                + HudText.FormatHudDuration(TimeSpan.FromSeconds(recordingLimitSeconds)),
            ProcessingStartedUtc: null,
            LevelBarVisible: true,
            LevelBarSpinnerMode: false,
            Level: level,
            ProgressVisible: true,
            Progress: HudMetrics.RecordingProgress(recordingDurationSeconds, recordingLimitSeconds),
            Message: null),
        DictationHudPresentation.ProcessingPhase => new(
            Visible: true,
            Accent: HudAccent.Amber,
            Glyph: HudGlyph.None,
            LabelKind: HudLabelKind.Transcribing,
            TimerVisible: true,
            TimerText: null, // live: view ticks FormatProcessingDuration(started, now)
            ProcessingStartedUtc: processingStartedUtc,
            LevelBarVisible: true,
            LevelBarSpinnerMode: true,
            Level: 0f,
            ProgressVisible: false,
            Progress: 0d,
            Message: null),
        DictationHudPresentation.CopiedPhase => new(
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
            Message: null),
        DictationHudPresentation.CancelledPhase => new(
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
            Message: null),
        DictationHudPresentation.ErrorPhase error => new(
            Visible: true,
            Accent: HudAccent.Red,
            Glyph: HudGlyph.Error, // clipboard failure included: red ✗ inside error (Swift resolve parity)
            LabelKind: HudLabelKind.None, // the message IS the text
            TimerVisible: false,
            TimerText: null,
            ProcessingStartedUtc: null,
            LevelBarVisible: false,
            LevelBarSpinnerMode: false,
            Level: 0f,
            ProgressVisible: false,
            Progress: 0d,
            Message: error.Message),
        _ => HudViewState.Hidden,
    };

    private HudViewState CurrentState() =>
        BuildState(_presentation, _recordingDuration, _controller.RecordingLimit, _level, _controller.ProcessingStartedAt);

    private void OnPresentationChanged(DictationHudPresentation next)
    {
        _presentation = next;
        if (next.IsVisible)
        {
            _view.Apply(CurrentState());
            if (!_shown)
            {
                _shown = true;
                _view.ShowHud(); // repositions on every show (Swift show() → reposition())
            }
        }
        else if (_shown)
        {
            _shown = false;
            _view.HideHud(); // no Apply: the view fades out from the last painted content
        }
    }

    private void OnRecordingDurationChanged(double duration)
    {
        _recordingDuration = duration;
        // BeginRecording resets the duration while the previous feedback presentation is
        // still live — only Recording repaints from tick events.
        if (_presentation is DictationHudPresentation.RecordingPhase)
        {
            _view.Apply(CurrentState());
        }
    }

    private void OnAudioLevelChanged(float level)
    {
        _level = level;
        if (_presentation is DictationHudPresentation.RecordingPhase)
        {
            _view.Apply(CurrentState());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.HudPresentationChanged -= OnPresentationChanged;
        _controller.RecordingDurationChanged -= OnRecordingDurationChanged;
        _controller.AudioLevelChanged -= OnAudioLevelChanged;
        if (_shown)
        {
            _shown = false;
            _view.HideHud();
        }
    }
}

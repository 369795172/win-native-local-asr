namespace WinLocalASR.Core.State;

/// <summary>
/// Swift DictationHUDPresentation (AppState.swift lines 20-76): the HUD presentation state
/// resolved from (phase, clipboardState, cancelFeedbackActive). ControlValue strings are a
/// load-bearing wire contract — the ControlServer e2e (Task 12) asserts exactly
/// hidden/recording/processing/copied/cancelled/error. Clipboard failure renders as
/// error(clipboardCopyFailed) with red-cross semantics — there is deliberately no separate
/// "failed" value (upstream Swift parity).
/// </summary>
public abstract record DictationHudPresentation
{
    private DictationHudPresentation() { }

    public sealed record HiddenPhase : DictationHudPresentation;
    public sealed record RecordingPhase : DictationHudPresentation;
    public sealed record ProcessingPhase : DictationHudPresentation;
    public sealed record CopiedPhase : DictationHudPresentation;
    public sealed record CancelledPhase : DictationHudPresentation;
    public sealed record ErrorPhase(string Message) : DictationHudPresentation;

    public static readonly DictationHudPresentation Hidden = new HiddenPhase();
    public static readonly DictationHudPresentation Recording = new RecordingPhase();
    public static readonly DictationHudPresentation Processing = new ProcessingPhase();
    public static readonly DictationHudPresentation Copied = new CopiedPhase();
    public static readonly DictationHudPresentation Cancelled = new CancelledPhase();

    public static DictationHudPresentation Error(string message) => new ErrorPhase(message);

    /// <summary>Swift DictationHUDPresentation.resolve(phase:clipboardState:cancelFeedbackActive:).</summary>
    public static DictationHudPresentation Resolve(
        AppPhase phase,
        ClipboardOutputState clipboardState,
        bool cancelFeedbackActive = false)
    {
        return phase switch
        {
            AppPhase.RecordingPhase => Recording,
            AppPhase.ProcessingPhase => Processing,
            AppPhase.ErrorPhase error => Error(error.Message),
            AppPhase.IdlePhase when cancelFeedbackActive => Cancelled,
            AppPhase.IdlePhase => clipboardState switch
            {
                ClipboardOutputState.Copied => Copied,
                ClipboardOutputState.Failed => Error(StateStrings.ClipboardCopyFailed),
                _ => Hidden,
            },
            _ => Hidden, // Loading
        };
    }

    public bool IsVisible => this != Hidden;

    public string ControlValue => this switch
    {
        HiddenPhase => "hidden",
        RecordingPhase => "recording",
        ProcessingPhase => "processing",
        CopiedPhase => "copied",
        CancelledPhase => "cancelled",
        ErrorPhase => "error",
        _ => throw new InvalidOperationException("unreachable"),
    };

    public bool NeedsTimelineUpdates => this is RecordingPhase or ProcessingPhase;
}

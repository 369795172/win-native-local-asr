using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Hud;

/// <summary>
/// Plain rectangle in Windows screen coordinates (Y grows downward). Core keeps the
/// placement math dependency-free; the WinForms view converts <c>Screen.WorkingArea</c>
/// into this shape.
/// </summary>
public readonly record struct HudRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>Accent (border) role per presentation; the view maps it to a color.</summary>
public enum HudAccent
{
    None,

    /// <summary>Recording border / dot / bars, and every error.</summary>
    Red,

    /// <summary>Processing border + spinner bars (Swift .orange).</summary>
    Amber,

    /// <summary>Copied feedback.</summary>
    Green,

    /// <summary>Cancelled feedback.</summary>
    Grey,
}

/// <summary>Feedback glyph; the view maps it to a character + color.</summary>
public enum HudGlyph
{
    None,

    /// <summary>Copied: green ✓ (Swift checkmark.circle.fill).</summary>
    Check,

    /// <summary>Cancelled: grey ↯.</summary>
    Cancel,

    /// <summary>Every error, including the clipboard-failure sub-case: red ✗.</summary>
    Error,
}

/// <summary>Static label kind; Core stays text-free, the view maps it to resources.</summary>
public enum HudLabelKind
{
    None,

    Recording,

    Transcribing,

    Copied,

    Cancelled,
}

/// <summary>
/// Everything the HUD window needs to render one frame, derived from
/// <see cref="DictationHudPresentation"/> plus the live recording signals. Recording
/// mode is fully event-driven (0.1 s <see cref="AppController.RecordingDurationChanged"/>
/// ticks refresh <see cref="TimerText"/>); processing mode carries the start timestamp
/// and the view ticks the elapsed text from its animation clock (Swift TimelineView
/// parity — <c>processingDuration(at:)</c>).
/// </summary>
public sealed record HudViewState(
    bool Visible,
    HudAccent Accent,
    HudGlyph Glyph,
    HudLabelKind LabelKind,
    bool TimerVisible,
    string? TimerText,
    DateTime? ProcessingStartedUtc,
    bool LevelBarVisible,
    bool LevelBarSpinnerMode,
    float Level,
    bool ProgressVisible,
    double Progress,
    string? Message)
{
    public static HudViewState Hidden { get; } = new(
        Visible: false,
        Accent: HudAccent.None,
        Glyph: HudGlyph.None,
        LabelKind: HudLabelKind.None,
        TimerVisible: false,
        TimerText: null,
        ProcessingStartedUtc: null,
        LevelBarVisible: false,
        LevelBarSpinnerMode: false,
        Level: 0f,
        ProgressVisible: false,
        Progress: 0d,
        Message: null);
}

/// <summary>View contract implemented by the WinForms HUD form; fakes record calls in tests.</summary>
public interface IHudView : IDisposable
{
    /// <summary>Push a (possibly partial) state refresh; the view repaints itself.</summary>
    void Apply(HudViewState state);

    /// <summary>Make the overlay visible at its computed position without taking focus.</summary>
    void ShowHud();

    /// <summary>Begin hiding (the view may fade out from the last painted content, Swift parity).</summary>
    void HideHud();
}

/// <summary>
/// Creates and wires the HUD. Implementations may throw when the environment cannot
/// host a window — <see cref="HudComposition.CreateDegrading"/> catches that and keeps
/// the process running headless (the Task 7 GUI-degrade hard requirement covers the HUD).
/// </summary>
public interface IHudShellFactory
{
    IDisposable CreateHud(AppController controller);
}

/// <summary>
/// Win32 extended-style constants for the HUD window, in Core so the shipped form
/// (App) and the Windows-only tests share one source of truth. The combination makes
/// the overlay topmost, invisible to Alt-Tab and the taskbar, non-activating, and
/// click-through (Swift: level .floating + nonactivatingPanel + ignoresMouseEvents).
/// </summary>
public static class HudWindowStyles
{
    public const int WsExTransparent = 0x00000020;

    public const int WsExTopmost = 0x00000008;

    public const int WsExToolWindow = 0x00000080;

    public const int WsExLayered = 0x00080000;

    public const int WsExNoActivate = 0x08000000;

    /// <summary>Every extended style the HUD window must carry.</summary>
    public const int RequiredExStyle =
        WsExTopmost | WsExToolWindow | WsExTransparent | WsExLayered | WsExNoActivate;

    public const int GwlExStyle = -20;

    /// <summary>ShowWindow command that never activates the window.</summary>
    public const int SwShowNoActivate = 8;
}

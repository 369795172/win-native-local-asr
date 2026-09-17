namespace WinLocalASR.Core.Hud;

/// <summary>
/// Pure window-placement math for the HUD overlay. Swift anchors the panel
/// bottom-center of the screen containing the mouse; the Windows port anchors it
/// bottom-right of the primary screen's working area (ratified in docs/rfc.md
/// §Parity HUD row) with a breathing margin above the taskbar.
/// </summary>
public static class HudPlacement
{
    /// <summary>Swift DictationHUDController.panelSize width (logical px, scaled by DPI in the view).</summary>
    public const int DefaultWidth = 360;

    /// <summary>Swift DictationHUDController.panelSize height.</summary>
    public const int DefaultHeight = 56;

    /// <summary>Distance from the working-area's right and bottom edges.</summary>
    public const int DefaultMargin = 16;

    /// <summary>
    /// Bottom-right placement inside <paramref name="workingArea"/> with
    /// <paramref name="margin"/> on the right and bottom. When the window cannot fit
    /// (pathologically small working area), it anchors at the working area's top-left
    /// so it never lands outside the screen.
    /// </summary>
    public static HudRect ComputeBounds(HudRect workingArea, int width, int height, int margin)
    {
        int x = workingArea.Right - width - margin;
        int y = workingArea.Bottom - height - margin;

        // Clamp with a valid range even when the window exceeds the working area.
        x = Math.Clamp(x, workingArea.X, Math.Max(workingArea.X, workingArea.Right - width));
        y = Math.Clamp(y, workingArea.Y, Math.Max(workingArea.Y, workingArea.Bottom - height));

        return new HudRect(x, y, width, height);
    }
}

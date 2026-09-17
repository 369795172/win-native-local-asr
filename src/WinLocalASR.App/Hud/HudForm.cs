using System.Drawing.Drawing2D;
using Microsoft.Win32;
using WinLocalASR.Core.Hud;
using WinLocalASR.Core.State;
using WinLocalASR.Resources;
using Timer = System.Windows.Forms.Timer;

namespace WinLocalASR.App;

/// <summary>
/// The Task 9 dictation HUD overlay — a WinForms port of Swift
/// <c>DictationHUDView</c>/<c>DictationHUDController</c>. Window traits (hard
/// requirements): borderless, topmost, <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>
/// (plus click-through <c>WS_EX_TRANSPARENT</c>, Swift <c>ignoresMouseEvents</c>)
/// via <see cref="CreateParams"/>, <c>ShowInTaskbar=false</c>, bottom-right of the
/// primary screen's working area, DPI-scaled (PerMonitorV2 via the app csproj).
/// The window never activates: <see cref="ShowWithoutActivation"/> makes even
/// <c>Visible=true</c> use <c>SW_SHOWNOACTIVATE</c> — callers must never invoke
/// <c>Activate</c>, <c>Focus</c>, <c>Select</c>, or <c>Show(owner)</c> overloads.
/// Content is custom-painted from <see cref="HudViewState"/>; the animation timer
/// repaints at 20 fps only while a live presentation (recording level bars /
/// processing spinner) is visible or a fade is in flight — no busy loops. Layered
/// alpha goes through <c>SetLayeredWindowAttributes</c> directly (never
/// <c>Form.Opacity</c>) so WinForms never strips the custom extended styles.
/// </summary>
internal sealed class HudForm : Form, IHudView
{
    private const int AnimationIntervalMs = 50; // Swift TimelineView 20 fps
    private const double FadeStepPerTick = 0.34; // ≈0.15 s to full (Swift 0.16 s ease-out)
    private const float BodyFontPoints = 9.75f;  // ≈ Swift 13 px at 96 dpi
    private const float GlyphFontPoints = 12f;   // ≈ Swift 16 px
    private const int PaddingX = 16;
    private const int BarAreaWidth = 54;
    private const int BarAreaHeight = 24;
    private const int ProgressStripHeight = 3;

    private const uint LwaAlpha = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    private readonly Timer _animation;
    private HudViewState _state = HudViewState.Hidden;
    private float _scale = 1f;
    private double _alpha = 1d;
    private int _fadeDirection;
    private Font? _bodyFont;
    private Font? _monoFont;
    private Font? _glyphFont;
    private bool _disposed;

    public HudForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        _animation = new Timer { Interval = AnimationIntervalMs };
        _animation.Tick += OnAnimationTick;

        // Swift screenParametersDidChange: keep the HUD on its anchor when displays change.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        DpiChanged += OnDpiChanged;
    }

    /// <summary>The no-activate discipline: WinForms routes Visible=true through SW_SHOWNOACTIVATE.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= HudWindowStyles.RequiredExStyle;
            return cp;
        }
    }

    // ---- IHudView ----

    public void Apply(HudViewState state)
    {
        _state = state;
        if (IsHandleCreated)
        {
            Invalidate();
        }

        SyncAnimation();
    }

    public void ShowHud()
    {
        if (_disposed)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            _ = Handle; // OnHandleCreated applies DPI scaling before we measure/place
        }

        Reposition();
        if (Visible)
        {
            SetAlpha(1d);
            _fadeDirection = 0;
        }
        else
        {
            SetAlpha(0d);
            _fadeDirection = 1;
            Visible = true; // SW_SHOWNOACTIVATE via ShowWithoutActivation
        }

        SyncAnimation();
    }

    public void HideHud()
    {
        if (_disposed || !Visible)
        {
            return;
        }

        // Fade out from the last painted content (Swift hide() animates alpha to 0
        // before orderOut — the presenter deliberately does not Apply(Hidden) here).
        _fadeDirection = -1;
        SyncAnimation();
    }

    // ---- Animation / fade ----

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_fadeDirection != 0)
        {
            double next = _alpha + (_fadeDirection * FadeStepPerTick);
            if (_fadeDirection > 0 && next >= 1d)
            {
                SetAlpha(1d);
                _fadeDirection = 0;
            }
            else if (_fadeDirection < 0 && next <= 0d)
            {
                SetAlpha(0d);
                _fadeDirection = 0;
                Visible = false;
            }
            else
            {
                SetAlpha(next);
            }
        }

        Invalidate(); // live presentations repaint at 20 fps
        SyncAnimation();
    }

    /// <summary>The timer runs only while something actually animates — never a busy loop.</summary>
    private void SyncAnimation() =>
        _animation.Enabled = !_disposed
            && (_fadeDirection != 0 || (Visible && (_state.TimerVisible || _state.LevelBarVisible)));

    private void SetAlpha(double alpha)
    {
        _alpha = Math.Clamp(alpha, 0d, 1d);
        if (IsHandleCreated)
        {
            SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(_alpha * 255), LwaAlpha);
        }
    }

    // ---- Placement / DPI ----

    private void Reposition()
    {
        Screen? screen = Screen.PrimaryScreen;
        if (screen is null)
        {
            return;
        }

        System.Drawing.Rectangle wa = screen.WorkingArea;
        int margin = Scaled(HudPlacement.DefaultMargin);
        HudRect bounds = HudPlacement.ComputeBounds(
            new HudRect(wa.X, wa.Y, wa.Width, wa.Height),
            Width,
            Height,
            margin);
        DesktopBounds = new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Visible)
        {
            Reposition(); // Swift repositions only while the panel is visible
        }
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        ApplyDpiScaling(e.DeviceDpiNew);
        Reposition();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiScaling(DeviceDpi);
    }

    private void ApplyDpiScaling(int deviceDpi)
    {
        float scale = deviceDpi / 96f;
        if (Math.Abs(scale - _scale) < 0.01f)
        {
            return;
        }

        _scale = scale;
        ClientSize = new Size(Scaled(HudPlacement.DefaultWidth), Scaled(HudPlacement.DefaultHeight));
        _bodyFont?.Dispose();
        _monoFont?.Dispose();
        _glyphFont?.Dispose();
        _bodyFont = new Font("Segoe UI", BodyFontPoints * _scale, FontStyle.Regular, GraphicsUnit.Point);
        _monoFont = new Font("Consolas", BodyFontPoints * _scale, FontStyle.Regular, GraphicsUnit.Point);
        _glyphFont = new Font("Segoe UI Symbol", GlyphFontPoints * _scale, FontStyle.Bold, GraphicsUnit.Point);
    }

    private int Scaled(int logical) => (int)Math.Round(logical * _scale);

    // ---- Painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        int radius = Scaled(20);
        using GraphicsPath card = RoundedCard(client, radius);
        g.FillPath(SystemBrushes.Window, card);
        using (Pen separator = new(Color.FromArgb(178, SystemColors.ControlDark), 1f))
        {
            g.DrawPath(separator, card);
        }

        Color accent = AccentColor(_state.Accent);
        if (accent != Color.Transparent)
        {
            using Pen accentPen = new(accent, Math.Max(1.5f, 1.5f * _scale));
            using GraphicsPath inner = RoundedCard(Rectangle.Inflate(client, -Scaled(2), -Scaled(2)), Math.Max(1, radius - Scaled(2)));
            g.DrawPath(accentPen, inner);
        }

        switch (_state)
        {
            case { LevelBarVisible: true, LevelBarSpinnerMode: false }:
                PaintRecording(g, client);
                break;
            case { LevelBarVisible: true, LevelBarSpinnerMode: true }:
                PaintProcessing(g, client);
                break;
            case { Glyph: not HudGlyph.None }:
                PaintFeedback(g, client);
                break;
        }
    }

    private void PaintRecording(Graphics g, Rectangle client)
    {
        int rowHeight = client.Height - Scaled(ProgressStripHeight);
        int centerY = rowHeight / 2;

        // Red recording dot (Swift Circle().fill(.red), 8×8)
        int dot = Scaled(8);
        using (SolidBrush red = new(Color.FromArgb(220, 38, 38)))
        {
            g.FillEllipse(red, client.X + Scaled(PaddingX), centerY - dot / 2, dot, dot);
        }

        PaintBars(
            g,
            x: client.X + Scaled(PaddingX) + dot + Scaled(12),
            centerY: centerY,
            heights: HudMetrics.RecordingBarHeights(_state.Level, Scaled(BarAreaHeight)),
            color: Color.FromArgb(220, 38, 38));

        int labelX = client.X + Scaled(PaddingX) + dot + Scaled(12) + Scaled(BarAreaWidth) + Scaled(12);
        DrawBodyText(g, Strings.Hud_Recording, labelX, 0, client.Right - Scaled(PaddingX) - TimerTextWidth(g) - Scaled(8));

        if (_state.TimerVisible && _state.TimerText is { } timer)
        {
            Size textSize = TextRenderer.MeasureText(g, timer, _monoFont);
            Rectangle timerBounds = new(
                client.Right - Scaled(PaddingX) - textSize.Width,
                0,
                textSize.Width,
                rowHeight);
            TextRenderer.DrawText(
                g,
                timer,
                _monoFont!,
                timerBounds,
                SystemColors.GrayText,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
        }

        // Progress capsule along the bottom edge (Swift Capsule, 3 pt, red fill on grey track)
        if (_state.ProgressVisible)
        {
            int stripY = client.Bottom - Scaled(ProgressStripHeight);
            using SolidBrush track = new(Color.FromArgb(220, 220, 220));
            g.FillRectangle(track, client.X, stripY, client.Width, Scaled(ProgressStripHeight));
            using SolidBrush fill = new(Color.FromArgb(220, 38, 38));
            g.FillRectangle(fill, client.X, stripY, (int)(client.Width * Math.Clamp(_state.Progress, 0d, 1d)), Scaled(ProgressStripHeight));
        }
    }

    private void PaintProcessing(Graphics g, Rectangle client)
    {
        int centerY = client.Height / 2;
        double elapsed = _state.ProcessingStartedUtc is { } started
            ? (DateTime.UtcNow - started).TotalSeconds
            : 0d;

        PaintBars(
            g,
            x: client.X + Scaled(PaddingX),
            centerY: centerY,
            heights: HudMetrics.ProcessingBarHeights(elapsed, Scaled(BarAreaHeight)),
            color: Color.FromArgb(245, 158, 11));

        int labelX = client.X + Scaled(PaddingX) + Scaled(BarAreaWidth) + Scaled(12);
        string timer = _state.ProcessingStartedUtc is { } processingStarted
            ? HudShellPresenter.FormatProcessingDuration(processingStarted, DateTime.UtcNow)
            : HudText.FormatHudDuration(TimeSpan.Zero);
        Size textSize = TextRenderer.MeasureText(g, timer, _monoFont);
        DrawBodyText(g, Strings.Hud_Transcribing, labelX, 0, client.Right - Scaled(PaddingX) - textSize.Width - Scaled(8));
        Rectangle timerBounds = new(
            client.Right - Scaled(PaddingX) - textSize.Width,
            0,
            textSize.Width,
            client.Height);
        TextRenderer.DrawText(
            g,
            timer,
            _monoFont!,
            timerBounds,
            SystemColors.GrayText,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
    }

    private void PaintFeedback(Graphics g, Rectangle client)
    {
        (string glyph, Color color, string text) = (_state.Glyph, _state.Message) switch
        {
            (HudGlyph.Check, _) => ("✓", Color.FromArgb(34, 139, 58), Strings.Hud_Copied),
            (HudGlyph.Cancel, _) => ("↯", SystemColors.GrayText, Strings.Hud_Cancelled),
            (HudGlyph.Error, { } message) => ("✕", Color.FromArgb(220, 38, 38), message),
            (HudGlyph.Error, null) => ("✕", Color.FromArgb(220, 38, 38), string.Empty),
            _ => (string.Empty, SystemColors.GrayText, string.Empty),
        };

        if (glyph.Length == 0)
        {
            return;
        }

        Size glyphSize = TextRenderer.MeasureText(g, glyph, _glyphFont);
        Rectangle glyphBounds = new(client.X + Scaled(PaddingX), 0, glyphSize.Width, client.Height);
        TextRenderer.DrawText(
            g,
            glyph,
            _glyphFont!,
            glyphBounds,
            color,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int textX = client.X + Scaled(PaddingX) + glyphSize.Width + Scaled(10);
        Rectangle textBounds = new(textX, 0, client.Right - Scaled(PaddingX) - textX, client.Height);
        if (textBounds.Width > 0)
        {
            TextRenderer.DrawText(
                g,
                text,
                _bodyFont!,
                textBounds,
                SystemColors.WindowText,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    private void PaintBars(Graphics g, int x, int centerY, double[] heights, Color color)
    {
        int barWidth = Scaled(3);
        int areaWidth = Scaled(BarAreaWidth);
        double gap = heights.Length > 1 ? (areaWidth - heights.Length * barWidth) / (double)(heights.Length - 1) : 0;
        using SolidBrush brush = new(color);
        for (int i = 0; i < heights.Length; i++)
        {
            int h = Math.Max(1, (int)Math.Round(heights[i]));
            int barX = x + (int)Math.Round(i * (barWidth + gap));
            using GraphicsPath capsule = RoundedCapsule(barX, centerY - h / 2, barWidth, h);
            g.FillPath(brush, capsule);
        }
    }

    private void DrawBodyText(Graphics g, string text, int x, int y, int rightBound)
    {
        int width = rightBound - x;
        if (width <= 0)
        {
            return;
        }

        Rectangle bounds = new(x, y, width, Scaled(HudPlacement.DefaultHeight));
        TextRenderer.DrawText(
            g,
            text,
            _bodyFont!,
            bounds,
            SystemColors.WindowText,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private int TimerTextWidth(Graphics g) =>
        _state.TimerText is { } timer ? TextRenderer.MeasureText(g, timer, _monoFont).Width : 0;

    private static Color AccentColor(HudAccent accent) => accent switch
    {
        HudAccent.Red => Color.FromArgb(220, 38, 38),
        HudAccent.Amber => Color.FromArgb(245, 158, 11),
        HudAccent.Green => Color.FromArgb(34, 139, 58),
        HudAccent.Grey => SystemColors.GrayText,
        _ => Color.Transparent,
    };

    private static GraphicsPath RoundedCard(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedCapsule(int x, int y, int width, int height)
    {
        GraphicsPath path = new();
        int d = Math.Max(1, Math.Min(width, height));
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- Teardown ----

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _animation.Stop();
            _animation.Dispose();
            _bodyFont?.Dispose();
            _monoFont?.Dispose();
            _glyphFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}

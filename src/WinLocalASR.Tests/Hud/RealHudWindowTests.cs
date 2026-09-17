using System.Runtime.InteropServices;
using WinLocalASR.Core.Hud;
using Xunit;

namespace WinLocalASR.Tests.Hud;

/// <summary>
/// Windows-only evidence for the HUD window traits. The Tests project (net10.0)
/// cannot reference the net10.0-windows App project where <c>HudForm</c> lives
/// (Task 7/8 NU1202 precedent), so these facts execute against a REAL native
/// window created with the shipped constants: <see cref="HudWindowStyles"/> is the
/// single source both the form's <c>CreateParams</c> and this test consume, and
/// <see cref="HudPlacement.ComputeBounds"/> is the same placement function the form
/// uses against the primary screen working area. The WinForms-side wiring is
/// covered by the presenter tests plus the Task 12 ControlServer e2e.
/// </summary>
public sealed class RealHudWindowTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int SwpNoSize = 0x0001;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SpiGetWorkArea = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(int action, int uiParam, ref RECT pvParam, int winIni);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static IntPtr CreateHudWindow()
    {
        IntPtr hwnd = CreateWindowExW(
            HudWindowStyles.RequiredExStyle,
            "STATIC",
            string.Empty,
            WsPopup,
            0,
            0,
            HudPlacement.DefaultWidth,
            HudPlacement.DefaultHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        return hwnd;
    }

    [SkippableFact]
    public void Shipped_ex_styles_land_on_a_real_window_without_taking_focus()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        IntPtr hwnd = CreateHudWindow();
        try
        {
            ShowWindow(hwnd, HudWindowStyles.SwShowNoActivate);

            int exStyle = GetWindowLongW(hwnd, HudWindowStyles.GwlExStyle);
            Assert.Equal(
                HudWindowStyles.RequiredExStyle,
                exStyle & HudWindowStyles.RequiredExStyle);

            // The no-activate discipline: showing never moved the foreground window.
            Assert.NotEqual(hwnd, GetForegroundWindow());
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [SkippableFact]
    public void Placement_lands_a_real_window_bottom_right_inside_the_primary_working_area()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        RECT workArea = default;
        Assert.True(SystemParametersInfoW(SpiGetWorkArea, 0, ref workArea, 0));
        var area = new HudRect(
            workArea.Left,
            workArea.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top);

        HudRect expected = HudPlacement.ComputeBounds(
            area,
            HudPlacement.DefaultWidth,
            HudPlacement.DefaultHeight,
            HudPlacement.DefaultMargin);

        IntPtr hwnd = CreateHudWindow();
        try
        {
            Assert.True(SetWindowPos(
                hwnd,
                IntPtr.Zero,
                expected.X,
                expected.Y,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate));

            Assert.True(GetWindowRect(hwnd, out RECT actual));
            Assert.Equal(expected.X, actual.Left);
            Assert.Equal(expected.Y, actual.Top);
            Assert.True(actual.Left >= area.X && actual.Top >= area.Y, $"outside: {actual.Left},{actual.Top}");
            Assert.True(actual.Right <= area.Right && actual.Bottom <= area.Bottom, $"outside: {actual.Right},{actual.Bottom}");
            Assert.Equal(HudPlacement.DefaultMargin, area.Right - actual.Right);
            Assert.Equal(HudPlacement.DefaultMargin, area.Bottom - actual.Bottom);
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }
}

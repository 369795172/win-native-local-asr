using WinLocalASR.Core.Hud;
using Xunit;

namespace WinLocalASR.Tests.Hud;

public sealed class HudPlacementTests
{
    [Fact]
    public void Anchors_bottom_right_with_margin()
    {
        HudRect workingArea = new(0, 0, 1920, 1040);

        HudRect bounds = HudPlacement.ComputeBounds(workingArea, 360, 56, 16);

        Assert.Equal(1920 - 360 - 16, bounds.X);
        Assert.Equal(1040 - 56 - 16, bounds.Y);
        Assert.Equal(360, bounds.Width);
        Assert.Equal(56, bounds.Height);
    }

    [Fact]
    public void Anchors_bottom_right_of_offset_working_area()
    {
        // Multi-monitor virtual coordinates: a secondary screen left of the primary.
        HudRect workingArea = new(-1920, 0, 1920, 1040);

        HudRect bounds = HudPlacement.ComputeBounds(workingArea, 360, 56, 16);

        Assert.Equal(-1920 + 1920 - 360 - 16, bounds.X);
        Assert.Equal(1040 - 56 - 16, bounds.Y);
    }

    [Theory]
    [InlineData(1920, 1040)]
    [InlineData(1366, 728)]
    [InlineData(2560, 1400)]
    [InlineData(1920, 300, -3840, 500)] // offset + smaller monitor above-left in the virtual desktop
    public void Window_stays_fully_inside_working_area(int areaWidth, int areaHeight, int areaX = 0, int areaY = 0)
    {
        HudRect workingArea = new(areaX, areaY, areaWidth, areaHeight);

        HudRect bounds = HudPlacement.ComputeBounds(workingArea, 360, 56, 16);

        Assert.True(bounds.X >= workingArea.X, $"x {bounds.X} < {workingArea.X}");
        Assert.True(bounds.Y >= workingArea.Y, $"y {bounds.Y} < {workingArea.Y}");
        Assert.True(bounds.Right <= workingArea.Right, $"right {bounds.Right} > {workingArea.Right}");
        Assert.True(bounds.Bottom <= workingArea.Bottom, $"bottom {bounds.Bottom} > {workingArea.Bottom}");
        Assert.Equal(16, workingArea.Right - bounds.Right);
        Assert.Equal(16, workingArea.Bottom - bounds.Bottom);
    }

    [Fact]
    public void Oversized_window_anchors_top_left_instead_of_escaping()
    {
        HudRect workingArea = new(100, 50, 200, 40); // smaller than 360×56 in both axes

        HudRect bounds = HudPlacement.ComputeBounds(workingArea, 360, 56, 16);

        Assert.Equal(workingArea.X, bounds.X);
        Assert.Equal(workingArea.Y, bounds.Y);
    }

    [Fact]
    public void Defaults_match_swift_panel_geometry()
    {
        Assert.Equal(360, HudPlacement.DefaultWidth);
        Assert.Equal(56, HudPlacement.DefaultHeight);
    }
}

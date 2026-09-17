using WinLocalASR.Core.Hud;
using Xunit;

namespace WinLocalASR.Tests.Hud;

/// <summary>
/// Level-bar + progress transforms, ported from Swift HUDLevelBars.barHeight
/// (DictationHUD.swift lines 218-234) and recordingProgress (lines 156-159).
/// </summary>
public sealed class HudMetricsTests
{
    // ---- Recording bars ----

    [Fact]
    public void Recording_bars_at_full_level_follow_swift_pattern()
    {
        double[] heights = HudMetrics.RecordingBarHeights(1f, 100);

        double[] expected = { 35, 55, 80, 62, 100, 68, 86, 52, 38 };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], heights[i], 5);
        }
    }

    [Fact]
    public void Recording_bars_scale_with_level()
    {
        double[] heights = HudMetrics.RecordingBarHeights(0.5f, 100);
        Assert.Equal(50, heights[4], 5); // tallest bar: maximum × amplitude × 1.0
        Assert.Equal(17.5, heights[0], 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    public void Recording_bars_floor_silence_at_minimum_amplitude(float level)
    {
        double[] heights = HudMetrics.RecordingBarHeights(level, 100);
        Assert.Equal(12, heights[4], 5); // 100 × 0.12
        Assert.Equal(4.2, heights[0], 5); // 100 × 0.12 × 0.35
    }

    [Fact]
    public void Recording_bars_clamp_level_above_one()
    {
        double[] heights = HudMetrics.RecordingBarHeights(2f, 100);
        Assert.Equal(100, heights[4], 5);
    }

    [Fact]
    public void Recording_bars_enforce_minimum_height()
    {
        // 10 × 1.0 × 0.35 = 3.5 < 4 → floored (Swift max(4, …))
        double[] heights = HudMetrics.RecordingBarHeights(1f, 10);
        Assert.Equal(4, heights[0]);
        Assert.Equal(5.5, heights[1], 5);
    }

    // ---- Processing spinner bars ----

    [Fact]
    public void Processing_bars_at_zero_elapsed_peak_at_first_bar()
    {
        double[] heights = HudMetrics.ProcessingBarHeights(0, 100);

        Assert.Equal(100, heights[0], 5);            // distance 0 → full
        Assert.Equal(68, heights[1], 5);             // distance 1 → 0.2 + 0.8×0.6
        Assert.Equal(36, heights[2], 5);             // distance 2 → 0.2 + 0.8×0.2
        Assert.Equal(20, heights[4], 5);             // distance 4 ≥ 2.5 → idle floor
        Assert.Equal(20, heights[5], 5);
        Assert.Equal(68, heights[8], 5);             // ring wraparound: distance min(8, 1) = 1
        Assert.Equal(heights[1], heights[8], 5);     // symmetric around position 0
    }

    [Fact]
    public void Processing_bars_sweep_wraps_the_ring()
    {
        // 1.5 s × 6 bars/s = position 9 → ring-wraps to 0, identical to t = 0.
        double[] atZero = HudMetrics.ProcessingBarHeights(0, 100);
        double[] wrapped = HudMetrics.ProcessingBarHeights(1.5, 100);
        for (int i = 0; i < atZero.Length; i++)
        {
            Assert.Equal(atZero[i], wrapped[i], 5);
        }
    }

    [Fact]
    public void Processing_bars_peak_at_swept_position()
    {
        // 3.5 s × 6 = 21 → position 21 % 9 = 3.
        double[] heights = HudMetrics.ProcessingBarHeights(3.5, 100);
        Assert.Equal(100, heights[3], 5);
        Assert.Equal(68, heights[2], 5);
        Assert.Equal(68, heights[4], 5);
        Assert.Equal(20, heights[0], 5);
        Assert.Equal(20, heights[7], 5); // distance |7-3| = 4 → floor (ring min is 4)
    }

    [Fact]
    public void Processing_bars_enforce_minimum_height()
    {
        double[] heights = HudMetrics.ProcessingBarHeights(0, 5);
        Assert.Equal(5, heights[0]);   // 5 × 1.0
        Assert.Equal(4, heights[3]);   // 5 × 0.2 = 1 < 4 → floored
    }

    // ---- Recording progress ----

    [Theory]
    [InlineData(0, 120, 0)]
    [InlineData(60, 120, 0.5)]
    [InlineData(120, 120, 1)]
    [InlineData(240, 120, 1)]   // clamped
    [InlineData(-5, 120, 0)]    // negative duration clamped
    [InlineData(50, 0, 0)]      // guard: limit <= 0 (Swift guard recordingLimit > 0)
    [InlineData(50, -3, 0)]
    public void Recording_progress_clamps_to_unit_interval(double duration, double limit, double expected)
    {
        Assert.Equal(expected, HudMetrics.RecordingProgress(duration, limit), 5);
    }
}

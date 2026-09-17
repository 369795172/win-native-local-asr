using WinLocalASR.Core.State;
using Xunit;

namespace WinLocalASR.Tests.State;

public sealed class HudPresentationTests
{
    [Theory]
    [InlineData("recording", "recording")]
    [InlineData("processing", "processing")]
    [InlineData("copied", "copied")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("error", "error")]
    [InlineData("hidden", "hidden")]
    public void ControlValue_strings_match_the_wire_contract(string presentationLabel, string expected)
    {
        DictationHudPresentation presentation = presentationLabel switch
        {
            "recording" => DictationHudPresentation.Recording,
            "processing" => DictationHudPresentation.Processing,
            "copied" => DictationHudPresentation.Copied,
            "cancelled" => DictationHudPresentation.Cancelled,
            "error" => DictationHudPresentation.Error("boom"),
            _ => DictationHudPresentation.Hidden,
        };
        Assert.Equal(expected, presentation.ControlValue);
    }

    [Theory]
    [InlineData(ClipboardOutputState.None, false, "hidden")]
    [InlineData(ClipboardOutputState.Copied, false, "copied")]
    [InlineData(ClipboardOutputState.Failed, false, "error")] // clipboard failure renders as error — no failed value
    [InlineData(ClipboardOutputState.None, true, "cancelled")]
    [InlineData(ClipboardOutputState.Copied, true, "cancelled")] // cancel feedback wins over clipboard in idle
    [InlineData(ClipboardOutputState.Failed, true, "cancelled")]
    public void Resolve_in_idle_combines_clipboard_and_cancel_feedback(
        ClipboardOutputState clipboard, bool cancelActive, string expectedControlValue)
    {
        DictationHudPresentation resolved =
            DictationHudPresentation.Resolve(AppPhase.Idle, clipboard, cancelActive);
        Assert.Equal(expectedControlValue, resolved.ControlValue);
    }

    [Theory]
    [InlineData(ClipboardOutputState.None)]
    [InlineData(ClipboardOutputState.Copied)]
    [InlineData(ClipboardOutputState.Failed)]
    public void Resolve_phase_dominates_clipboard_state(ClipboardOutputState clipboard)
    {
        Assert.True(DictationHudPresentation.Resolve(AppPhase.Recording, clipboard) is DictationHudPresentation.RecordingPhase);
        Assert.True(DictationHudPresentation.Resolve(AppPhase.Processing, clipboard) is DictationHudPresentation.ProcessingPhase);
        Assert.True(DictationHudPresentation.Resolve(AppPhase.Loading, clipboard) is DictationHudPresentation.HiddenPhase);
    }

    [Fact]
    public void Resolve_error_phase_carries_the_message()
    {
        DictationHudPresentation.ErrorPhase resolved =
            Assert.IsType<DictationHudPresentation.ErrorPhase>(
                DictationHudPresentation.Resolve(AppPhase.Error("disk full"), ClipboardOutputState.Copied));
        Assert.Equal("disk full", resolved.Message);
        Assert.Equal("error", resolved.ControlValue);
    }

    [Fact]
    public void Resolve_failed_clipboard_renders_error_with_failure_message()
    {
        DictationHudPresentation.ErrorPhase resolved =
            Assert.IsType<DictationHudPresentation.ErrorPhase>(
                DictationHudPresentation.Resolve(AppPhase.Idle, ClipboardOutputState.Failed));
        Assert.Equal(StateStrings.ClipboardCopyFailed, resolved.Message);
        Assert.True(resolved.IsVisible);
    }

    [Fact]
    public void NeedsTimelineUpdates_only_for_recording_and_processing()
    {
        Assert.True(DictationHudPresentation.Recording.NeedsTimelineUpdates);
        Assert.True(DictationHudPresentation.Processing.NeedsTimelineUpdates);
        Assert.False(DictationHudPresentation.Copied.NeedsTimelineUpdates);
        Assert.False(DictationHudPresentation.Cancelled.NeedsTimelineUpdates);
        Assert.False(DictationHudPresentation.Hidden.NeedsTimelineUpdates);
        Assert.False(DictationHudPresentation.Error("x").NeedsTimelineUpdates);
    }

    [Fact]
    public void IsVisible_is_false_only_for_hidden()
    {
        Assert.False(DictationHudPresentation.Hidden.IsVisible);
        Assert.True(DictationHudPresentation.Recording.IsVisible);
        Assert.True(DictationHudPresentation.Processing.IsVisible);
        Assert.True(DictationHudPresentation.Copied.IsVisible);
        Assert.True(DictationHudPresentation.Cancelled.IsVisible);
        Assert.True(DictationHudPresentation.Error("x").IsVisible);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    [InlineData(59.9, "00:59")]
    [InlineData(65, "01:05")]
    [InlineData(125.9, "02:05")]
    [InlineData(3600, "60:00")]
    [InlineData(-3, "00:00")] // Swift max(0, Int(duration))
    public void FormatHudDuration_matches_swift(double seconds, string expected)
    {
        Assert.Equal(expected, HudText.FormatHudDuration(TimeSpan.FromSeconds(seconds)));
    }
}

using WinLocalASR.Core.Output;
using Xunit;

namespace WinLocalASR.Tests.Output;

public class ClipboardWriterTests
{
    [Fact]
    public void Fails_twice_then_succeeds_returns_success_with_three_attempts()
    {
        var delays = new List<int>();
        var interop = new FakeClipboardInterop(failuresBeforeSuccess: 2);
        var writer = new ClipboardWriter(interop, delay: delays.Add);

        bool success = writer.SetText("你好世界");

        Assert.True(success);
        Assert.Equal(3, interop.Attempts.Count);
        Assert.All(interop.Attempts, attempt => Assert.Equal("你好世界", attempt));
        Assert.Equal(new[] { 50, 50 }, delays); // ~50 ms gap after each failed attempt, none after the last
    }

    [Fact]
    public void All_attempts_fail_returns_failure_after_exactly_three_attempts()
    {
        var delays = new List<int>();
        var interop = new FakeClipboardInterop(failuresBeforeSuccess: int.MaxValue);
        var writer = new ClipboardWriter(interop, delay: delays.Add);

        bool success = writer.SetText("hello");

        Assert.False(success);
        Assert.Equal(3, interop.Attempts.Count);
        Assert.Equal(new[] { 50, 50 }, delays);
    }

    [Fact]
    public void First_attempt_succeeds_without_retry_delay()
    {
        var delays = new List<int>();
        var interop = new FakeClipboardInterop(failuresBeforeSuccess: 0);
        var writer = new ClipboardWriter(interop, delay: delays.Add);

        bool success = writer.SetText("ok");

        Assert.True(success);
        Assert.Single(interop.Attempts);
        Assert.Empty(delays);
    }

    /// <summary>
    /// Windows-only real-interop smoke. Self-skips when the clipboard is
    /// unavailable (headless CI sessions without an interactive window station).
    /// This is a smoke only — full real-clipboard verification (paste into other
    /// apps) is a real-machine acceptance item.
    /// </summary>
    [SkippableFact]
    public void Real_win32_interop_roundtrips_text()
    {
        Skip.If(!OperatingSystem.IsWindows());

        string marker = $"winlocalasr-smoke-{Guid.NewGuid():N}";
        var interop = new Win32ClipboardInterop();

        bool set = interop.TrySetText(marker);
        Skip.If(!set, "clipboard unavailable in this session");

        bool got = interop.TryGetText(out string? text);
        Skip.If(!got, "clipboard readable check unavailable in this session");

        Assert.Equal(marker, text);
    }

    private sealed class FakeClipboardInterop(int failuresBeforeSuccess) : IClipboardInterop
    {
        public List<string> Attempts { get; } = new();

        public bool TrySetText(string text)
        {
            Attempts.Add(text);
            return Attempts.Count > failuresBeforeSuccess;
        }

        public bool TryGetText(out string? text)
        {
            text = null;
            return false;
        }
    }
}

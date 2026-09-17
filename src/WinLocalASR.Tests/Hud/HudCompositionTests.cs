using WinLocalASR.Core.Hud;
using WinLocalASR.Tests.Shell;
using WinLocalASR.Tests.State;
using Xunit;

namespace WinLocalASR.Tests.Hud;

/// <summary>
/// QA- GUI-degrade evidence for the HUD (mirrors the Task 7 tray degrade test): a
/// throwing HUD factory must leave the composition intact — warning logged, no
/// exception escapes, no disposable handed back.
/// </summary>
public sealed class HudCompositionTests
{
    [Fact]
    public void Throwing_factory_degrades_to_warning_and_null()
    {
        ControllerHarness harness = new();
        ThrowingHudFactory factory = new();
        FakeShellLog log = new();

        IDisposable? created = HudComposition.CreateDegrading(factory, harness.Controller, log);

        Assert.Null(created);
        Assert.Equal(1, factory.Calls);
        Assert.Single(log.Warnings);
        Assert.Contains("dictation HUD unavailable", log.Warnings[0]);
        Assert.Contains("no desktop window station", log.Warnings[0]);
    }

    [Fact]
    public void Working_factory_returns_disposable_without_warnings()
    {
        ControllerHarness harness = new();
        WorkingHudFactory factory = new();
        FakeShellLog log = new();

        IDisposable? created = HudComposition.CreateDegrading(factory, harness.Controller, log);

        Assert.NotNull(created);
        Assert.Same(harness.Controller, Assert.Single(factory.Controllers));
        Assert.Empty(log.Warnings);
        created!.Dispose();
    }
}

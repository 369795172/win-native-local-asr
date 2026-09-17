using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.Shell;
using Xunit;

namespace WinLocalASR.Tests.Hotkeys;

/// <summary>
/// Real <c>RegisterHotKey</c> verification (windows-latest / real machine only):
/// (1) the register → unregister roundtrip holds, and (2) a hotkey combination
/// occupied up front — the TEST takes the role of the conflicting program — makes
/// the manager's registration fail and surface Conflict with a balloon, proving
/// QA- without needing another application.
/// </summary>
public class RealHotkeyTests
{
    // CA1416: Win32HotkeyRegistrar is [SupportedOSPlatform("windows")]; the runtime
    // guard is the Skip.If below (RegistryKeyFactory precedent).
#pragma warning disable CA1416

    private const uint CtrlShift = 0x0002 | 0x0004; // MOD_CONTROL | MOD_SHIFT
    private const uint VkSpace = 0x20;

    [SkippableFact]
    public void Register_then_unregister_roundtrip_succeeds_without_throwing()
    {
        Skip.If(!OperatingSystem.IsWindows());

        Win32HotkeyRegistrar registrar = new(IntPtr.Zero);

        Assert.True(registrar.Register(99, CtrlShift, VkSpace));
        Assert.True(registrar.Unregister(99));
    }

    [SkippableFact]
    public void Occupied_combination_makes_the_manager_surface_conflict_and_balloon()
    {
        Skip.If(!OperatingSystem.IsWindows());

        // The test itself occupies Ctrl+Shift+Space (id 99, thread-bound): another
        // program holding the user's chosen hotkey.
        Win32HotkeyRegistrar occupier = new(IntPtr.Zero);
        Assert.True(occupier.Register(99, CtrlShift, VkSpace));

        try
        {
            List<string> balloons = new();
            FakeShellLog log = new();
            HotkeyManager manager = new(
                new Win32HotkeyRegistrar(IntPtr.Zero),
                new InlineDispatcher(),
                () => "Ctrl+Shift+Space",
                () => { },
                () => { },
                log,
                balloons.Add);

            manager.Initialize();

            Assert.Equal(HotkeyRegistrationStatus.Conflict, manager.Status);
            Assert.Single(balloons);
            Assert.Contains(balloons, b => b.Contains("already in use"));
        }
        finally
        {
            occupier.Unregister(99);
        }
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }
}

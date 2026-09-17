using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Hud;

/// <summary>
/// GUI-degrade composition for the HUD (the Task 7 hard requirement extends to every
/// GUI construct): a throwing HUD factory degrades to a logged warning and a null
/// result — the tray/controller/message loop composition continues, the process
/// stays alive (headless CI runners hit exactly this path).
/// </summary>
public static class HudComposition
{
    public static IDisposable? CreateDegrading(IHudShellFactory factory, AppController controller, IShellLog log)
    {
        try
        {
            return factory.CreateHud(controller);
        }
        catch (Exception ex)
        {
            log.Warn($"dictation HUD unavailable, continuing without it: {ex.Message}");
            return null;
        }
    }
}

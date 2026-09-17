namespace WinLocalASR.App;

internal static class Program
{
    // Minimal entry point for the bootstrap skeleton: keeps the WinForms target compiling
    // while the tray shell, HUD, and hotkey wiring arrive in later milestones.
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
    }
}

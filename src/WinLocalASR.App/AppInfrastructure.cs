using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.App;

/// <summary>
/// Best-effort file log (%APPDATA%\WinLocalASR\shell.log) — the tray app has no console,
/// and the GUI-degrade requirement demands degradation be observable (headless CI runs
/// read this to see why a tray vanished). Logging must never crash the shell.
/// </summary>
internal sealed class FileShellLog : IShellLog
{
    private readonly string _path;

    public FileShellLog()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinLocalASR");
        _path = Path.Combine(directory, "shell.log");
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        try
        {
            File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // A read-only %APPDATA% or a full disk must not take the app down.
        }

        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
    }
}

/// <summary>
/// <see cref="IMessageLoop"/> over the WinForms pump: <see cref="Run"/> blocks the UI
/// thread until <see cref="Stop"/> posts <see cref="Application.ExitThread"/> through the
/// context captured at construction (the UI thread). Keeps the process alive when the
/// tray degraded away — the headless survival the GUI-degrade requirement demands.
/// </summary>
internal sealed class WinFormsMessageLoop : IMessageLoop
{
    private readonly SynchronizationContext _ui;
    private volatile bool _stopped;

    public WinFormsMessageLoop() => _ui = SynchronizationContext.Current ?? new SynchronizationContext();

    public void Run()
    {
        if (_stopped)
        {
            return;
        }

        Application.Run();
    }

    public void Stop()
    {
        _stopped = true;
        _ui.Post(_ => Application.ExitThread(), null);
    }
}

/// <summary>Composes the WinForms tray: view + Core presenter, wired to the controller.</summary>
internal sealed class WinTrayShellFactory : ITrayShellFactory
{
    private readonly IShellLog _log;

    public WinTrayShellFactory(IShellLog log) => _log = log;

    public IDisposable CreateTray(
        AppController controller,
        ISetupDialogFactory setupDialogFactory,
        ISettingsDialogFactory settingsDialogFactory,
        Action requestExit) =>
        new TrayShellPresenter(
            controller,
            new TrayShell(_log),
            setupDialogFactory,
            settingsDialogFactory,
            requestExit,
            _log);
}

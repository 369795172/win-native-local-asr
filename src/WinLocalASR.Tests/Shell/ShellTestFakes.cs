using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.Tests.Shell;

/// <summary>Wires an <see cref="AppBootstrapper"/> with every fake seam; tests override what they care about.</summary>
public static class ShellTestHost
{
    public static AppBootstrapper CreateBootstrapper(
        IAppServiceGraphFactory graphFactory,
        out FakeMessageLoop loop,
        ITrayShellFactory? trayFactory = null,
        FakeSetupDialogFactory? setupDialogFactory = null,
        FakeShellLog? log = null,
        bool mutexAcquire = true)
    {
        loop = new FakeMessageLoop();
        return new AppBootstrapper(new AppBootstrapperDependencies
        {
            MutexFactory = new FakeMutexFactory(mutexAcquire),
            ServiceGraphFactory = graphFactory,
            TrayShellFactory = trayFactory ?? new FakeTrayShellFactory(log ?? new FakeShellLog()),
            SetupDialogFactory = setupDialogFactory ?? new FakeSetupDialogFactory(),
            SettingsDialogFactory = new FakeSettingsDialogFactory(),
            MessageLoop = loop,
            Log = log ?? new FakeShellLog(),
        });
    }
}

public sealed class FakeMutex : IMutex
{
    public bool AcquireResult { get; }

    public Exception? ThrowOnAcquire { get; set; }

    public bool Released { get; private set; }

    public bool Disposed { get; private set; }

    public FakeMutex(bool acquireResult) => AcquireResult = acquireResult;

    public bool TryAcquire()
    {
        if (ThrowOnAcquire is { } ex)
        {
            throw ex;
        }

        return AcquireResult;
    }

    public void Release() => Released = true;

    public void Dispose() => Disposed = true;
}

public sealed class FakeMutexFactory : IMutexFactory
{
    private readonly bool _acquire;

    public Exception? ThrowOnAcquire { get; set; }

    public List<FakeMutex> Created { get; } = new();

    public string? LastName { get; private set; }

    public FakeMutexFactory(bool acquire) => _acquire = acquire;

    public IMutex Create(string name)
    {
        LastName = name;
        FakeMutex mutex = new(_acquire) { ThrowOnAcquire = ThrowOnAcquire };
        Created.Add(mutex);
        return mutex;
    }
}

public sealed class FakeShellLog : IShellLog
{
    public List<string> Infos { get; } = new();

    public List<string> Warnings { get; } = new();

    public void Info(string message) => Infos.Add(message);

    public void Warn(string message) => Warnings.Add(message);
}

/// <summary>Tray factory that always throws — the GUI-degrade scenario (headless CI runner).</summary>
public sealed class ThrowingTrayFactory : ITrayShellFactory
{
    public int Calls { get; private set; }

    public IDisposable CreateTray(
        AppController controller,
        ISetupDialogFactory setupDialogFactory,
        ISettingsDialogFactory settingsDialogFactory,
        Action requestExit)
    {
        Calls++;
        throw new InvalidOperationException("no explorer shell in this session");
    }
}

/// <summary>
/// Composes the REAL <see cref="TrayShellPresenter"/> over a <see cref="FakeTrayShell"/>, so
/// bootstrapper tests exercise the true wiring (menu events → controller → exit path).
/// </summary>
public sealed class FakeTrayShellFactory : ITrayShellFactory
{
    private readonly IShellLog _log;

    public FakeTrayShellFactory(IShellLog log) => _log = log;

    public int Calls { get; private set; }

    public FakeTrayShell? LastShell { get; private set; }

    public Action? CapturedExit { get; private set; }

    public IDisposable CreateTray(
        AppController controller,
        ISetupDialogFactory setupDialogFactory,
        ISettingsDialogFactory settingsDialogFactory,
        Action requestExit)
    {
        Calls++;
        CapturedExit = requestExit;
        LastShell = new FakeTrayShell();
        return new TrayShellPresenter(controller, LastShell, setupDialogFactory, settingsDialogFactory, requestExit, _log);
    }
}

public sealed class FakeSetupDialogFactory : ISetupDialogFactory
{
    public Exception? ThrowOnOpen { get; set; }

    public List<AppController> OpenedControllers { get; } = new();

    public void Open(AppController controller)
    {
        if (ThrowOnOpen is { } ex)
        {
            throw ex;
        }

        OpenedControllers.Add(controller);
    }
}

public sealed class FakeSettingsDialogFactory : ISettingsDialogFactory
{
    public Exception? ThrowOnOpen { get; set; }

    public List<AppController> OpenedControllers { get; } = new();

    public void Open(AppController controller)
    {
        if (ThrowOnOpen is { } ex)
        {
            throw ex;
        }

        OpenedControllers.Add(controller);
    }
}

public sealed class FakeAppServiceGraphFactory : IAppServiceGraphFactory
{
    private readonly AppServiceGraph _graph;

    public FakeAppServiceGraphFactory(AppServiceGraph graph) => _graph = graph;

    public int CreateCalls { get; private set; }

    public AppServiceGraph Create()
    {
        CreateCalls++;
        return _graph;
    }
}

/// <summary>
/// Message-loop fake. Default: Run returns immediately (bootstrapper-completion tests).
/// <see cref="BlockUntilStop"/> spins inside Run until Stop is called — used with
/// Task.Run to prove the loop keeps the process alive until an exit request.
/// </summary>
public sealed class FakeMessageLoop : IMessageLoop
{
    private int _runCount;
    private int _stopCount;

    public bool BlockUntilStop { get; set; }

    public ManualResetEventSlim Entered { get; } = new(false);

    public Action? OnRunEntered { get; set; }

    public int RunCount => Volatile.Read(ref _runCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    public void Run()
    {
        Interlocked.Increment(ref _runCount);
        OnRunEntered?.Invoke();
        Entered.Set();
        if (BlockUntilStop)
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref _stopCount) > 0, 10_000);
        }
    }

    public void Stop() => Interlocked.Increment(ref _stopCount);
}

/// <summary>Records every presenter command; Raise* simulate menu clicks.</summary>
public sealed class FakeTrayShell : ITrayShell
{
    public List<TrayIconKind> Icons { get; } = new();

    public List<(TrayPhaseStatus Phase, string? Detail)> Tooltips { get; } = new();

    public List<TrayStatus> Statuses { get; } = new();

    public List<bool> SetupHighlights { get; } = new();

    public List<bool> CopyEnabled { get; } = new();

    public List<string> Balloons { get; } = new();

    public bool Disposed { get; private set; }

    public event Action? SetupRequested;
    public event Action? SettingsRequested;
    public event Action? CopyLastTranscriptRequested;
    public event Action? RestartEngineRequested;
    public event Action? ExitRequested;
    public event Action? MenuOpening;

    public void RaiseSetup() => SetupRequested?.Invoke();

    public void RaiseSettings() => SettingsRequested?.Invoke();

    public void RaiseCopyLastTranscript() => CopyLastTranscriptRequested?.Invoke();

    public void RaiseRestartEngine() => RestartEngineRequested?.Invoke();

    public void RaiseExit() => ExitRequested?.Invoke();

    public void RaiseMenuOpening() => MenuOpening?.Invoke();

    public void SetIcon(TrayIconKind icon) => Icons.Add(icon);

    public void SetPhaseTooltip(TrayPhaseStatus phase, string? detail) => Tooltips.Add((phase, detail));

    public void SetStatus(TrayStatus status) => Statuses.Add(status);

    public void SetSetupHighlighted(bool highlighted) => SetupHighlights.Add(highlighted);

    public void SetCopyLastTranscriptEnabled(bool enabled) => CopyEnabled.Add(enabled);

    public void ShowErrorBalloon(string message) => Balloons.Add(message);

    public void Dispose() => Disposed = true;
}

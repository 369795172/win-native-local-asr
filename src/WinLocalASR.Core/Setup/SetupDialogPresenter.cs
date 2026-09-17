namespace WinLocalASR.Core.Setup;

/// <summary>
/// View surface implemented by the WinForms SetupDialog. All methods are invoked on
/// the UI thread (the presenter marshals through the injected marshal delegate).
/// </summary>
public interface ISetupDialogView
{
    void SetStep(int step, int totalSteps, string stepName);

    void SetFileProgress(string fileName, int percent);

    void AppendLog(string line);

    void ShowResult(bool success, string? errorMessage);

    void CloseView();
}

/// <summary>
/// Presenter between the six-step <see cref="SetupRunner"/> and a dialog view: starts
/// the run on the thread pool, marshals progress ticks (file bar only) and step-log
/// lines to the view, surfaces success/error/cancel, and owns the cancellation token
/// (cancel cleans up .part files inside the runner). The step-6 completion delegate is
/// wired via <see cref="SetupRunnerOptions.Completion"/> (the shell points it at
/// AppController.InitializeAsync).
/// </summary>
public sealed class SetupDialogPresenter : IDisposable
{
    private readonly SetupRunner _runner;
    private readonly ISetupDialogView _view;
    private readonly Action<Action> _marshal;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _run;

    public SetupDialogPresenter(SetupRunner runner, ISetupDialogView view, Action<Action> marshal)
    {
        _runner = runner;
        _view = view;
        _marshal = marshal;
    }

    /// <summary>The run task; null until <see cref="Start"/> is called. Tests await this.</summary>
    public Task? RunTask => _run;

    public void Start()
    {
        if (_run is not null)
        {
            throw new InvalidOperationException("Setup presenter already started.");
        }

        _run = Task.Run(() => _runner.RunAsync(OnProgress, _cancellation.Token));
        _ = _run.ContinueWith(OnFinished, TaskScheduler.Default);
    }

    /// <summary>Cancel button: cooperative cancel; the runner deletes .part files
    /// (finished artifacts stay) and the view is closed.</summary>
    public void Cancel() => _cancellation.Cancel();

    private void OnProgress(SetupProgress p)
    {
        _marshal(() =>
        {
            _view.SetStep(p.Step, SetupRunner.TotalSteps, p.StepName);
            if (p.FilePercent is int percent)
            {
                if (p.FileName is not null)
                {
                    _view.SetFileProgress(p.FileName, percent);
                }
            }
            else
            {
                _view.AppendLog(p.Message);
            }
        });
    }

    private void OnFinished(Task task)
    {
        _marshal(() =>
        {
            if (task.IsCanceled)
            {
                _view.CloseView();
            }
            else if (task.IsFaulted)
            {
                Exception root = task.Exception!.GetBaseException();
                _view.ShowResult(false, root is SetupException ? root.Message : $"Setup failed: {root.Message}");
            }
            else
            {
                _view.ShowResult(true, null);
                _view.CloseView();
            }
        });
    }

    public void Dispose() => _cancellation.Dispose();
}

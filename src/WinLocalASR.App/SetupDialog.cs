using WinLocalASR.Core.Setup;

namespace WinLocalASR.App;

/// <summary>
/// Thin WinForms shell for the six-step first-run installer: coarse step progress bar,
/// per-file progress bar, step log, and a Cancel button (wires the token; the runner
/// cleans .part files). All logic lives in Core (SetupRunner + SetupDialogPresenter);
/// this class only renders. Task 7's shell constructs it when Configured=false and
/// wires the completion delegate to AppController.InitializeAsync.
/// </summary>
internal sealed class SetupDialog : Form, ISetupDialogView
{
    private readonly SetupDialogPresenter _presenter;
    private readonly ProgressBar _stepProgress;
    private readonly ProgressBar _fileProgress;
    private readonly Label _statusLabel;
    private readonly Label _fileLabel;
    private readonly ListBox _log;
    private readonly Button _cancelButton;

    public SetupDialog(SetupRunner runner)
    {
        Text = "WinLocalASR Setup (首次配置 / First-run setup)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(560, 360);

        _stepProgress = new ProgressBar { Left = 12, Top = 12, Width = 536, Height = 18, Minimum = 0 };
        _statusLabel = new Label { Left = 12, Top = 34, Width = 536, Height = 18, Text = "Preparing…" };
        _fileProgress = new ProgressBar { Left = 12, Top = 58, Width = 536, Height = 14, Minimum = 0, Maximum = 100 };
        _fileLabel = new Label { Left = 12, Top = 76, Width = 536, Height = 16, Text = "" };
        _log = new ListBox { Left = 12, Top = 98, Width = 536, Height = 208, HorizontalScrollbar = true };
        _cancelButton = new Button { Left = 472, Top = 316, Width = 76, Height = 28, Text = "Cancel / 取消" };
        _cancelButton.Click += (_, _) => CancelSetup();

        Controls.Add(_stepProgress);
        Controls.Add(_statusLabel);
        Controls.Add(_fileProgress);
        Controls.Add(_fileLabel);
        Controls.Add(_log);
        Controls.Add(_cancelButton);

        _presenter = new SetupDialogPresenter(runner, this, MarshalToUi);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _presenter.Start();
    }

    private void CancelSetup()
    {
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Cancelling…";
        _presenter.Cancel();
    }

    private void MarshalToUi(Action action)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    void ISetupDialogView.SetStep(int step, int totalSteps, string stepName)
    {
        _stepProgress.Maximum = totalSteps;
        _stepProgress.Value = Math.Min(step, totalSteps);
        _statusLabel.Text = $"Step {step}/{totalSteps}: {stepName}";
    }

    void ISetupDialogView.SetFileProgress(string fileName, int percent)
    {
        _fileLabel.Text = fileName;
        _fileProgress.Value = Math.Clamp(percent, 0, 100);
    }

    void ISetupDialogView.AppendLog(string line)
    {
        _log.Items.Add(line);
        _log.TopIndex = _log.Items.Count - 1;
    }

    void ISetupDialogView.ShowResult(bool success, string? errorMessage)
    {
        _cancelButton.Enabled = false;
        if (success)
        {
            _statusLabel.Text = "Setup complete (配置完成).";
        }
        else
        {
            _statusLabel.Text = "Setup failed (配置失败).";
            _log.Items.Add(errorMessage ?? "Unknown error");
            _log.TopIndex = _log.Items.Count - 1;
        }
    }

    void ISetupDialogView.CloseView()
    {
        if (IsHandleCreated)
        {
            BeginInvoke(Close);
        }
        else
        {
            Close();
        }
    }
}

using System.Threading;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Setup;
using Xunit;

namespace WinLocalASR.Tests.Setup;

/// <summary>Presenter wiring tests (view-side logic, no WinForms). The dialog itself is
/// a rendering shell; CI green on windows-latest via EnableWindowsTargeting is its bar.</summary>
public class SetupDialogPresenterTests : IDisposable
{
    private readonly string _installBase = SetupTestAssets.NewTempDir();
    private readonly string _appDataBase = SetupTestAssets.NewTempDir();
    private readonly BlockingStream _blocking = new();
    private readonly string _manifestPath;

    private const string LlamaPrimaryUrl =
        "https://github.com/ggml-org/llama.cpp/releases/download/b10964/llama-b10964-bin-win-cpu-x64.zip";

    private const string MainModelUrl =
        "https://huggingface.co/" + SetupTestAssets.Repo + "/resolve/main/" + SetupTestAssets.MainModelFile;

    private const string MmprojModelUrl =
        "https://huggingface.co/" + SetupTestAssets.Repo + "/resolve/main/" + SetupTestAssets.MmprojModelFile;

    public SetupDialogPresenterTests()
    {
        byte[] zip = SetupTestAssets.MakeFakeLlamaZip();
        byte[] main = SetupTestAssets.FakeBytes(2048, 3);
        byte[] mmproj = SetupTestAssets.FakeBytes(1024, 4);
        _manifestPath = SetupTestAssets.WriteManifest(
            github: "https://github.com",
            ghProxyPrefix: "https://ghproxy.cn/",
            huggingFace: "https://huggingface.co",
            hfMirror: "https://hf-mirror.com",
            llamaSha256: SetupTestAssets.Sha256(zip),
            mainSha256: SetupTestAssets.Sha256(main),
            mmprojSha256: SetupTestAssets.Sha256(mmproj),
            mainSizeBytes: main.Length,
            mmprojSizeBytes: mmproj.Length,
            directory: _installBase);
        _files = (zip, main, mmproj);
    }

    private readonly (byte[] Zip, byte[] Main, byte[] Mmproj) _files;

    public void Dispose()
    {
        _blocking.Release();
        SetupTestAssets.DeleteDir(_installBase);
        SetupTestAssets.DeleteDir(_appDataBase);
    }

    private SetupRunner NewRunner(StubHttpFileHandler handler) => new(_manifestPath, new SetupRunnerOptions
    {
        InstallBaseDirectory = _installBase,
        Settings = new SettingsStore(_appDataBase),
        HttpMessageHandler = handler,
        MirrorRetryDelay = TimeSpan.Zero,
    });

    [Fact]
    public async Task Forwards_steps_logs_and_success_to_the_marshaled_view()
    {
        var handler = new StubHttpFileHandler(uri => uri switch
        {
            LlamaPrimaryUrl => StubHttpFileHandler.Bytes(_files.Zip),
            MainModelUrl => StubHttpFileHandler.Bytes(_files.Main),
            MmprojModelUrl => StubHttpFileHandler.Bytes(_files.Mmproj),
            _ => StubHttpFileHandler.NotFound(),
        });
        var view = new FakeSetupView();
        using var presenter = new SetupDialogPresenter(NewRunner(handler), view, a => a());

        presenter.Start();
        Assert.NotNull(presenter.RunTask);
        await presenter.RunTask;

        Assert.True(SpinWait.SpinUntil(() => view.ResultCount > 0, 10_000), "view never got a result");
        Assert.True(SpinWait.SpinUntil(() => view.Closed, 10_000), "view never closed");

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, view.Steps.Select(s => s.Step).Distinct());
        Assert.Equal(SetupRunner.TotalSteps, view.Steps.First().Total);
        Assert.NotEmpty(view.Logs);
        Assert.Contains(view.Logs, line => line.Contains("skipping download") is false && line.Contains("sha256 verified"));
        Assert.Contains(view.FileTicks, t => t.Percent > 0);
        (bool ok, string? error) result = view.Results.Single();
        Assert.True(result.ok);
        Assert.Null(result.error);
        Assert.True(new SettingsStore(_appDataBase).Load().Configured);
    }

    [Fact]
    public async Task Cancel_mid_download_closes_the_view_without_success()
    {
        var handler = new StubHttpFileHandler(uri => uri switch
        {
            LlamaPrimaryUrl => StubHttpFileHandler.Bytes(_files.Zip),
            MainModelUrl => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StreamContent(_blocking),
            },
            _ => StubHttpFileHandler.NotFound(),
        });
        var view = new FakeSetupView();
        using var presenter = new SetupDialogPresenter(NewRunner(handler), view, a => a());

        presenter.Start();
        string mainPart = Path.Combine(
            _installBase, SetupRunner.InstallDirName, SetupRunner.ModelsDirName,
            SetupTestAssets.MainModelFile + SetupRunner.PartSuffix);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(mainPart), 10_000), "model download never started");

        presenter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => presenter.RunTask!);

        Assert.True(SpinWait.SpinUntil(() => view.Closed, 10_000), "view never closed after cancel");
        Assert.Equal(0, view.ResultCount); // cancelled ≠ failed result
        Assert.False(new SettingsStore(_appDataBase).Load().Configured);
        _blocking.Release();
    }

    [Fact]
    public async Task Setup_failure_surfaces_the_error_message_to_the_view()
    {
        var handler = new StubHttpFileHandler(_ => StubHttpFileHandler.NotFound());
        var view = new FakeSetupView();
        using var presenter = new SetupDialogPresenter(NewRunner(handler), view, a => a());

        presenter.Start();
        await Assert.ThrowsAsync<SetupException>(() => presenter.RunTask!);

        Assert.True(SpinWait.SpinUntil(() => view.ResultCount > 0, 10_000), "view never got a result");
        (bool ok, string? error) result = view.Results.Single();
        Assert.False(result.ok);
        Assert.Contains("download failed", result.error, StringComparison.OrdinalIgnoreCase);
        Assert.False(view.Closed, "failed setup must stay open for the user to read/retry");
    }

    private sealed class FakeSetupView : ISetupDialogView
    {
        private readonly object _gate = new();
        private List<(int Step, int Total, string Name)> _steps = new();
        private List<(string File, int Percent)> _fileTicks = new();
        private List<string> _logs = new();
        private List<(bool Ok, string? Error)> _results = new();
        private bool _closed;

        public IReadOnlyList<(int Step, int Total, string Name)> Steps
        {
            get { lock (_gate) return _steps.ToList(); }
        }

        public IReadOnlyList<(string File, int Percent)> FileTicks
        {
            get { lock (_gate) return _fileTicks.ToList(); }
        }

        public IReadOnlyList<string> Logs
        {
            get { lock (_gate) return _logs.ToList(); }
        }

        public IReadOnlyList<(bool Ok, string? Error)> Results
        {
            get { lock (_gate) return _results.ToList(); }
        }

        public int ResultCount
        {
            get { lock (_gate) return _results.Count; }
        }

        public bool Closed
        {
            get { lock (_gate) return _closed; }
        }

        public void SetStep(int step, int totalSteps, string stepName)
        {
            lock (_gate) _steps.Add((step, totalSteps, stepName));
        }

        public void SetFileProgress(string fileName, int percent)
        {
            lock (_gate) _fileTicks.Add((fileName, percent));
        }

        public void AppendLog(string line)
        {
            lock (_gate) _logs.Add(line);
        }

        public void ShowResult(bool success, string? errorMessage)
        {
            lock (_gate) _results.Add((success, errorMessage));
        }

        public void CloseView()
        {
            lock (_gate) _closed = true;
        }
    }
}

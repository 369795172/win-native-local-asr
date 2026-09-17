using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Output;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Core.Setup;

namespace WinLocalASR.App;

/// <summary>
/// The real object graph (Task 7 composition): SettingsStore → configured check →
/// AudioCaptureManager + ClipboardWriter + LlamaServerClient adapters → AppController with
/// Task 6's BCL timing defaults (SystemClock / SystemSchedulingTimer /
/// SynchronizationContextDispatcher, constructed on the UI thread by Program).
/// The engine manifest is read lazily-guarded: before first setup the installed
/// versions.json does not exist yet, which is the NORMAL unconfigured path — the graph
/// still builds and the controller surfaces the failure as an Error phase via
/// <see cref="UnavailableTranscriptionService"/>.
/// </summary>
internal sealed class DefaultAppServiceGraphFactory : IAppServiceGraphFactory
{
    private readonly IShellLog _log;

    public DefaultAppServiceGraphFactory(IShellLog log) => _log = log;

    public AppServiceGraph Create()
    {
        SettingsStore settings = new();
        SynchronizationContextDispatcher dispatcher = new();
        AppController controller = new(
            new AudioCaptureServiceAdapter(new AudioCaptureManager()),
            BuildTranscriptionService(),
            new ClipboardServiceAdapter(new ClipboardWriter()),
            new SettingsProviderAdapter(settings),
            new SystemSchedulingTimer(),
            dispatcher,
            new SystemClock());

        return new AppServiceGraph(
            controller,
            settings.Load().Configured,
            Dispatcher: dispatcher);
    }

    private ITranscriptionService BuildTranscriptionService()
    {
        string installedManifest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SetupRunner.InstallDirName,
            SetupRunner.InstalledVersionsFileName);
        try
        {
            LlamaServerPaths paths = LlamaServerPaths.Load(installedManifest);
            return new TranscriptionServiceAdapter(
                new LlamaServerClient(new LlamaServerClientOptions { Paths = paths }));
        }
        catch (Exception ex)
        {
            _log.Warn(
                $"engine manifest unavailable ('{installedManifest}'): {ex.Message} — " +
                "the engine will report not-ready until setup completes");
            return new UnavailableTranscriptionService(ex);
        }
    }

    /// <summary>
    /// Stands in for the llama-server client when the installed versions.json is missing
    /// (first run) or unreadable: StartAsync rethrows the reason so
    /// AppController.StartBridgeAsync funnels it into the Error phase exactly like an
    /// engine start failure.
    /// </summary>
    private sealed class UnavailableTranscriptionService : ITranscriptionService
    {
        private readonly Exception _reason;

        public UnavailableTranscriptionService(Exception reason) => _reason = reason;

        public bool IsReady => false;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(_reason);

        public Task StopAsync() => Task.CompletedTask;

        public Task<string> TranscribeAsync(string wavPath, string? context, CancellationToken cancellationToken, TimeSpan timeout) =>
            Task.FromException<string>(new InvalidOperationException("engine is not installed"));
    }
}

using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Output;
using WinLocalASR.Core.Settings;

namespace WinLocalASR.Core.State;

/// <summary>
/// Thin adapters from the concrete Task 3-5 modules to the narrow Core/State seams. They
/// live in Core/State (not next to the modules) so the consumed modules stay untouched.
/// </summary>
public sealed class AudioCaptureServiceAdapter : IAudioCaptureService
{
    private readonly AudioCaptureManager _manager;

    public AudioCaptureServiceAdapter(AudioCaptureManager manager) => _manager = manager;

    public string? SelectedDeviceId
    {
        get => _manager.SelectedDeviceId;
        set => _manager.SelectedDeviceId = value;
    }

    public double MaximumDuration
    {
        get => _manager.MaximumDuration;
        set => _manager.MaximumDuration = value;
    }

    public Action? OnMaximumDuration
    {
        get => _manager.OnMaximumDuration;
        set => _manager.OnMaximumDuration = value;
    }

    public Action<float>? OnAudioLevel
    {
        get => _manager.OnAudioLevel;
        set => _manager.OnAudioLevel = value;
    }

    public void StartRecording(string? deviceId = null) => _manager.StartRecording(deviceId);

    public string StopRecording() => _manager.StopRecording();

    public void CancelRecording() => _manager.CancelRecording();
}

public sealed class TranscriptionServiceAdapter : ITranscriptionService
{
    private readonly LlamaServerClient _client;

    public TranscriptionServiceAdapter(LlamaServerClient client) => _client = client;

    public bool IsReady => _client.IsReady;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _client.StartAsync(cancellationToken);

    public Task StopAsync() => _client.StopAsync();

    public Task<string> TranscribeAsync(string wavPath, string? context, CancellationToken cancellationToken, TimeSpan timeout) =>
        _client.TranscribeAsync(wavPath, context, cancellationToken, timeout);
}

public sealed class ClipboardServiceAdapter : IClipboardService
{
    private readonly ClipboardWriter _writer;

    public ClipboardServiceAdapter(ClipboardWriter writer) => _writer = writer;

    public bool SetText(string text) => _writer.SetText(text);
}

public sealed class SettingsProviderAdapter : ISettingsProvider
{
    private readonly SettingsStore _store;

    public SettingsProviderAdapter(SettingsStore store) => _store = store;

    public AppSettings Load() => _store.Load();
}

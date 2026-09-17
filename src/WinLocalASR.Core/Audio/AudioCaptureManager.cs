namespace WinLocalASR.Core.Audio;

/// <summary>
/// Port of MacLocalASR AudioCaptureManager.swift: captures the selected input device,
/// converts every chunk to 24 kHz / mono / Int16 PCM, meters RMS level, accumulates
/// the samples, and on stop writes one WAV file per utterance to %TEMP%.
///
/// Public surface maps 1:1 to the Swift reference:
///   sampleRate                    -> SampleRate
///   onMaximumDuration             -> OnMaximumDuration
///   onAudioLevel                  -> OnAudioLevel
///   selectedDeviceID              -> SelectedDeviceId
///   maximumDuration               -> MaximumDuration
///   listInputDevices()            -> ListInputDevices() (instance; via IAudioDeviceFactory)
///   startRecording()              -> StartRecording(deviceId) (deviceId optional, falls back
///                                    to SelectedDeviceId, then system default — Swift order)
///   stopRecording() -> URL        -> StopRecording() -> file path
///   cancelRecording()             -> CancelRecording()
///   AudioCaptureError cases       -> AudioCaptureException subclasses
/// </summary>
public sealed class AudioCaptureManager : IDisposable
{
    public const int SampleRate = 24000;

    public static readonly AudioFormat TargetFormat = new(SampleRate, 1, SampleFormat.Pcm16);

    public Action? OnMaximumDuration { get; set; }

    public Action<float>? OnAudioLevel { get; set; }

    public string? SelectedDeviceId { get; set; }

    /// <summary>Auto-stop callback delay in seconds (Swift default: 60).</summary>
    public double MaximumDuration { get; set; } = 60;

    private readonly IAudioDeviceFactory _deviceFactory;
    private readonly IPcmResamplerFactory _resamplerFactory;
    private readonly object _sync = new();
    private MemoryStream _pcm = new();
    private bool _isRecording;
    private IAudioCaptureSource? _source;
    private IPcmResampler? _resampler;
    private CancellationTokenSource? _maxDurationCts;

    public AudioCaptureManager(IAudioDeviceFactory? deviceFactory = null, IPcmResamplerFactory? resamplerFactory = null)
    {
        _deviceFactory = deviceFactory ?? new WindowsAudioDeviceFactory();
        _resamplerFactory = resamplerFactory ?? new MediaFoundationResamplerFactory();
    }

    public IReadOnlyList<AudioDeviceInfo> ListInputDevices() => _deviceFactory.ListInputDevices();

    public void StartRecording(string? deviceId = null)
    {
        lock (_sync)
        {
            if (_isRecording)
            {
                return; // Swift: guard shouldStart else { return }
            }
        }

        string? effectiveDeviceId = string.IsNullOrEmpty(deviceId) ? SelectedDeviceId : deviceId;
        IAudioCaptureSource source = _deviceFactory.OpenCapture(effectiveDeviceId)
            ?? throw new NoInputDeviceException();

        IPcmResampler? resampler = null;
        try
        {
            if (source.Format != TargetFormat)
            {
                resampler = _resamplerFactory.Create(source.Format, TargetFormat);
            }
        }
        catch (Exception ex)
        {
            source.Dispose();
            throw new ConverterUnavailableException($"Failed to create converter for {source.Format}: {ex.Message}");
        }

        lock (_sync)
        {
            _pcm = new MemoryStream();
            _source = source;
            _resampler = resampler;
            _isRecording = true; // set before Start so first synchronous chunks are kept
        }

        source.PcmChunkReceived += OnPcmChunkReceived;
        try
        {
            source.Start();
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _isRecording = false;
                _source = null;
                _resampler = null;
            }
            source.PcmChunkReceived -= OnPcmChunkReceived;
            source.Dispose();
            resampler?.Dispose();
            throw new EngineStartFailedException($"Audio capture did not start: {ex.Message}");
        }

        double durationLimit = MaximumDuration;
        var cts = new CancellationTokenSource();
        _maxDurationCts = cts;
        _ = RunMaxDurationTimerAsync(durationLimit, cts.Token);
    }

    public string StopRecording()
    {
        IAudioCaptureSource? source;
        IPcmResampler? resampler;
        lock (_sync)
        {
            if (!_isRecording)
            {
                throw new NotRecordingException();
            }
            source = _source;
            resampler = _resampler;
        }

        _maxDurationCts?.Cancel();
        source?.PcmChunkReceived -= OnPcmChunkReceived;
        try
        {
            source?.Stop(); // synchronous: joins the capture thread before Flush
        }
        catch
        {
            // stopping a failed source must not mask the real outcome
        }

        byte[] tail = Array.Empty<byte>();
        try
        {
            tail = resampler?.Flush() ?? Array.Empty<byte>();
        }
        catch
        {
        }

        byte[] data;
        lock (_sync)
        {
            _isRecording = false;
            _source = null;
            _resampler = null;
            if (tail.Length > 0)
            {
                _pcm.Write(tail);
            }
            data = _pcm.ToArray();
        }
        source?.Dispose();
        resampler?.Dispose();

        if (data.Length == 0)
        {
            throw new NoAudioCapturedException();
        }

        string path = Path.Combine(Path.GetTempPath(), $"WinLocalASR-{Guid.NewGuid()}.wav");
        return Pcm16WavWriter.WriteWavFile(path, data, SampleRate, channels: 1);
    }

    public void CancelRecording()
    {
        IAudioCaptureSource? source;
        IPcmResampler? resampler;
        lock (_sync)
        {
            if (!_isRecording)
            {
                return; // Swift: guard recording else { return }
            }
            source = _source;
            resampler = _resampler;
            _isRecording = false;
            _source = null;
            _resampler = null;
            _pcm = new MemoryStream(); // discard captured data
        }

        _maxDurationCts?.Cancel();
        source!.PcmChunkReceived -= OnPcmChunkReceived;
        try
        {
            source.Stop();
        }
        catch
        {
        }
        source.Dispose();
        resampler?.Dispose();
    }

    private void OnPcmChunkReceived(object? sender, ReadOnlyMemory<byte> chunk)
    {
        IPcmResampler? resampler;
        lock (_sync)
        {
            if (!_isRecording || chunk.Length == 0)
            {
                return;
            }
            resampler = _resampler;
        }

        byte[] converted;
        try
        {
            converted = resampler is null ? chunk.ToArray() : resampler.Process(chunk.Span);
        }
        catch
        {
            return; // Swift silently drops buffers whose conversion fails
        }

        if (converted.Length == 0)
        {
            return;
        }

        float level = AudioLevelMeter.ComputeLevel(converted);
        OnAudioLevel?.Invoke(level); // raised outside the lock; handlers may call back in

        lock (_sync)
        {
            if (_isRecording)
            {
                _pcm.Write(converted);
            }
        }
    }

    private async Task RunMaxDurationTimerAsync(double seconds, CancellationToken token)
    {
        try
        {
            if (seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }

            if (!token.IsCancellationRequested)
            {
                // Like Swift, this only reports; stopping is the state machine's job.
                OnMaximumDuration?.Invoke();
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _maxDurationCts?.Cancel();
        IAudioCaptureSource? source;
        IPcmResampler? resampler;
        lock (_sync)
        {
            source = _source;
            resampler = _resampler;
            _isRecording = false;
            _source = null;
            _resampler = null;
        }
        if (source is not null)
        {
            source.PcmChunkReceived -= OnPcmChunkReceived;
            try { source.Stop(); } catch { }
            source.Dispose();
        }
        resampler?.Dispose();
        _pcm.Dispose();
    }
}

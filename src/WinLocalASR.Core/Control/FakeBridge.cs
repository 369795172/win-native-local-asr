using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Inference;
using WinLocalASR.Core.Output;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;

namespace WinLocalASR.Core.Control;

/// <summary>
/// The <c>--fake-configured</c> transcription stand-in (the "FakeClient"): returns the
/// preset text after a fixed delay. It deliberately READS the WAV file the real capture
/// pipeline produced (RIFF header + duration) so the CI e2e proves the whole audio chain,
/// not just the state machine. Zero network: no engine is ever spawned.
/// </summary>
public sealed class FakeTranscriptionService : ITranscriptionService
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _delay;

    public FakeTranscriptionService(TimeSpan? delay = null) => _delay = delay ?? DefaultDelay;

    public string PresetText { get; set; } = "hello world";

    public int TranscribeCalls { get; private set; }

    public string? LastWavPath { get; private set; }

    public List<TimeSpan> ObservedDurations { get; } = new();

    public bool IsReady => true;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public async Task<string> TranscribeAsync(string wavPath, string? context, CancellationToken cancellationToken, TimeSpan timeout)
    {
        TranscribeCalls++;
        LastWavPath = wavPath;
        ObservedDurations.Add(await Task.Run(() => WavDurationParser.ParseDuration(wavPath), cancellationToken).ConfigureAwait(false));
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }

        return PresetText;
    }
}

/// <summary>
/// Synthetic capture device: a real-time 440 Hz sine at 48 kHz mono PCM16 (a plausible
/// device format, so the REAL resample → accumulate → WAV-write pipeline runs — a CI
/// runner has no WASAPI devices). Chunks are raised on a timer thread; after Stop
/// returns no further chunks are raised.
/// </summary>
public sealed class SineCaptureSource : IAudioCaptureSource
{
    public const double DefaultFrequencyHz = 440;
    public static readonly TimeSpan DefaultChunkInterval = TimeSpan.FromMilliseconds(100);

    private readonly double _frequencyHz;
    private readonly double _amplitude;
    private readonly TimeSpan _chunkInterval;
    private readonly int _samplesPerChunk;
    private readonly object _stopLock = new();
    private System.Threading.Timer? _timer;
    private bool _stopped;
    private int _nextSample;

    public SineCaptureSource(
        AudioFormat? format = null,
        double frequencyHz = DefaultFrequencyHz,
        double amplitude = 0.25,
        TimeSpan? chunkInterval = null)
    {
        Format = format ?? new AudioFormat(48000, 1, SampleFormat.Pcm16);
        _frequencyHz = frequencyHz;
        _amplitude = amplitude;
        _chunkInterval = chunkInterval ?? DefaultChunkInterval;
        _samplesPerChunk = (int)(Format.SampleRate * _chunkInterval.TotalSeconds);
    }

    public AudioFormat Format { get; }

    public event EventHandler<ReadOnlyMemory<byte>>? PcmChunkReceived;

    public void Start()
    {
        lock (_stopLock)
        {
            if (_timer is not null)
            {
                return;
            }

            _timer = new System.Threading.Timer(
                _ => EmitChunk(),
                null,
                _chunkInterval,
                _chunkInterval);
        }
    }

    private void EmitChunk()
    {
        lock (_stopLock)
        {
            if (_stopped)
            {
                return;
            }

            // Event raised INSIDE the stop lock: once Stop() returns, no further chunks
            // are raised (the manager's chunk handler never calls back into the source).
            PcmChunkReceived?.Invoke(this, BuildChunk());
        }
    }

    private byte[] BuildChunk()
    {
        byte[] chunk = new byte[_samplesPerChunk * 2];
        for (int i = 0; i < _samplesPerChunk; i++)
        {
            short sample = (short)Math.Clamp(
                Math.Round(_amplitude * short.MaxValue * Math.Sin(2 * Math.PI * _frequencyHz * _nextSample++ / Format.SampleRate)),
                short.MinValue,
                short.MaxValue);
            BitConverter.GetBytes(sample).CopyTo(chunk, i * 2);
        }

        return chunk;
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            _stopped = true;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>IAudioDeviceFactory stand-in whose every device is a <see cref="SineCaptureSource"/>.</summary>
public sealed class FakeAudioDeviceFactory : IAudioDeviceFactory
{
    public const string DeviceId = "fake-sine";

    private readonly AudioFormat? _format;

    public FakeAudioDeviceFactory(AudioFormat? format = null) => _format = format;

    public int OpenCalls { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> ListInputDevices() =>
        new[] { new AudioDeviceInfo(DeviceId, "Fake Sine Capture") };

    public IAudioCaptureSource? OpenCapture(string? deviceId)
    {
        OpenCalls++;
        return new SineCaptureSource(_format);
    }
}

/// <summary>
/// Service graph for <c>--fake-configured</c>: FakeClient + sine capture injected through
/// the REAL capture pipeline and REAL clipboard writer, configured=true settings (Setup is
/// skipped entirely; InitializeAsync reaches Idle with zero network). Everything else is
/// the production wiring — SystemSchedulingTimer / SynchronizationContextDispatcher /
/// SystemClock, the same defaults <c>DefaultAppServiceGraphFactory</c> uses.
/// </summary>
public sealed class FakeServiceGraphFactory : IAppServiceGraphFactory
{
    private readonly IPcmResamplerFactory? _resamplerFactory;
    private readonly IDispatcher? _dispatcher;
    private readonly TimeSpan? _fakeDelay;

    public FakeServiceGraphFactory(
        IPcmResamplerFactory? resamplerFactory = null,
        IDispatcher? dispatcher = null,
        TimeSpan? fakeDelay = null)
    {
        _resamplerFactory = resamplerFactory;
        _dispatcher = dispatcher;
        _fakeDelay = fakeDelay;
    }

    public FakeTranscriptionService FakeTranscription { get; private set; } = null!;

    public AppServiceGraph Create()
    {
        FakeTranscriptionService transcription = new(_fakeDelay);
        FakeTranscription = transcription;
        IDispatcher dispatcher = _dispatcher ?? new SynchronizationContextDispatcher();
        AppController controller = new(
            new AudioCaptureServiceAdapter(new AudioCaptureManager(
                new FakeAudioDeviceFactory(),
                _resamplerFactory ?? new MediaFoundationResamplerFactory())),
            transcription,
            new ClipboardServiceAdapter(new ClipboardWriter()),
            new ConfiguredSettingsProvider(),
            new SystemSchedulingTimer(),
            dispatcher,
            new SystemClock());

        return new AppServiceGraph(
            controller,
            InitiallyConfigured: true,
            Dispatcher: dispatcher,
            FakeTranscription: transcription);
    }

    /// <summary>Configured=true, otherwise default settings — the "skip Setup" stand-in.</summary>
    private sealed class ConfiguredSettingsProvider : ISettingsProvider
    {
        public AppSettings Load() => new() { Configured = true };
    }
}

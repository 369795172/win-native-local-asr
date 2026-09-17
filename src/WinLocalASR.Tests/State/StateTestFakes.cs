using WinLocalASR.Core.Settings;
using WinLocalASR.Core.State;
using ITimer = WinLocalASR.Core.State.ITimer;
using Xunit;

namespace WinLocalASR.Tests.State;

/// <summary>Shared virtual time source: FakeTimer moves it, FakeClock reads it.</summary>
public sealed class FakeTime
{
    public static readonly DateTime BaseUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public long NowMs;

    public DateTime UtcNow => BaseUtc.AddMilliseconds(NowMs);
}

/// <summary>
/// Fast-forwardable <see cref="ITimer"/>: <see cref="Advance"/> fires due one-shots in
/// (dueAt, registration) order. Callbacks may schedule new entries (the controller's
/// recording tick re-arms itself) — they are picked up by the same Advance call.
/// </summary>
public sealed class FakeTimer : ITimer
{
    private sealed class Entry : IDisposable
    {
        public long DueAtMs;
        public Action? Callback;
        public bool Cancelled;

        public void Dispose() => Cancelled = true;
    }

    private readonly FakeTime _time;
    private readonly List<Entry> _entries = new();

    public FakeTimer(FakeTime time) => _time = time;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        Entry entry = new()
        {
            DueAtMs = _time.NowMs + (long)delay.TotalMilliseconds,
            Callback = callback,
        };
        _entries.Add(entry);
        return entry;
    }

    public void Advance(TimeSpan by)
    {
        long target = _time.NowMs + (long)by.TotalMilliseconds;
        while (true)
        {
            Entry? next = null;
            int nextIndex = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry candidate = _entries[i];
                if (candidate.Cancelled || candidate.DueAtMs > target)
                {
                    continue;
                }

                if (next is null || candidate.DueAtMs < next.DueAtMs)
                {
                    next = candidate;
                    nextIndex = i;
                }
            }

            if (next is null)
            {
                break;
            }

            _entries.RemoveAt(nextIndex);
            _time.NowMs = Math.Max(_time.NowMs, next.DueAtMs);
            next.Callback!.Invoke();
        }

        _time.NowMs = target;
    }
}

public sealed class FakeClock : IClock
{
    private readonly FakeTime _time;

    public FakeClock(FakeTime time) => _time = time;

    public DateTime UtcNow => _time.UtcNow;
}

/// <summary>Synchronous dispatcher: everything runs inline, so tests are deterministic.</summary>
public sealed class SyncDispatcher : IDispatcher
{
    public int PostCount { get; private set; }

    public void Post(Action action)
    {
        PostCount++;
        action();
    }
}

public sealed class FakeAudioCapture : IAudioCaptureService
{
    public string? SelectedDeviceId { get; set; }

    public double MaximumDuration { get; set; } = 60;

    public Action? OnMaximumDuration { get; set; }

    public Action<float>? OnAudioLevel { get; set; }

    public Exception? StartException { get; set; }

    public string StopResult { get; set; } = "fake-recording.wav";

    public int StartCalls;
    public int StopCalls;
    public int CancelCalls;
    public string? LastStartedDeviceId;

    public void StartRecording(string? deviceId = null)
    {
        StartCalls++;
        if (StartException is not null)
        {
            throw StartException;
        }

        LastStartedDeviceId = deviceId;
    }

    public string StopRecording()
    {
        StopCalls++;
        return StopResult;
    }

    public void CancelRecording() => CancelCalls++;
}

public sealed class FakeTranscriptionService : ITranscriptionService
{
    public bool IsReady { get; set; } = true;

    public Exception? StartException { get; set; }

    /// <summary>When set, StartAsync returns this pending gate instead of completing.</summary>
    public TaskCompletionSource? StartGate { get; set; }

    public string NextTranscript { get; set; } = "hello world";

    /// <summary>When set, TranscribeAsync returns this pending task (race / clock tests).</summary>
    public TaskCompletionSource<string>? PendingResult { get; set; }

    public Exception? TranscribeException { get; set; }

    public int StartCalls;
    public int StopCalls;
    public int TranscribeCalls;
    public string? LastWavPath;
    public string? LastContext;
    public TimeSpan? LastTimeout;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        if (StartException is not null)
        {
            throw StartException;
        }

        return StartGate?.Task ?? Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCalls++;
        return Task.CompletedTask;
    }

    public Task<string> TranscribeAsync(string wavPath, string? context, CancellationToken cancellationToken, TimeSpan timeout)
    {
        TranscribeCalls++;
        LastWavPath = wavPath;
        LastContext = context;
        LastTimeout = timeout;
        if (TranscribeException is not null)
        {
            throw TranscribeException;
        }

        return PendingResult?.Task ?? Task.FromResult(NextTranscript);
    }
}

public sealed class FakeClipboardService : IClipboardService
{
    public bool Succeed { get; set; } = true;

    public List<string> Texts { get; } = new();

    public string? LastText => Texts.Count > 0 ? Texts[^1] : null;

    public bool SetText(string text)
    {
        Texts.Add(text);
        return Succeed;
    }
}

public sealed class FakeSettingsProvider : ISettingsProvider
{
    public AppSettings Current { get; set; } = new();

    public AppSettings Load() => Current;
}

/// <summary>
/// Composes the controller with all fakes plus an ordered observable-event log
/// (phase / hud controlValue / esc availability) for sequence assertions.
/// </summary>
public sealed class ControllerHarness
{
    public readonly FakeTime Time = new();
    public readonly FakeTimer Timer;
    public readonly FakeClock Clock;
    public readonly SyncDispatcher Dispatcher = new();
    public readonly FakeAudioCapture Audio;
    public readonly FakeTranscriptionService Transcription;
    public readonly FakeClipboardService Clipboard;
    public readonly FakeSettingsProvider Settings;
    public readonly AppController Controller;
    public readonly List<string> Events = new();

    public ControllerHarness(bool configured = true)
    {
        Timer = new FakeTimer(Time);
        Clock = new FakeClock(Time);
        Audio = new FakeAudioCapture();
        Transcription = new FakeTranscriptionService();
        Clipboard = new FakeClipboardService();
        Settings = new FakeSettingsProvider
        {
            Current = new AppSettings { Configured = configured },
        };
        Controller = new AppController(Audio, Transcription, Clipboard, Settings, Timer, Dispatcher, Clock);
        Controller.PhaseChanged += phase => Events.Add("phase:" + Label(phase));
        Controller.HudPresentationChanged += hud => Events.Add("hud:" + hud.ControlValue);
        Controller.CancelHotkeyAvailabilityChanged += active => Events.Add("esc:" + (active ? "true" : "false"));
    }

    public static string Label(AppPhase phase) => phase switch
    {
        AppPhase.LoadingPhase => "loading",
        AppPhase.IdlePhase => "idle",
        AppPhase.RecordingPhase => "recording",
        AppPhase.ProcessingPhase => "processing",
        AppPhase.ErrorPhase error => "error:" + error.Message,
        _ => throw new InvalidOperationException("unreachable"),
    };

    public void AdvanceMs(long ms) => Timer.Advance(TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// Waits for a condition reached via a pending-task continuation. Under xunit's
    /// AsyncTestSyncContext, completing a TCS does NOT run ConfigureAwait(false)
    /// continuations inline (they go to the thread pool), so post-SetResult state is
    /// observed by spinning on the condition instead of assuming synchronous completion.
    /// </summary>
    public void Settle(Func<bool> condition, int timeoutMs = 2000) =>
        Assert.True(SpinWait.SpinUntil(condition, timeoutMs), "condition not reached in time");
}

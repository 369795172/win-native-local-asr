using WinLocalASR.Core.Settings;

namespace WinLocalASR.Core.Setup;

/// <summary>Failure of the first-run setup flow (download exhausted both mirrors,
/// checksum verification failed, extraction failed). Not retried automatically;
/// the dialog surfaces the message and the user can re-run setup (idempotent).</summary>
public sealed class SetupException : Exception
{
    public IReadOnlyList<string> AttemptedUrls { get; }

    public SetupException(string message, IReadOnlyList<string>? attemptedUrls = null, Exception? inner = null)
        : base(message, inner)
    {
        AttemptedUrls = attemptedUrls ?? Array.Empty<string>();
    }
}

/// <summary>One progress update from the setup flow. <see cref="FilePercent"/> is set only
/// on per-file byte-progress ticks (the dialog updates its file bar and skips the log);
/// otherwise <see cref="Message"/> is a step-log line.</summary>
public sealed record SetupProgress(
    int Step,
    string StepName,
    string Message,
    string? FileName = null,
    int? FilePercent = null);

/// <summary>Everything the six-step installer needs injected for testing: install base
/// directory (never the real %LOCALAPPDATA% in tests), HTTP handler (fake file servers),
/// settings store (temp %APPDATA%), failover delay, and the step-6 completion delegate
/// the shell wires to AppController.InitializeAsync.</summary>
public sealed class SetupRunnerOptions
{
    public string InstallBaseDirectory { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public HttpMessageHandler? HttpMessageHandler { get; init; }

    public SettingsStore? Settings { get; init; }

    /// <summary>Pauses between the primary attempt and the mirror retry.</summary>
    public TimeSpan MirrorRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Injectable delay so failover tests run in milliseconds.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>Step 6: invoked after the installed manifest is written and
    /// Configured=true (the shell wires AppController.InitializeAsync here).</summary>
    public Func<Task>? Completion { get; init; }

    public Func<TimeSpan, CancellationToken, Task> EffectiveDelay => Delay ?? Task.Delay;
}

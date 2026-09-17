namespace WinLocalASR.Core.Inference;

/// <summary>
/// Tunables for <see cref="LlamaServerClient"/>. Time knobs are injectable so tests run fast
/// (default backoff 1/2/4 s and the 2 s stop grace match Swift).
/// </summary>
public sealed class LlamaServerClientOptions
{
    /// <summary>Resolved exe/model paths (minimal versions.json reader output).</summary>
    public required LlamaServerPaths Paths { get; init; }

    /// <summary>Fixed port (tests); default: probe from <see cref="BasePort"/> upward.</summary>
    public int? Port { get; init; }

    public int BasePort { get; init; } = PortProber.DefaultBasePort;

    /// <summary>Port never chosen by the probe (ControlServer, Task 12).</summary>
    public int ReservedPort { get; init; } = PortProber.ReservedControlServerPort;

    /// <summary>Crash restart backoff sequence; length caps the attempt count (Swift: 1/2/4, max 3).</summary>
    public IReadOnlyList<TimeSpan> RestartBackoff { get; init; } = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    };

    /// <summary>Grace period between the /shutdown attempt and the process kill (Swift: 2 s).</summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long /health may take to turn healthy (Swift start timeout: 120 s).</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

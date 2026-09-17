using System.Diagnostics;

namespace WinLocalASR.Core.Inference;

/// <summary>
/// Spawn seam so restart/backoff tests can simulate process exits without a real llama-server
/// (unit tests must not require llama-server.exe presence).
/// </summary>
public interface IProcessSpawner
{
    IManagedProcess Spawn(ProcessStartInfo startInfo);
}

/// <summary>Lifecycle surface of the spawned llama-server process (subset of Process).</summary>
public interface IManagedProcess : IDisposable
{
    /// <summary>Raised when the process terminates, whether by natural exit or Kill.</summary>
    event EventHandler? Exited;

    /// <summary>Standard output lines (empty lines and terminal null markers are filtered).</summary>
    event EventHandler<string>? OutputLineReceived;

    /// <summary>Standard error lines.</summary>
    event EventHandler<string>? ErrorLineReceived;

    bool HasExited { get; }

    void Kill();
}

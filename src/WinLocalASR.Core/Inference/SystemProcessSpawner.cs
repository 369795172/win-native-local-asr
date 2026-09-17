using System.ComponentModel;
using System.Diagnostics;

namespace WinLocalASR.Core.Inference;

/// <summary>Production spawner: real OS process via System.Diagnostics.Process.</summary>
public sealed class SystemProcessSpawner : IProcessSpawner
{
    public IManagedProcess Spawn(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new ProcessExitedException($"Failed to start '{startInfo.FileName}'.");
            }
        }
        catch (Exception ex) when (ex is not LlamaServerException)
        {
            process.Dispose();
            throw new ProcessExitedException($"Failed to start '{startInfo.FileName}': {ex.Message}", ex);
        }

        return new SystemProcess(process);
    }
}

internal sealed class SystemProcess : IManagedProcess
{
    private readonly Process _process;

    public SystemProcess(Process process)
    {
        _process = process;
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                OutputLineReceived?.Invoke(this, e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                ErrorLineReceived?.Invoke(this, e.Data);
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public event EventHandler? Exited;

    public event EventHandler<string>? OutputLineReceived;

    public event EventHandler<string>? ErrorLineReceived;

    public bool HasExited => _process.HasExited;

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (Win32Exception)
        {
            // exiting concurrently; Exited still fires
        }
    }

    public void Dispose() => _process.Dispose();
}

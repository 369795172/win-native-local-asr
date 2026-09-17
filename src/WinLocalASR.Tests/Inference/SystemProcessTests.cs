using System.Diagnostics;
using WinLocalASR.Core.Inference;
using Xunit;

namespace WinLocalASR.Tests.Inference;

/// <summary>
/// Exercises the production spawner against a real OS process (no llama-server.exe needed):
/// output redirection, the Exited event, and HasExited. Runs on windows-latest CI and macOS
/// local builds; skips only when no known echo binary exists.
/// </summary>
public class SystemProcessTests
{
    [SkippableFact]
    public async Task Spawn_collects_output_and_exit_from_a_real_process()
    {
        string fileName;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            fileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            arguments = new[] { "/c", "echo winlocalasr-smoke" };
        }
        else if (File.Exists("/bin/echo"))
        {
            fileName = "/bin/echo";
            arguments = new[] { "winlocalasr-smoke" };
        }
        else
        {
            Skip.If(true, "no known echo binary on this platform");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var spawner = new SystemProcessSpawner();
        var outputReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using IManagedProcess process = spawner.Spawn(startInfo);
        process.OutputLineReceived += (_, line) => outputReceived.TrySetResult(line);
        process.ErrorLineReceived += (_, line) => outputReceived.TrySetResult(line);
        process.Exited += (_, _) => exited.TrySetResult();

        string line = await outputReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("winlocalasr-smoke", line);

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.HasExited);
    }
}

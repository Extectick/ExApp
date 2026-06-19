using System.Diagnostics;
using ExApp.Ipc;

namespace ExApp.Desktop.Services;

internal sealed class AgentProcessManager
{
    public const string PipeName = "ExApp.Agent.v1";
    private readonly NamedPipeIpcClient _client = new(PipeName);

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await PingAsync(cancellationToken))
        {
            return;
        }

        var executable = FindAgentExecutable()
            ?? throw new InvalidOperationException("ExApp Agent executable was not found. Build ExApp.Agent first.");
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(150, cancellationToken);
            if (await PingAsync(cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException("ExApp Agent did not become ready in time.");
    }

    private async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.SendAsync<object, object>(
                IpcCommands.AgentPing,
                new { },
                TimeSpan.FromMilliseconds(350),
                cancellationToken);
            return true;
        }
        catch (IpcException)
        {
            return false;
        }
    }

    private static string? FindAgentExecutable()
    {
        var environmentPath = Environment.GetEnvironmentVariable("EXAPP_AGENT_EXE");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        foreach (var bundled in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "agent", "ExApp.Agent.exe"),
                     Path.Combine(AppContext.BaseDirectory, "ExApp.Agent.exe")
                 })
        {
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ExApp.Agent", "bin", "Debug", "net8.0", "ExApp.Agent.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}

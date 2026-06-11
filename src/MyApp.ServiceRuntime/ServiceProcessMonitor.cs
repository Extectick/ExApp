using System.Diagnostics;
using MyApp.Ipc;

namespace MyApp.ServiceRuntime;

public sealed class ServiceProcessMonitor
{
    public AgentServiceStatus Normalize(ServiceDescriptor service, AgentServiceStatus status)
    {
        var pid = ReadPid(Path.Combine(service.RuntimeDirectory, "service.pid"));
        if (pid is not null && IsProcessRunning(pid.Value))
        {
            return status.State.Equals("starting", StringComparison.OrdinalIgnoreCase)
                ? status with { State = "starting", Health = "starting", Message = $"{service.Manifest.Name} is starting." }
                : status with { State = "running", Health = "ok" };
        }

        if (IsActive(status.State))
        {
            return status with
            {
                State = "crashed",
                Health = "error",
                Message = $"{service.Manifest.Name} process is not running.",
                LastError = "Process exited unexpectedly.",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        return status;
    }

    public AgentServiceStatus Missing(ServiceDescriptor service, string message) =>
        new(
            service.ServiceId,
            service.Manifest.Version,
            "missing",
            "error",
            message,
            "Service executable is missing.",
            DateTimeOffset.UtcNow);

    public async Task EnsureStoppedAsync(
        ServiceDescriptor service,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var pidPath = Path.Combine(service.RuntimeDirectory, "service.pid");
        var pid = ReadPid(pidPath);
        if (pid is null)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
        catch (TimeoutException)
        {
            throw new ServiceRuntimeException(
                "service.stopTimeout",
                $"Service process '{pid.Value}' did not stop before uninstall.");
        }
        finally
        {
            TryDeletePid(pidPath);
        }
    }

    private static int? ReadPid(string pidPath)
    {
        if (!File.Exists(pidPath) || !int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid))
        {
            return null;
        }

        return pid;
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsActive(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("starting", StringComparison.OrdinalIgnoreCase);

    private static void TryDeletePid(string pidPath)
    {
        try
        {
            File.Delete(pidPath);
        }
        catch (IOException)
        {
        }
    }
}

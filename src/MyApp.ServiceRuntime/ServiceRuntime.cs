using System.Text.Json;
using MyApp.Ipc;
using MyApp.Packaging.Models;

namespace MyApp.ServiceRuntime;

public sealed class ServiceRuntime(
    ServiceRegistry registry,
    ServiceCommandRunner commands,
    ServiceProcessMonitor monitor,
    ServiceSupervisor supervisor,
    ServiceLogRouter logs)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsAvailable(string serviceId) => registry.Resolve(serviceId)?.ExecutablePath is not null;

    public ServiceManifest? GetManifest(string serviceId) => registry.Resolve(serviceId)?.Manifest;

    public async Task<IReadOnlyList<AgentServiceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<AgentServiceInfo>();
        foreach (var service in registry.List())
        {
            var status = await StatusAsync(service.ServiceId, cancellationToken);
            results.Add(new AgentServiceInfo(
                service.ServiceId,
                service.Manifest.Name,
                service.Manifest.Version,
                service.Installed,
                status));
        }

        return results;
    }

    public async Task<AgentServiceStatus> StartAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        var service = registry.Require(serviceId);
        supervisor.MarkStartRequested(service);
        return await ExecuteStatusAsync(service, "start", allowRestart: false, cancellationToken);
    }

    public async Task<AgentServiceStatus> StopAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        var service = registry.Require(serviceId);
        supervisor.MarkStopRequested(service);
        return await ExecuteStatusAsync(service, "stop", allowRestart: false, cancellationToken);
    }

    public Task<AgentServiceStatus> StatusAsync(string serviceId, CancellationToken cancellationToken = default) =>
        ExecuteStatusAsync(registry.Require(serviceId), "status", allowRestart: true, cancellationToken);

    public Task<string> LogsAsync(string serviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(logs.Read(registry.Require(serviceId)));

    public Task ClearLogsAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        logs.Clear(registry.Require(serviceId));
        return Task.CompletedTask;
    }

    public async Task PrepareForUninstallAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var service = registry.Require(serviceId);
        supervisor.MarkStopRequested(service);

        if (service.ExecutablePath is not null)
        {
            try
            {
                EnsureSuccess(await commands.ExecuteAsync(service, "stop", cancellationToken));
            }
            catch (ServiceRuntimeException)
            {
                // PID-based termination below is the final authority for uninstall.
            }
        }

        await monitor.EnsureStoppedAsync(service, TimeSpan.FromSeconds(8), cancellationToken);
        await Task.Delay(150, cancellationToken);
    }

    private async Task<AgentServiceStatus> ExecuteStatusAsync(
        ServiceDescriptor service,
        string command,
        bool allowRestart,
        CancellationToken cancellationToken)
    {
        if (service.ExecutablePath is null)
        {
            return monitor.Missing(service, $"Service executable for '{service.ServiceId}' was not found.");
        }

        var result = await commands.ExecuteAsync(service, command, cancellationToken);
        EnsureSuccess(result);
        var status = JsonSerializer.Deserialize<AgentServiceStatus>(result.StandardOutput, JsonOptions)
            ?? throw new ServiceRuntimeException("service.invalidResponse", "Service returned an invalid status response.");
        status = monitor.Normalize(service, status);

        if (!allowRestart)
        {
            return status;
        }

        if (!supervisor.ShouldRestart(service, status, out var safeModeStatus))
        {
            return safeModeStatus;
        }

        EnsureSuccess(await commands.ExecuteAsync(service, "start", cancellationToken));
        return supervisor.Restarting(service, status);
    }

    private static void EnsureSuccess(ServiceCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new ServiceRuntimeException(
                "service.commandFailed",
                string.IsNullOrWhiteSpace(result.StandardError) ? "Service command failed." : result.StandardError.Trim());
        }
    }
}

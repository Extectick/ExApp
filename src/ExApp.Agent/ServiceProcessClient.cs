using ExApp.Ipc;
using ExApp.Packaging.Models;
using ExApp.ServiceRuntime;
using RuntimeService = ExApp.ServiceRuntime.ServiceRuntime;

namespace ExApp.Agent;

internal sealed class ServiceProcessClient(RuntimeService runtime)
{
    public bool IsAvailable(string serviceId) => runtime.IsAvailable(serviceId);

    public ServiceManifest? GetManifest(string serviceId) => runtime.GetManifest(serviceId);

    public Task<IReadOnlyList<AgentServiceInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        runtime.ListAsync(cancellationToken);

    public Task<AgentServiceStatus> StartAsync(string serviceId, CancellationToken cancellationToken = default) =>
        runtime.StartAsync(serviceId, cancellationToken);

    public Task<AgentServiceStatus> StopAsync(string serviceId, CancellationToken cancellationToken = default) =>
        runtime.StopAsync(serviceId, cancellationToken);

    public Task<AgentServiceStatus> RestartAsync(string serviceId, CancellationToken cancellationToken = default) =>
        runtime.RestartAsync(serviceId, cancellationToken);

    public Task<AgentServiceStatus> StatusAsync(string serviceId, CancellationToken cancellationToken = default) =>
        runtime.StatusAsync(serviceId, cancellationToken);

    public Task<string> LogsAsync(string serviceId) => runtime.LogsAsync(serviceId);

    public Task ClearLogsAsync(string serviceId) => runtime.ClearLogsAsync(serviceId);

    public Task<ServiceCommandResult> ExecuteAsync(
        string serviceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default) =>
        runtime.ExecuteAsync(serviceId, command, arguments, cancellationToken);

    public Task PrepareForUninstallAsync(string serviceId, CancellationToken cancellationToken = default) =>
        runtime.PrepareForUninstallAsync(serviceId, cancellationToken);
}

internal sealed class AgentCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

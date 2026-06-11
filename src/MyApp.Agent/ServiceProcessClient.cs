using MyApp.Ipc;
using MyApp.Packaging.Models;
using MyApp.ServiceRuntime;
using RuntimeService = MyApp.ServiceRuntime.ServiceRuntime;

namespace MyApp.Agent;

internal sealed class ServiceProcessClient(RuntimeService runtime)
{
    public bool IsAvailable(string serviceId) => runtime.IsAvailable(serviceId);

    public ServiceManifest? GetManifest(string serviceId) => runtime.GetManifest(serviceId);

    public Task<IReadOnlyList<AgentServiceInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        runtime.ListAsync(cancellationToken);

    public Task<AgentServiceStatus> StartAsync(string serviceId) => runtime.StartAsync(serviceId);

    public Task<AgentServiceStatus> StopAsync(string serviceId) => runtime.StopAsync(serviceId);

    public Task<AgentServiceStatus> StatusAsync(string serviceId) => runtime.StatusAsync(serviceId);

    public Task<string> LogsAsync(string serviceId) => runtime.LogsAsync(serviceId);

    public Task ClearLogsAsync(string serviceId) => runtime.ClearLogsAsync(serviceId);
}

internal sealed class AgentCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

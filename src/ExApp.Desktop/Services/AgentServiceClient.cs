using ExApp.Ipc;

namespace ExApp.Desktop.Services;

internal sealed class AgentServiceClient
{
    private readonly NamedPipeIpcClient _client = new(AgentProcessManager.PipeName);

    public async Task<bool> PingAsync()
    {
        try
        {
            await _client.SendAsync<object, object>(IpcCommands.AgentPing, new { }, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (IpcException)
        {
            return false;
        }
    }

    public Task<AgentServiceStatus?> StartAsync(string serviceId) =>
        SendStatusAsync(IpcCommands.ServiceStart, serviceId);

    public Task<AgentServiceStatus?> StopAsync(string serviceId) =>
        SendStatusAsync(IpcCommands.ServiceStop, serviceId);

    public Task<AgentServiceStatus?> RestartAsync(string serviceId) =>
        SendStatusAsync(IpcCommands.ServiceRestart, serviceId);

    public Task<AgentServiceStatus?> GetStatusAsync(string serviceId) =>
        SendStatusAsync(IpcCommands.ServiceStatus, serviceId);

    public async Task<IReadOnlyList<AgentServiceInfo>> ListAsync() =>
        await _client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { })
        ?? [];

    public Task<AgentDiagnosticsSnapshot?> GetDiagnosticsAsync() =>
        _client.SendAsync<object, AgentDiagnosticsSnapshot>(
            IpcCommands.AgentDiagnostics,
            new { },
            TimeSpan.FromSeconds(15));

    public async Task InstallAsync(string packagePath, string? expectedSha256 = null)
    {
        await _client.SendAsync<ServiceInstallRequest, object>(
            IpcCommands.ServiceInstall,
            new ServiceInstallRequest(packagePath, expectedSha256));
    }

    public Task<ServiceUpdateResult?> UpdateAsync(string packagePath, string? expectedSha256 = null) =>
        _client.SendAsync<ServiceUpdateRequest, ServiceUpdateResult>(
            IpcCommands.ServiceUpdate,
            new ServiceUpdateRequest(packagePath, expectedSha256),
            TimeSpan.FromSeconds(30));

    public async Task UninstallAsync(string serviceId, bool deleteData)
    {
        await _client.SendAsync<ServiceUninstallRequest, object>(
            IpcCommands.ServiceUninstall,
            new ServiceUninstallRequest(serviceId, deleteData));
    }

    public async Task<string> GetLogsAsync(string serviceId)
    {
        var result = await _client.SendAsync<ServiceCommandRequest, ServiceLogsResult>(
            IpcCommands.ServiceLogs,
            new ServiceCommandRequest(serviceId));
        return result?.Logs ?? string.Empty;
    }

    public async Task ClearLogsAsync(string serviceId)
    {
        await _client.SendAsync<ServiceCommandRequest, object>(
            IpcCommands.ServiceClearLogs,
            new ServiceCommandRequest(serviceId));
    }

    private Task<AgentServiceStatus?> SendStatusAsync(string command, string serviceId) =>
        _client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            command,
            new ServiceCommandRequest(serviceId));
}

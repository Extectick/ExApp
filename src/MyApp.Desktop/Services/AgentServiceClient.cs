using MyApp.Ipc;

namespace MyApp.Desktop.Services;

internal sealed class AgentServiceClient
{
    private const string MockServiceId = "mock-service";
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

    public Task<AgentServiceStatus?> StartAsync() => SendStatusAsync(IpcCommands.ServiceStart);
    public Task<AgentServiceStatus?> StopAsync() => SendStatusAsync(IpcCommands.ServiceStop);
    public Task<AgentServiceStatus?> GetStatusAsync() => SendStatusAsync(IpcCommands.ServiceStatus);

    public async Task<IReadOnlyList<AgentServiceInfo>> ListAsync() =>
        await _client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { })
        ?? [];

    public async Task InstallAsync(string packagePath, string? expectedSha256 = null)
    {
        await _client.SendAsync<ServiceInstallRequest, object>(
            IpcCommands.ServiceInstall,
            new ServiceInstallRequest(packagePath, expectedSha256));
    }

    public async Task UninstallAsync(bool deleteData)
    {
        await _client.SendAsync<ServiceUninstallRequest, object>(
            IpcCommands.ServiceUninstall,
            new ServiceUninstallRequest(MockServiceId, deleteData));
    }

    public async Task<string> GetLogsAsync()
    {
        var result = await _client.SendAsync<ServiceCommandRequest, ServiceLogsResult>(
            IpcCommands.ServiceLogs,
            new ServiceCommandRequest(MockServiceId));
        return result?.Logs ?? string.Empty;
    }

    public async Task ClearLogsAsync()
    {
        await _client.SendAsync<ServiceCommandRequest, object>(
            IpcCommands.ServiceClearLogs,
            new ServiceCommandRequest(MockServiceId));
    }

    private Task<AgentServiceStatus?> SendStatusAsync(string command) =>
        _client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            command,
            new ServiceCommandRequest(MockServiceId));
}

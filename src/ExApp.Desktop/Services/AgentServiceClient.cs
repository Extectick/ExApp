using ExApp.Ipc;

namespace ExApp.Desktop.Services;

internal sealed class AgentServiceClient
{
    private readonly NamedPipeIpcClient _client = new(AgentProcessManager.PipeName);
    private readonly AgentProcessManager _processManager = new();

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
        await SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { })
        ?? [];

    public Task<AgentDiagnosticsSnapshot?> GetDiagnosticsAsync() =>
        SendAsync<object, AgentDiagnosticsSnapshot>(
            IpcCommands.AgentDiagnostics,
            new { },
            TimeSpan.FromSeconds(15));

    public async Task InstallAsync(string packagePath, string? expectedSha256 = null)
    {
        await SendAsync<ServiceInstallRequest, object>(
            IpcCommands.ServiceInstall,
            new ServiceInstallRequest(packagePath, expectedSha256));
    }

    public Task<ServiceUpdateResult?> UpdateAsync(
        string packagePath,
        string? expectedSha256 = null,
        bool isDelta = false) =>
        SendAsync<ServiceUpdateRequest, ServiceUpdateResult>(
            IpcCommands.ServiceUpdate,
            new ServiceUpdateRequest(packagePath, expectedSha256, isDelta),
            TimeSpan.FromSeconds(30));

    public async Task UninstallAsync(string serviceId, bool deleteData)
    {
        await SendAsync<ServiceUninstallRequest, object>(
            IpcCommands.ServiceUninstall,
            new ServiceUninstallRequest(serviceId, deleteData));
    }

    public async Task<string> GetLogsAsync(string serviceId)
    {
        var result = await SendAsync<ServiceCommandRequest, ServiceLogsResult>(
            IpcCommands.ServiceLogs,
            new ServiceCommandRequest(serviceId));
        return result?.Logs ?? string.Empty;
    }

    public async Task ClearLogsAsync(string serviceId)
    {
        await SendAsync<ServiceCommandRequest, object>(
            IpcCommands.ServiceClearLogs,
            new ServiceCommandRequest(serviceId));
    }

    public Task<ServiceExecuteResult?> ExecuteAsync(
        string serviceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null) =>
        SendAsync<ServiceExecuteRequest, ServiceExecuteResult>(
            IpcCommands.ServiceExecute,
            new ServiceExecuteRequest(serviceId, command, arguments),
            timeout);

    private Task<AgentServiceStatus?> SendStatusAsync(string command, string serviceId) =>
        SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            command,
            new ServiceCommandRequest(serviceId));

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(
        string command,
        TRequest payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await _processManager.EnsureRunningAsync(cancellationToken);
        return await _client.SendAsync<TRequest, TResponse>(command, payload, timeout, cancellationToken);
    }
}

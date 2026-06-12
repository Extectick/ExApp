using System.Text.Json;

namespace ExApp.Ipc;

public static class IpcCommands
{
    public const string AgentPing = "agent.ping";
    public const string AgentDiagnostics = "agent.diagnostics";
    public const string ServiceList = "service.list";
    public const string ServiceInstall = "service.install";
    public const string ServiceUpdate = "service.update";
    public const string ServiceUninstall = "service.uninstall";
    public const string ServiceStart = "service.start";
    public const string ServiceRestart = "service.restart";
    public const string ServiceStop = "service.stop";
    public const string ServiceStatus = "service.status";
    public const string ServiceLogs = "service.logs";
    public const string ServiceClearLogs = "service.clearLogs";
    public const string ServiceRollback = "service.rollback";
}

public sealed record IpcRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Command { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }
}

public sealed record IpcResponse
{
    public string RequestId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public JsonElement? Result { get; init; }
    public IpcError? Error { get; init; }
}

public sealed record IpcError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record ServiceCommandRequest(string ServiceId);
public sealed record ServiceInstallRequest(string PackagePath, string? ExpectedSha256);
public sealed record ServiceUpdateRequest(string PackagePath, string? ExpectedSha256);
public sealed record ServiceUninstallRequest(string ServiceId, bool DeleteData);
public sealed record ServiceLogsResult(string ServiceId, string Logs);
public sealed record ServiceUpdateResult(
    string ServiceId,
    string PreviousVersion,
    string CurrentVersion,
    bool Restarted,
    AgentServiceStatus? Status);

public sealed record AgentServiceStatus(
    string ServiceId,
    string Version,
    string State,
    string Health,
    string Message,
    string? LastError,
    DateTimeOffset UpdatedAt);

public sealed record AgentServiceInfo(
    string ServiceId,
    string Name,
    string Version,
    bool Installed,
    AgentServiceStatus Status)
{
    public string LifecycleState { get; init; } = ServiceLifecycleStates.Installed;
    public AgentServiceRuntimeInfo? Runtime { get; init; }
}

public sealed record AgentServiceRuntimeInfo(
    int? ProcessId,
    DateTimeOffset? StartedAt,
    long? UptimeSeconds,
    string? ExecutablePath,
    string RuntimeDirectory,
    string DataDirectory,
    string LogsDirectory,
    string? PreviousVersion);

public sealed record AgentDiagnosticsSnapshot(
    string AgentVersion,
    string RootDirectory,
    DateTimeOffset GeneratedAt,
    int InstalledServices,
    int RunningServices,
    int FailedServices,
    IReadOnlyList<AgentServiceInfo> Services);

public static class ServiceLifecycleStates
{
    public const string NotInstalled = "not-installed";
    public const string Installed = "installed";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Failed = "failed";
}

public static class ServiceLifecycle
{
    public static string From(bool installed, AgentServiceStatus status)
    {
        if (!installed)
        {
            return ServiceLifecycleStates.NotInstalled;
        }

        return status.State.ToLowerInvariant() switch
        {
            "starting" => ServiceLifecycleStates.Starting,
            "running" => ServiceLifecycleStates.Running,
            "crashed" or "safe-mode" or "missing" or "error" => ServiceLifecycleStates.Failed,
            _ => ServiceLifecycleStates.Installed
        };
    }
}

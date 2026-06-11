using System.Text.Json;

namespace MyApp.Ipc;

public static class IpcCommands
{
    public const string AgentPing = "agent.ping";
    public const string ServiceList = "service.list";
    public const string ServiceInstall = "service.install";
    public const string ServiceUninstall = "service.uninstall";
    public const string ServiceStart = "service.start";
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
public sealed record ServiceUninstallRequest(string ServiceId, bool DeleteData);
public sealed record ServiceLogsResult(string ServiceId, string Logs);

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
    AgentServiceStatus Status);

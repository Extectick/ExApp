namespace ExApp.Core.Services;

public sealed record ServiceStatusDto(
    string ServiceId,
    string Version,
    ServiceState State,
    string Health,
    string Message,
    string? LastError,
    DateTimeOffset UpdatedAt);

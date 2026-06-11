namespace MyApp.ServiceRuntime;

public sealed record ServiceSupervisorState
{
    public string DesiredState { get; init; } = "stopped";
    public int RestartAttempts { get; init; }
    public DateTimeOffset RestartWindowStartedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool SafeMode { get; init; }
    public string? SafeModeReason { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

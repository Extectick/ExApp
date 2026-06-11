namespace MyApp.ServiceRuntime;

public sealed record ServiceRuntimeOptions
{
    public string RootDirectory { get; init; } = string.Empty;
    public TimeSpan CommandTimeout { get; init; } = default;
    public string? MockServiceExecutable { get; init; }
    public bool RestartEnabled { get; init; } = true;
    public int MaxRestartAttempts { get; init; } = 3;
    public TimeSpan RestartWindow { get; init; } = default;
}

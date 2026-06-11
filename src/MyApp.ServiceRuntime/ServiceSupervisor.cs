using System.Text.Json;
using MyApp.Ipc;

namespace MyApp.ServiceRuntime;

public sealed class ServiceSupervisor(ServiceRuntimeOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public void MarkStartRequested(ServiceDescriptor service) =>
        Write(service, new ServiceSupervisorState
        {
            DesiredState = "running",
            RestartAttempts = 0,
            RestartWindowStartedAt = DateTimeOffset.UtcNow,
            SafeMode = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    public void MarkStopRequested(ServiceDescriptor service) =>
        Write(service, new ServiceSupervisorState
        {
            DesiredState = "stopped",
            RestartAttempts = 0,
            RestartWindowStartedAt = DateTimeOffset.UtcNow,
            SafeMode = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    public bool ShouldRestart(ServiceDescriptor service, AgentServiceStatus status, out AgentServiceStatus safeModeStatus)
    {
        var state = Read(service);
        if (!options.RestartEnabled ||
            !state.DesiredState.Equals("running", StringComparison.OrdinalIgnoreCase) ||
            !status.State.Equals("crashed", StringComparison.OrdinalIgnoreCase))
        {
            safeModeStatus = status;
            return false;
        }

        if (state.SafeMode)
        {
            safeModeStatus = ToSafeModeStatus(service, state);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var window = options.RestartWindow == default ? TimeSpan.FromMinutes(2) : options.RestartWindow;
        var attempts = now - state.RestartWindowStartedAt > window
            ? 0
            : state.RestartAttempts;

        var maxAttempts = Math.Max(0, options.MaxRestartAttempts);
        if (attempts >= maxAttempts)
        {
            var safeState = state with
            {
                SafeMode = true,
                SafeModeReason = $"Restart policy stopped '{service.ServiceId}' after {attempts} failed restart attempts.",
                UpdatedAt = now
            };
            Write(service, safeState);
            safeModeStatus = ToSafeModeStatus(service, safeState);
            return false;
        }

        Write(service, state with
        {
            RestartAttempts = attempts + 1,
            RestartWindowStartedAt = attempts == 0 ? now : state.RestartWindowStartedAt,
            UpdatedAt = now
        });
        safeModeStatus = status;
        return true;
    }

    public AgentServiceStatus Restarting(ServiceDescriptor service, AgentServiceStatus status) =>
        status with
        {
            State = "starting",
            Health = "starting",
            Message = $"Restarting {service.Manifest.Name} after crash.",
            LastError = status.LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private ServiceSupervisorState Read(ServiceDescriptor service)
    {
        var path = GetStatePath(service);
        if (!File.Exists(path))
        {
            return new ServiceSupervisorState();
        }

        return JsonSerializer.Deserialize<ServiceSupervisorState>(File.ReadAllText(path), JsonOptions)
            ?? new ServiceSupervisorState();
    }

    private void Write(ServiceDescriptor service, ServiceSupervisorState state)
    {
        Directory.CreateDirectory(service.RuntimeDirectory);
        File.WriteAllText(GetStatePath(service), JsonSerializer.Serialize(state, JsonOptions));
    }

    private static string GetStatePath(ServiceDescriptor service) =>
        Path.Combine(service.RuntimeDirectory, "supervisor.json");

    private static AgentServiceStatus ToSafeModeStatus(ServiceDescriptor service, ServiceSupervisorState state) =>
        new(
            service.ServiceId,
            service.Manifest.Version,
            "safe-mode",
            "error",
            state.SafeModeReason ?? $"{service.Manifest.Name} is in safe mode.",
            state.SafeModeReason,
            state.UpdatedAt);
}

using MyApp.Ipc;
using MyApp.Packaging;
using MyApp.ServiceRuntime;

namespace MyApp.Agent;

internal sealed class AgentRequestHandler(
    PackageManager packageManager,
    ServiceProcessClient services)
{
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Command switch
            {
                IpcCommands.AgentPing => Success(request, new { status = "ok", version = "0.1.0" }),
                IpcCommands.ServiceList => await ListAsync(request),
                IpcCommands.ServiceInstall => await InstallAsync(request, cancellationToken),
                IpcCommands.ServiceUninstall => await UninstallAsync(request, cancellationToken),
                IpcCommands.ServiceStart => await ServiceStatusCommandAsync(request, services.StartAsync),
                IpcCommands.ServiceStop => await ServiceStatusCommandAsync(request, services.StopAsync),
                IpcCommands.ServiceStatus => await ServiceStatusCommandAsync(request, services.StatusAsync),
                IpcCommands.ServiceLogs => await LogsAsync(request),
                IpcCommands.ServiceClearLogs => await ClearLogsAsync(request),
                IpcCommands.ServiceRollback => await RollbackAsync(request, cancellationToken),
                _ => Error(request, "ipc.unknownCommand", $"Unknown command '{request.Command}'.")
            };
        }
        catch (PackageException exception)
        {
            return Error(request, exception.Code, exception.Message);
        }
        catch (ServiceRuntimeException exception)
        {
            return Error(request, exception.Code, exception.Message);
        }
        catch (AgentCommandException exception)
        {
            return Error(request, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            return Error(request, "agent.internalError", exception.Message);
        }
    }

    private async Task<IpcResponse> ListAsync(IpcRequest request)
    {
        return Success(request, await services.ListAsync());
    }

    private async Task<IpcResponse> InstallAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceInstallRequest>(request);
        var result = await packageManager.InstallAsync(payload.PackagePath, payload.ExpectedSha256, cancellationToken);
        return Success(request, result);
    }

    private async Task<IpcResponse> UninstallAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceUninstallRequest>(request);
        if (services.IsAvailable(payload.ServiceId))
        {
            try
            {
                await services.StopAsync(payload.ServiceId);
            }
            catch (AgentCommandException)
            {
            }
        }

        await packageManager.UninstallAsync(payload.ServiceId, payload.DeleteData, cancellationToken);
        return Success(request, new { payload.ServiceId, uninstalled = true });
    }

    private async Task<IpcResponse> RollbackAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        try
        {
            await services.StopAsync(payload.ServiceId);
        }
        catch (AgentCommandException)
        {
        }

        return Success(request, await packageManager.RollbackAsync(payload.ServiceId, cancellationToken));
    }

    private async Task<IpcResponse> ServiceStatusCommandAsync(
        IpcRequest request,
        Func<string, Task<AgentServiceStatus>> command)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        return Success(request, await command(payload.ServiceId));
    }

    private async Task<IpcResponse> LogsAsync(IpcRequest request)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        return Success(request, new ServiceLogsResult(payload.ServiceId, await services.LogsAsync(payload.ServiceId)));
    }

    private async Task<IpcResponse> ClearLogsAsync(IpcRequest request)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        await services.ClearLogsAsync(payload.ServiceId);
        return Success(request, new { payload.ServiceId, cleared = true });
    }

    private static T RequirePayload<T>(IpcRequest request) =>
        IpcJson.FromElement<T>(request.Payload)
        ?? throw new AgentCommandException("ipc.invalidPayload", $"Command '{request.Command}' payload is invalid.");

    private static IpcResponse Success<T>(IpcRequest request, T result) =>
        NamedPipeIpcServer.Success(request.RequestId, result);

    private static IpcResponse Error(IpcRequest request, string code, string message) =>
        NamedPipeIpcServer.Error(request.RequestId, code, message);
}

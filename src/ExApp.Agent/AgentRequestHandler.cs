using System.Collections.Concurrent;
using ExApp.Ipc;
using ExApp.Packaging;
using ExApp.ServiceRuntime;
using System.Reflection;

namespace ExApp.Agent;

internal sealed class AgentRequestHandler(
    PackageManager packageManager,
    ServiceProcessClient services)
{
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serviceOperationLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Command switch
            {
                IpcCommands.AgentPing => Success(request, new { status = "ok", version = AgentVersion }),
                IpcCommands.AgentDiagnostics => await DiagnosticsAsync(request, cancellationToken),
                IpcCommands.ServiceList => await ListAsync(request),
                IpcCommands.ServiceInstall => await InstallAsync(request, cancellationToken),
                IpcCommands.ServiceUpdate => await UpdateAsync(request, cancellationToken),
                IpcCommands.ServiceUninstall => await UninstallAsync(request, cancellationToken),
                IpcCommands.ServiceStart => await LockedServiceStatusCommandAsync(
                    request,
                    serviceId => services.StartAsync(serviceId, cancellationToken),
                    cancellationToken),
                IpcCommands.ServiceStop => await LockedServiceStatusCommandAsync(
                    request,
                    serviceId => services.StopAsync(serviceId, cancellationToken),
                    cancellationToken),
                IpcCommands.ServiceRestart => await LockedServiceStatusCommandAsync(
                    request,
                    serviceId => services.RestartAsync(serviceId, cancellationToken),
                    cancellationToken),
                IpcCommands.ServiceStatus => await ServiceStatusCommandAsync(
                    request,
                    serviceId => services.StatusAsync(serviceId, cancellationToken)),
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

    private async Task<IpcResponse> DiagnosticsAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var serviceList = await services.ListAsync(cancellationToken);
        return Success(request, new AgentDiagnosticsSnapshot(
            AgentVersion,
            packageManager.RootDirectory,
            DateTimeOffset.UtcNow,
            serviceList.Count(service => service.Installed),
            serviceList.Count(service =>
                service.LifecycleState.Equals(ServiceLifecycleStates.Running, StringComparison.OrdinalIgnoreCase) ||
                service.LifecycleState.Equals(ServiceLifecycleStates.Starting, StringComparison.OrdinalIgnoreCase)),
            serviceList.Count(service =>
                service.LifecycleState.Equals(ServiceLifecycleStates.Failed, StringComparison.OrdinalIgnoreCase)),
            serviceList));
    }

    private async Task<IpcResponse> InstallAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceInstallRequest>(request);
        var manifest = await packageManager.InspectPackageManifestAsync(
            payload.PackagePath,
            payload.ExpectedSha256,
            cancellationToken);
        return await WithServiceOperationLockAsync(request, manifest.Id, async () =>
        {
            var result = await packageManager.InstallAsync(payload.PackagePath, payload.ExpectedSha256, cancellationToken);
            return Success(request, result);
        }, cancellationToken);
    }

    private async Task<IpcResponse> UninstallAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceUninstallRequest>(request);
        return await WithServiceOperationLockAsync(request, payload.ServiceId, async () =>
        {
            try
            {
                await services.PrepareForUninstallAsync(payload.ServiceId, cancellationToken);
            }
            catch (ServiceRuntimeException exception) when (exception.Code is "service.notFound")
            {
            }

            await packageManager.UninstallAsync(payload.ServiceId, payload.DeleteData, cancellationToken);
            return Success(request, new { payload.ServiceId, uninstalled = true });
        }, cancellationToken);
    }

    private async Task<IpcResponse> UpdateAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceUpdateRequest>(request);
        var manifest = await packageManager.InspectPackageManifestAsync(
            payload.PackagePath,
            payload.ExpectedSha256,
            cancellationToken);

        return await WithServiceOperationLockAsync(request, manifest.Id, async () =>
        {
            var previousState = packageManager.GetState(manifest.Id)
                ?? throw new AgentCommandException(
                    "service.notInstalled",
                    $"Service '{manifest.Id}' must be installed before it can be updated.");
            if (previousState.CurrentVersion.Equals(manifest.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentCommandException(
                    "service.updateSameVersion",
                    $"Service '{manifest.Id}' is already on version {manifest.Version}.");
            }

            var previousStatus = await services.StatusAsync(manifest.Id, cancellationToken);
            var shouldRestart = IsActive(previousStatus);

            await services.PrepareForUninstallAsync(manifest.Id, cancellationToken);

            ExApp.Packaging.Models.PackageInstallResult installResult;
            try
            {
                installResult = await packageManager.InstallAsync(
                    payload.PackagePath,
                    payload.ExpectedSha256,
                    cancellationToken);
            }
            catch (Exception updateException)
            {
                if (shouldRestart)
                {
                    await RestorePreviousRuntimeAsync(manifest.Id, updateException, CancellationToken.None);
                }

                throw;
            }

            AgentServiceStatus? updatedStatus = null;
            if (shouldRestart)
            {
                try
                {
                    await services.StartAsync(manifest.Id, cancellationToken);
                    updatedStatus = await WaitForHealthyAsync(manifest.Id, cancellationToken);
                }
                catch (Exception updateException)
                {
                    await RollbackAfterFailedUpdateAsync(
                        manifest.Id,
                        previousState.CurrentVersion,
                        updateException,
                        CancellationToken.None);
                }
            }

            return Success(request, new ServiceUpdateResult(
                manifest.Id,
                previousState.CurrentVersion,
                installResult.State.CurrentVersion,
                shouldRestart,
                updatedStatus));
        }, cancellationToken);
    }

    private async Task<IpcResponse> RollbackAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        return await WithServiceOperationLockAsync(request, payload.ServiceId, async () =>
        {
            try
            {
                await services.PrepareForUninstallAsync(payload.ServiceId, cancellationToken);
            }
            catch (ServiceRuntimeException exception) when (exception.Code is "service.notFound")
            {
            }

            return Success(request, await packageManager.RollbackAsync(payload.ServiceId, cancellationToken));
        }, cancellationToken);
    }

    private async Task<IpcResponse> LockedServiceStatusCommandAsync(
        IpcRequest request,
        Func<string, Task<AgentServiceStatus>> command,
        CancellationToken cancellationToken)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        return await WithServiceOperationLockAsync(
            request,
            payload.ServiceId,
            async () => Success(request, await command(payload.ServiceId)),
            cancellationToken);
    }

    private async Task<IpcResponse> WithServiceOperationLockAsync(
        IpcRequest request,
        string serviceId,
        Func<Task<IpcResponse>> operation,
        CancellationToken cancellationToken)
    {
        var gate = _serviceOperationLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Error(request, "service.operationInProgress", $"Service '{serviceId}' already has an active operation.");
        }

        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IpcResponse> ServiceStatusCommandAsync(
        IpcRequest request,
        Func<string, Task<AgentServiceStatus>> command)
    {
        var payload = RequirePayload<ServiceCommandRequest>(request);
        return Success(request, await command(payload.ServiceId));
    }

    private async Task<AgentServiceStatus> WaitForHealthyAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        AgentServiceStatus? lastStatus = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastStatus = await services.StatusAsync(serviceId, cancellationToken);
            if (lastStatus.State.Equals("running", StringComparison.OrdinalIgnoreCase) &&
                lastStatus.Health.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return lastStatus;
            }

            if (IsFailed(lastStatus))
            {
                throw new ServiceRuntimeException(
                    "service.updateHealthCheckFailed",
                    lastStatus.LastError ?? lastStatus.Message);
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new ServiceRuntimeException(
            "service.updateHealthCheckTimeout",
            lastStatus is null
                ? $"Service '{serviceId}' did not report health after update."
                : $"Service '{serviceId}' did not become healthy after update. Last state: {lastStatus.State}/{lastStatus.Health}.");
    }

    private async Task RollbackAfterFailedUpdateAsync(
        string serviceId,
        string previousVersion,
        Exception updateException,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await services.PrepareForUninstallAsync(serviceId, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A damaged updated executable may not support stop; rollback still must proceed.
            }

            await packageManager.RollbackAsync(serviceId, cancellationToken);
            await services.StartAsync(serviceId, cancellationToken);
            await WaitForHealthyAsync(serviceId, cancellationToken);
        }
        catch (Exception rollbackException)
        {
            throw new AgentCommandException(
                "service.updateRollbackFailed",
                $"Update failed: {updateException.Message} Rollback to {previousVersion} also failed: {rollbackException.Message}");
        }

        throw new AgentCommandException(
            "service.updateRolledBack",
            $"The updated service failed its health check and was rolled back to {previousVersion}. {updateException.Message}");
    }

    private async Task RestorePreviousRuntimeAsync(
        string serviceId,
        Exception updateException,
        CancellationToken cancellationToken)
    {
        try
        {
            await services.StartAsync(serviceId, cancellationToken);
            await WaitForHealthyAsync(serviceId, cancellationToken);
        }
        catch (Exception restoreException)
        {
            throw new AgentCommandException(
                "service.updateRestoreFailed",
                $"Update failed before installation completed: {updateException.Message} The previous service could not be restarted: {restoreException.Message}");
        }
    }

    private static bool IsActive(AgentServiceStatus status) =>
        status.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        status.State.Equals("starting", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(AgentServiceStatus status) =>
        status.State.Equals("crashed", StringComparison.OrdinalIgnoreCase) ||
        status.State.Equals("safe-mode", StringComparison.OrdinalIgnoreCase) ||
        status.State.Equals("missing", StringComparison.OrdinalIgnoreCase) ||
        status.State.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        status.Health.Equals("error", StringComparison.OrdinalIgnoreCase);

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

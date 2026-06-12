using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExApp.Ipc.Tests;

public sealed class AgentIntegrationTests : IAsyncLifetime
{
    private readonly string _runtimeRoot = Path.Combine(Path.GetTempPath(), "ExApp.AgentTests", Guid.NewGuid().ToString("N"));
    private Process? _agent;

    public async Task InitializeAsync()
    {
        foreach (var process in Process.GetProcessesByName("ExApp.Agent"))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        var repositoryRoot = FindRepositoryRoot();
        var configuration = GetBuildConfiguration();
        var agentExecutable = Path.Combine(repositoryRoot, "src", "ExApp.Agent", "bin", configuration, "net8.0", "ExApp.Agent.exe");
        var mockExecutable = Path.Combine(repositoryRoot, "services", "MockService", "bin", configuration, "net8.0", "ExApp.Service.MockService.exe");
        Assert.True(File.Exists(agentExecutable), $"Agent executable not found at {agentExecutable}");
        Assert.True(File.Exists(mockExecutable), $"Mock executable not found at {mockExecutable}");

        Directory.CreateDirectory(_runtimeRoot);
        var startInfo = new ProcessStartInfo(agentExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["EXAPP_ROOT"] = _runtimeRoot;
        startInfo.Environment["EXAPP_MOCK_SERVICE_EXE"] = mockExecutable;
        _agent = Process.Start(startInfo);

        var client = new NamedPipeIpcClient("ExApp.Agent.v1");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await client.SendAsync<object, object>(IpcCommands.AgentPing, new { }, TimeSpan.FromMilliseconds(300));
                return;
            }
            catch (IpcException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("Agent did not start.");
    }

    [Fact]
    public async Task Agent_ControlsMockServiceThroughIpc()
    {
        var client = new NamedPipeIpcClient("ExApp.Agent.v1");
        var packagePath = CreateMockPackage();
        await client.SendAsync<ServiceInstallRequest, object>(
            IpcCommands.ServiceInstall,
            new ServiceInstallRequest(packagePath, null));

        var services = await client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { });
        Assert.Contains(services!, item => item.ServiceId == "mock-service" && item.Installed);

        var started = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            IpcCommands.ServiceStart,
            new ServiceCommandRequest("mock-service"));
        Assert.Equal("starting", started?.State);

        await Task.Delay(1800);
        var status = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            IpcCommands.ServiceStatus,
            new ServiceCommandRequest("mock-service"));
        Assert.Equal("running", status?.State);

        var runningServices = await client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { });
        var runningService = Assert.Single(runningServices!, item => item.ServiceId == "mock-service" && item.Installed);
        Assert.NotNull(runningService.Runtime?.ProcessId);
        Assert.NotNull(runningService.Runtime?.StartedAt);
        Assert.NotNull(runningService.Runtime?.UptimeSeconds);
        Assert.False(string.IsNullOrWhiteSpace(runningService.Runtime?.ExecutablePath));

        var originalPid = runningService.Runtime!.ProcessId;
        var restartRequested = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            IpcCommands.ServiceRestart,
            new ServiceCommandRequest("mock-service"));
        Assert.Equal("starting", restartRequested?.State);
        await WaitForStateAsync(client, "running");
        var restartedServices = await client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { });
        var restartedService = Assert.Single(restartedServices!, item => item.ServiceId == "mock-service" && item.Installed);
        Assert.NotEqual(originalPid, restartedService.Runtime?.ProcessId);

        var diagnostics = await client.SendAsync<object, AgentDiagnosticsSnapshot>(
            IpcCommands.AgentDiagnostics,
            new { });
        Assert.Equal(1, diagnostics?.InstalledServices);
        Assert.Equal(1, diagnostics?.RunningServices);
        Assert.Equal(0, diagnostics?.FailedServices);
        Assert.Contains(diagnostics!.Services, item => item.ServiceId == "mock-service");

        var logs = await client.SendAsync<ServiceCommandRequest, ServiceLogsResult>(
            IpcCommands.ServiceLogs,
            new ServiceCommandRequest("mock-service"));
        Assert.Contains("Heartbeat loop started", logs?.Logs);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            KillMockServiceProcess();
            await Task.Delay(500);

            var restarting = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
                IpcCommands.ServiceStatus,
                new ServiceCommandRequest("mock-service"));
            Assert.Equal("starting", restarting?.State);

            await Task.Delay(1800);
            var restarted = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
                IpcCommands.ServiceStatus,
                new ServiceCommandRequest("mock-service"));
            Assert.Equal("running", restarted?.State);
        }

        KillMockServiceProcess();
        await Task.Delay(500);
        var safeMode = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            IpcCommands.ServiceStatus,
            new ServiceCommandRequest("mock-service"));
        Assert.Equal("safe-mode", safeMode?.State);

        var stopped = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            IpcCommands.ServiceStop,
            new ServiceCommandRequest("mock-service"));
        Assert.Equal("stopped", stopped?.State);

        await client.SendAsync<ServiceUninstallRequest, object>(
            IpcCommands.ServiceUninstall,
            new ServiceUninstallRequest("mock-service", DeleteData: true));
        var afterUninstall = await client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { });
        Assert.Contains(afterUninstall!, item => item.ServiceId == "mock-service" && !item.Installed);
    }

    [Fact]
    public async Task Agent_UpdateRestartsServiceAndRollsBackUnhealthyVersion()
    {
        var client = new NamedPipeIpcClient("ExApp.Agent.v1");
        await client.SendAsync<ServiceInstallRequest, object>(
            IpcCommands.ServiceInstall,
            new ServiceInstallRequest(CreateMockPackage("0.1.0"), null));
        await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
            IpcCommands.ServiceStart,
            new ServiceCommandRequest("mock-service"));
        await WaitForStateAsync(client, "running");

        var update = await client.SendAsync<ServiceUpdateRequest, ServiceUpdateResult>(
            IpcCommands.ServiceUpdate,
            new ServiceUpdateRequest(CreateMockPackage("0.1.1"), null),
            TimeSpan.FromSeconds(20));

        Assert.Equal("0.1.0", update?.PreviousVersion);
        Assert.Equal("0.1.1", update?.CurrentVersion);
        Assert.True(update?.Restarted == true);
        Assert.Equal("running", update?.Status?.State);

        var updateException = await Assert.ThrowsAsync<IpcException>(
            () => client.SendAsync<ServiceUpdateRequest, ServiceUpdateResult>(
                IpcCommands.ServiceUpdate,
                new ServiceUpdateRequest(CreateMockPackage("0.1.2", brokenExecutable: true), null),
                TimeSpan.FromSeconds(20)));

        Assert.Equal("service.updateRolledBack", updateException.Code);
        var services = await client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { });
        var service = Assert.Single(services!, item => item.ServiceId == "mock-service" && item.Installed);
        Assert.Equal("0.1.1", service.Version);
        Assert.Equal("running", service.Status.State);
    }

    [Fact]
    public async Task Agent_UpdateKeepsStoppedServiceStopped()
    {
        var client = new NamedPipeIpcClient("ExApp.Agent.v1");
        await client.SendAsync<ServiceInstallRequest, object>(
            IpcCommands.ServiceInstall,
            new ServiceInstallRequest(CreateMockPackage("0.1.0"), null));

        var update = await client.SendAsync<ServiceUpdateRequest, ServiceUpdateResult>(
            IpcCommands.ServiceUpdate,
            new ServiceUpdateRequest(CreateMockPackage("0.1.1"), null),
            TimeSpan.FromSeconds(20));

        Assert.False(update?.Restarted == true);
        Assert.Null(update?.Status);
        var services = await client.SendAsync<object, AgentServiceInfo[]>(IpcCommands.ServiceList, new { });
        var service = Assert.Single(services!, item => item.ServiceId == "mock-service" && item.Installed);
        Assert.Equal("0.1.1", service.Version);
        Assert.Equal("stopped", service.Status.State);
    }

    public Task DisposeAsync()
    {
        if (_agent is { HasExited: false })
        {
            _agent.Kill(entireProcessTree: true);
            _agent.WaitForExit(5000);
        }

        if (Directory.Exists(_runtimeRoot))
        {
            foreach (var pidPath in Directory.EnumerateFiles(_runtimeRoot, "service.pid", SearchOption.AllDirectories))
            {
                if (!int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid))
                {
                    continue;
                }

                try
                {
                    var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (ArgumentException)
                {
                }
            }
        }

        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ExApp.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private string CreateMockPackage(string version = "0.1.0", bool brokenExecutable = false)
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceBin = Path.Combine(repositoryRoot, "services", "MockService", "bin", GetBuildConfiguration(), "net8.0");
        var staging = Path.Combine(_runtimeRoot, $"package-source-{Guid.NewGuid():N}");
        var packageBin = Path.Combine(staging, "bin");
        Directory.CreateDirectory(packageBin);

        foreach (var file in Directory.EnumerateFiles(sourceBin, "ExApp.Service.MockService.*"))
        {
            File.Copy(file, Path.Combine(packageBin, Path.GetFileName(file)));
        }

        File.Copy(
            Path.Combine(repositoryRoot, "services", "MockService", "service.manifest.json"),
            Path.Combine(staging, "service.manifest.json"));
        var manifestPath = Path.Combine(staging, "service.manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["version"] = version;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        if (brokenExecutable)
        {
            File.WriteAllText(
                Path.Combine(packageBin, "ExApp.Service.MockService.exe"),
                "This package intentionally contains an invalid executable.");
        }

        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                path = Path.GetRelativePath(staging, path).Replace('\\', '/'),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            })
            .OrderBy(item => item.path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(
            Path.Combine(staging, "checksums.json"),
            JsonSerializer.Serialize(new { algorithm = "sha256", files }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(staging, "signature.sig"), "integration-test");

        var packagePath = Path.Combine(_runtimeRoot, $"mock-service-{version}-{Guid.NewGuid():N}.svcpkg");
        ZipFile.CreateFromDirectory(staging, packagePath);
        Directory.Delete(staging, recursive: true);
        return packagePath;
    }

    private static async Task<AgentServiceStatus> WaitForStateAsync(
        NamedPipeIpcClient client,
        string expectedState)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.SendAsync<ServiceCommandRequest, AgentServiceStatus>(
                IpcCommands.ServiceStatus,
                new ServiceCommandRequest("mock-service"));
            if (status?.State.Equals(expectedState, StringComparison.OrdinalIgnoreCase) == true)
            {
                return status;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Mock Service did not reach state '{expectedState}'.");
    }

    private static string GetBuildConfiguration()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (current.Name is "Debug" or "Release")
            {
                return current.Name;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Build configuration was not found in the test output path.");
    }

    private void KillMockServiceProcess()
    {
        var pidPath = Path.Combine(_runtimeRoot, "services", "mock-service", "data", "runtime", "service.pid");
        var pid = int.Parse(File.ReadAllText(pidPath).Trim());
        var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }
}

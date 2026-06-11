using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyApp.Ipc.Tests;

public sealed class AgentIntegrationTests : IAsyncLifetime
{
    private readonly string _runtimeRoot = Path.Combine(Path.GetTempPath(), "ExApp.AgentTests", Guid.NewGuid().ToString("N"));
    private Process? _agent;

    public async Task InitializeAsync()
    {
        foreach (var process in Process.GetProcessesByName("MyApp.Agent"))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        var repositoryRoot = FindRepositoryRoot();
        var agentExecutable = Path.Combine(repositoryRoot, "src", "MyApp.Agent", "bin", "Debug", "net8.0", "MyApp.Agent.exe");
        var mockExecutable = Path.Combine(repositoryRoot, "services", "MockService", "bin", "Debug", "net8.0", "MyApp.Service.MockService.exe");
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

    public Task DisposeAsync()
    {
        if (_agent is { HasExited: false })
        {
            _agent.Kill(entireProcessTree: true);
            _agent.WaitForExit(5000);
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
            if (File.Exists(Path.Combine(current.FullName, "MyApp.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private string CreateMockPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceBin = Path.Combine(repositoryRoot, "services", "MockService", "bin", "Debug", "net8.0");
        var staging = Path.Combine(_runtimeRoot, "package-source");
        var packageBin = Path.Combine(staging, "bin");
        Directory.CreateDirectory(packageBin);

        foreach (var file in Directory.EnumerateFiles(sourceBin, "MyApp.Service.MockService.*"))
        {
            File.Copy(file, Path.Combine(packageBin, Path.GetFileName(file)));
        }

        File.Copy(
            Path.Combine(repositoryRoot, "services", "MockService", "service.manifest.json"),
            Path.Combine(staging, "service.manifest.json"));

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

        var packagePath = Path.Combine(_runtimeRoot, "mock-service.svcpkg");
        ZipFile.CreateFromDirectory(staging, packagePath);
        return packagePath;
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

using System.Text.Json;
using MyApp.Packaging;
using MyApp.Packaging.Models;

namespace MyApp.ServiceRuntime;

public sealed class ServiceRegistry(PackageManager packageManager, ServiceRuntimeOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<ServiceDescriptor> List()
    {
        var installed = packageManager.GetInstalledServices()
            .Select(item => item.Id)
            .Append("mock-service")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return installed
            .Select(Resolve)
            .Where(descriptor => descriptor is not null)
            .Cast<ServiceDescriptor>()
            .OrderBy(descriptor => descriptor.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ServiceDescriptor? Resolve(string serviceId)
    {
        var runtimeDirectory = GetRuntimeDirectory(serviceId);
        var state = packageManager.GetState(serviceId);
        if (state is not null)
        {
            var manifest = ReadInstalledManifest(serviceId);
            var executable = ResolveInstalledExecutable(serviceId, manifest);
            return new ServiceDescriptor(serviceId, manifest, executable, Installed: true, runtimeDirectory);
        }

        if (!serviceId.Equals("mock-service", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mockExecutable = ResolveMockServiceExecutable();
        if (mockExecutable is null)
        {
            return null;
        }

        return new ServiceDescriptor(
            serviceId,
            new ServiceManifest
            {
                Id = "mock-service",
                Name = "Mock Service",
                Version = "0.1.0",
                Entry = new ServiceEntry { Executable = mockExecutable }
            },
            mockExecutable,
            Installed: false,
            runtimeDirectory);
    }

    public ServiceDescriptor Require(string serviceId) =>
        Resolve(serviceId) ?? throw new ServiceRuntimeException("service.notFound", $"Service '{serviceId}' was not found.");

    public string GetRuntimeDirectory(string serviceId) =>
        Path.Combine(options.RootDirectory, "services", serviceId, "data", "runtime");

    private ServiceManifest ReadInstalledManifest(string serviceId)
    {
        var manifestPath = Path.Combine(packageManager.ResolveCurrentVersionDirectory(serviceId), "service.manifest.json");
        return JsonSerializer.Deserialize<ServiceManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new ServiceRuntimeException("service.invalidManifest", $"Service '{serviceId}' manifest is invalid.");
    }

    private string? ResolveInstalledExecutable(string serviceId, ServiceManifest manifest)
    {
        var executable = Path.GetFullPath(Path.Combine(
            packageManager.ResolveCurrentVersionDirectory(serviceId),
            manifest.Entry.Executable.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(executable) ? executable : null;
    }

    private string? ResolveMockServiceExecutable()
    {
        if (!string.IsNullOrWhiteSpace(options.MockServiceExecutable) &&
            File.Exists(options.MockServiceExecutable))
        {
            return options.MockServiceExecutable;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "services", "MockService", "bin", "Debug", "net8.0", "MyApp.Service.MockService.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}

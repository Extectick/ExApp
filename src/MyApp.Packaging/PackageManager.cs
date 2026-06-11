using MyApp.Packaging.Models;

namespace MyApp.Packaging;

public sealed class PackageManager
{
    private readonly PackageManagerOptions _options;
    private readonly ServicePackageReader _reader;
    private readonly PackageValidator _validator;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public PackageManager(PackageManagerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        _options = options with { RootDirectory = Path.GetFullPath(options.RootDirectory) };
        _reader = new ServicePackageReader(_options);
        _validator = new PackageValidator(_options);
    }

    public async Task<PackageInstallResult> InstallAsync(
        string packagePath,
        string? expectedPackageSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        packagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(packagePath))
        {
            throw new PackageException("package.notFound", $"Package '{packagePath}' was not found.");
        }

        await _operationLock.WaitAsync(cancellationToken);
        var stagingDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageHash = await _reader.ComputePackageHashAsync(packagePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedPackageSha256) &&
                !packageHash.Equals(expectedPackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("package.hashMismatch", "Package SHA-256 does not match the expected catalog hash.");
            }

            _reader.ExtractSecure(packagePath, stagingDirectory);
            var manifest = _reader.ReadManifest(stagingDirectory);
            _validator.ValidateManifest(manifest, stagingDirectory);
            _validator.ValidateChecksums(stagingDirectory, _reader.ReadChecksums(stagingDirectory));
            _validator.ValidateSignaturePlaceholder(stagingDirectory);

            var serviceRoot = GetServiceRoot(manifest.Id);
            var versionDirectory = Path.Combine(serviceRoot, "versions", manifest.Version);
            var statePath = Path.Combine(serviceRoot, "package-state.json");
            var existingState = AtomicJsonStore.Read<PackageState>(statePath);

            if (Directory.Exists(versionDirectory))
            {
                if (existingState?.CurrentVersion == manifest.Version)
                {
                    CachePackage(packagePath, packageHash);
                    return new PackageInstallResult(manifest, existingState, versionDirectory, AlreadyInstalled: true);
                }

                throw new PackageException("package.versionExists", $"Service '{manifest.Id}' version '{manifest.Version}' is already installed.");
            }

            Directory.CreateDirectory(Path.Combine(serviceRoot, "versions"));
            Directory.CreateDirectory(Path.Combine(serviceRoot, "data"));
            Directory.CreateDirectory(Path.Combine(serviceRoot, "logs"));
            Directory.Move(stagingDirectory, versionDirectory);

            var now = DateTimeOffset.UtcNow;
            var state = new PackageState
            {
                ServiceId = manifest.Id,
                CurrentVersion = manifest.Version,
                PreviousVersion = existingState?.CurrentVersion,
                State = "installed",
                UpdatedAt = now
            };
            AtomicJsonStore.Write(statePath, state);
            UpdateRegistry(manifest, state, existingState is null ? now : null);
            CachePackage(packagePath, packageHash);

            return new PackageInstallResult(manifest, state, versionDirectory, AlreadyInstalled: false);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            _operationLock.Release();
        }
    }

    public async Task<PackageState> RollbackAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        ValidateServiceId(serviceId);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var serviceRoot = GetServiceRoot(serviceId);
            var statePath = Path.Combine(serviceRoot, "package-state.json");
            var state = AtomicJsonStore.Read<PackageState>(statePath)
                ?? throw new PackageException("service.notInstalled", $"Service '{serviceId}' is not installed.");

            if (string.IsNullOrWhiteSpace(state.PreviousVersion))
            {
                throw new PackageException("rollback.unavailable", $"Service '{serviceId}' has no previous version.");
            }

            var previousDirectory = Path.Combine(serviceRoot, "versions", state.PreviousVersion);
            if (!Directory.Exists(previousDirectory))
            {
                throw new PackageException("rollback.versionMissing", $"Previous version '{state.PreviousVersion}' is missing.");
            }

            var rolledBackState = state with
            {
                CurrentVersion = state.PreviousVersion,
                PreviousVersion = state.CurrentVersion,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            AtomicJsonStore.Write(statePath, rolledBackState);
            UpdateRegistryCurrentVersion(serviceId, rolledBackState.CurrentVersion);
            return rolledBackState;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task UninstallAsync(
        string serviceId,
        bool deleteData,
        CancellationToken cancellationToken = default)
    {
        ValidateServiceId(serviceId);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var serviceRoot = GetServiceRoot(serviceId);
            if (!Directory.Exists(serviceRoot))
            {
                RemoveFromRegistry(serviceId);
                return;
            }

            TryDeleteDirectory(Path.Combine(serviceRoot, "versions"));
            TryDeleteFile(Path.Combine(serviceRoot, "package-state.json"));

            if (deleteData)
            {
                TryDeleteDirectory(serviceRoot);
            }
            else
            {
                DeleteServiceMetadataExceptData(serviceRoot);
            }

            RemoveFromRegistry(serviceId);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public PackageState? GetState(string serviceId)
    {
        ValidateServiceId(serviceId);
        return AtomicJsonStore.Read<PackageState>(Path.Combine(GetServiceRoot(serviceId), "package-state.json"));
    }

    public string ResolveCurrentVersionDirectory(string serviceId)
    {
        var state = GetState(serviceId)
            ?? throw new PackageException("service.notInstalled", $"Service '{serviceId}' is not installed.");
        var directory = Path.Combine(GetServiceRoot(serviceId), "versions", state.CurrentVersion);
        if (!Directory.Exists(directory))
        {
            throw new PackageException("service.currentVersionMissing", $"Current version '{state.CurrentVersion}' is missing.");
        }

        return directory;
    }

    public IReadOnlyList<InstalledServiceRecord> GetInstalledServices() =>
        ReadRegistry().Services;

    private string ServicesRoot => Path.Combine(_options.RootDirectory, "services");
    private string StagingRoot => Path.Combine(_options.RootDirectory, "packages", "staging");
    private string CacheRoot => Path.Combine(_options.RootDirectory, "packages", "cache");
    private string RegistryPath => Path.Combine(_options.RootDirectory, "registry", "installed-services.json");

    private string GetServiceRoot(string serviceId) => Path.Combine(ServicesRoot, serviceId);

    private void CachePackage(string packagePath, string packageHash)
    {
        Directory.CreateDirectory(CacheRoot);
        var cachePath = Path.Combine(CacheRoot, $"{packageHash}.svcpkg");
        if (!File.Exists(cachePath))
        {
            File.Copy(packagePath, cachePath);
        }
    }

    private void UpdateRegistry(ServiceManifest manifest, PackageState state, DateTimeOffset? installedAt)
    {
        var registry = ReadRegistry();
        var services = registry.Services.ToList();
        var existingIndex = services.FindIndex(item => item.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
        var existing = existingIndex >= 0 ? services[existingIndex] : null;
        var record = new InstalledServiceRecord
        {
            Id = manifest.Id,
            Name = manifest.Name,
            PublisherId = manifest.Publisher.Id,
            CurrentVersion = state.CurrentVersion,
            ManifestPath = Path.Combine("services", manifest.Id, "versions", state.CurrentVersion, "service.manifest.json")
                .Replace('\\', '/'),
            InstalledAt = installedAt ?? existing?.InstalledAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = state.UpdatedAt
        };

        if (existingIndex >= 0)
        {
            services[existingIndex] = record;
        }
        else
        {
            services.Add(record);
        }

        WriteRegistry(registry with { Services = services.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray() });
    }

    private void UpdateRegistryCurrentVersion(string serviceId, string version)
    {
        var registry = ReadRegistry();
        var services = registry.Services
            .Select(item => item.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase)
                ? item with
                {
                    CurrentVersion = version,
                    ManifestPath = Path.Combine("services", serviceId, "versions", version, "service.manifest.json").Replace('\\', '/'),
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                : item)
            .ToArray();
        WriteRegistry(registry with { Services = services });
    }

    private void RemoveFromRegistry(string serviceId)
    {
        var registry = ReadRegistry();
        var services = registry.Services
            .Where(item => !item.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        WriteRegistry(registry with { Services = services });
    }

    private InstalledServicesRegistry ReadRegistry() =>
        AtomicJsonStore.Read<InstalledServicesRegistry>(RegistryPath) ?? new InstalledServicesRegistry();

    private void WriteRegistry(InstalledServicesRegistry registry) =>
        AtomicJsonStore.Write(RegistryPath, registry);

    private static void ValidateServiceId(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        if (serviceId.Contains(Path.DirectorySeparatorChar) ||
            serviceId.Contains(Path.AltDirectorySeparatorChar) ||
            serviceId.Contains("..", StringComparison.Ordinal))
        {
            throw new PackageException("service.invalidId", "Service id is invalid.");
        }
    }

    private static void DeleteServiceMetadataExceptData(string serviceRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(serviceRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.Equals("data", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("logs", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(directory);
            }
        }

        foreach (var file in Directory.EnumerateFiles(serviceRoot))
        {
            TryDeleteFile(file);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

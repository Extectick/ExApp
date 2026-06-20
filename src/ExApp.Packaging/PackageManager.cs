using ExApp.Packaging.Models;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ExApp.Packaging;

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

    public string RootDirectory => _options.RootDirectory;

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
            _validator.ValidateSignature(stagingDirectory);

            var serviceRoot = GetServiceRoot(manifest.Id);
            var versionDirectory = Path.Combine(serviceRoot, "versions", manifest.Version);
            var statePath = Path.Combine(serviceRoot, "package-state.json");
            var existingState = AtomicJsonStore.Read<PackageState>(statePath);
            var repairingCurrentVersion = false;

            if (Directory.Exists(versionDirectory))
            {
                if (existingState?.CurrentVersion == manifest.Version &&
                    IsInstalledVersionComplete(versionDirectory, manifest))
                {
                    CachePackage(packagePath, packageHash);
                    return new PackageInstallResult(manifest, existingState, versionDirectory, AlreadyInstalled: true);
                }

                if (existingState?.CurrentVersion == manifest.Version)
                {
                    TryDeleteDirectory(versionDirectory);
                    repairingCurrentVersion = true;
                }
                else
                {
                    throw new PackageException("package.versionExists", $"Service '{manifest.Id}' version '{manifest.Version}' is already installed.");
                }
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
                PreviousVersion = repairingCurrentVersion
                    ? existingState?.PreviousVersion
                    : existingState?.CurrentVersion,
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

    public async Task<ServiceManifest> InspectPackageManifestAsync(
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

        var stagingDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var packageHash = await _reader.ComputePackageHashAsync(packagePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedPackageSha256) &&
                !packageHash.Equals(expectedPackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("package.hashMismatch", "Package SHA-256 does not match the expected catalog hash.");
            }

            _reader.ExtractSecure(packagePath, stagingDirectory);
            var manifest = _reader.ReadManifest(stagingDirectory);
            _validator.ValidateManifest(manifest, stagingDirectory);
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<ServiceManifest> InspectDeltaPackageManifestAsync(
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

        var stagingDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var packageHash = await _reader.ComputePackageHashAsync(packagePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedPackageSha256) &&
                !packageHash.Equals(expectedPackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("package.hashMismatch", "Package SHA-256 does not match the expected catalog hash.");
            }

            _reader.ExtractSecure(packagePath, stagingDirectory);
            var manifest = _reader.ReadManifest(stagingDirectory);
            var delta = _reader.ReadDeltaManifest(stagingDirectory);
            ValidateDeltaMetadata(delta, manifest);
            ValidateDeltaPayload(delta, stagingDirectory);
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<PackageInstallResult> InstallDeltaAsync(
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
        var deltaStagingDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        var targetStagingDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageHash = await _reader.ComputePackageHashAsync(packagePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedPackageSha256) &&
                !packageHash.Equals(expectedPackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("package.hashMismatch", "Package SHA-256 does not match the expected catalog hash.");
            }

            _reader.ExtractSecure(packagePath, deltaStagingDirectory);
            var manifest = _reader.ReadManifest(deltaStagingDirectory);
            var targetChecksums = _reader.ReadChecksums(deltaStagingDirectory);
            var delta = _reader.ReadDeltaManifest(deltaStagingDirectory);
            ValidateDeltaMetadata(delta, manifest);
            ValidateDeltaPayload(delta, deltaStagingDirectory);

            var serviceRoot = GetServiceRoot(manifest.Id);
            var statePath = Path.Combine(serviceRoot, "package-state.json");
            var existingState = AtomicJsonStore.Read<PackageState>(statePath)
                ?? throw new PackageException("service.notInstalled", $"Service '{manifest.Id}' is not installed.");
            if (!existingState.CurrentVersion.Equals(delta.BaseVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException(
                    "delta.baseVersionMismatch",
                    $"Service '{manifest.Id}' is on version {existingState.CurrentVersion}, but delta requires {delta.BaseVersion}.");
            }

            var baseDirectory = Path.Combine(serviceRoot, "versions", delta.BaseVersion);
            if (!Directory.Exists(baseDirectory))
            {
                throw new PackageException("delta.baseVersionMissing", $"Base service version '{delta.BaseVersion}' is missing.");
            }

            var versionDirectory = Path.Combine(serviceRoot, "versions", manifest.Version);
            if (Directory.Exists(versionDirectory))
            {
                throw new PackageException("package.versionExists", $"Service '{manifest.Id}' version '{manifest.Version}' is already installed.");
            }

            var baseChecksums = _reader.ReadChecksums(baseDirectory);
            var buildStats = BuildDeltaTargetVersion(baseDirectory, deltaStagingDirectory, targetStagingDirectory, baseChecksums, targetChecksums, delta);
            _validator.ValidateManifest(manifest, targetStagingDirectory);
            _validator.ValidateChecksums(targetStagingDirectory, targetChecksums);
            _validator.ValidateSignature(targetStagingDirectory);

            Directory.CreateDirectory(Path.Combine(serviceRoot, "versions"));
            Directory.CreateDirectory(Path.Combine(serviceRoot, "data"));
            Directory.CreateDirectory(Path.Combine(serviceRoot, "logs"));
            Directory.Move(targetStagingDirectory, versionDirectory);

            var now = DateTimeOffset.UtcNow;
            var state = new PackageState
            {
                ServiceId = manifest.Id,
                CurrentVersion = manifest.Version,
                PreviousVersion = existingState.CurrentVersion,
                State = "installed",
                UpdatedAt = now
            };
            AtomicJsonStore.Write(statePath, state);
            UpdateRegistry(manifest, state, null);
            CachePackage(packagePath, packageHash);

            return new PackageInstallResult(manifest, state, versionDirectory, AlreadyInstalled: false)
            {
                AppliedDelta = true,
                CopiedFiles = buildStats.CopiedFiles,
                LinkedFiles = buildStats.LinkedFiles,
                DeletedFiles = buildStats.DeletedFiles
            };
        }
        finally
        {
            TryDeleteDirectory(deltaStagingDirectory);
            TryDeleteDirectory(targetStagingDirectory);
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

            await DeleteDirectoryWithRetryAsync(
                Path.Combine(serviceRoot, "versions"),
                cancellationToken);
            await DeleteFileWithRetryAsync(
                Path.Combine(serviceRoot, "package-state.json"),
                cancellationToken);

            if (deleteData)
            {
                await DeleteDirectoryWithRetryAsync(serviceRoot, cancellationToken);
            }
            else
            {
                await DeleteServiceMetadataExceptDataAsync(serviceRoot, cancellationToken);
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
        var extension = Path.GetExtension(packagePath);
        if (!extension.Equals(".svcpkg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".svcdelta", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".svcpkg";
        }

        var cachePath = Path.Combine(CacheRoot, $"{packageHash}{extension}");
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

    private static bool IsInstalledVersionComplete(string versionDirectory, ServiceManifest manifest)
    {
        var manifestPath = Path.Combine(versionDirectory, "service.manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var executablePath = Path.GetFullPath(Path.Combine(
            versionDirectory,
            manifest.Entry.Executable.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(executablePath);
    }

    private static void ValidateDeltaMetadata(ServiceDeltaManifest delta, ServiceManifest manifest)
    {
        if (delta.ManifestVersion != 1)
        {
            throw new PackageException("delta.unsupportedVersion", $"Service delta manifest version {delta.ManifestVersion} is not supported.");
        }

        if (!delta.ServiceId.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException("delta.serviceMismatch", "Service delta id does not match service.manifest.json.");
        }

        if (!delta.Version.Equals(manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException("delta.versionMismatch", "Service delta target version does not match service.manifest.json.");
        }

        if (string.IsNullOrWhiteSpace(delta.BaseVersion) ||
            !Version.TryParse(delta.BaseVersion, out _) ||
            !Version.TryParse(delta.Version, out _))
        {
            throw new PackageException("delta.invalidVersion", "Service delta has invalid version metadata.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in delta.Files)
        {
            var relativePath = NormalizePackagePath(file.Path);
            if (!paths.Add(relativePath))
            {
                throw new PackageException("delta.duplicatePath", $"Service delta path '{file.Path}' is duplicated.");
            }
        }

        foreach (var patch in delta.Patches)
        {
            var relativePath = NormalizePackagePath(patch.Path);
            if (!paths.Add(relativePath))
            {
                throw new PackageException("delta.duplicatePath", $"Service delta path '{patch.Path}' is duplicated.");
            }

            NormalizePackagePath(patch.DataPath);
            if (patch.BlockSize <= 0 ||
                patch.BaseSize < 0 ||
                patch.BaseSha256.Length != 64 ||
                !patch.BaseSha256.All(Uri.IsHexDigit) ||
                patch.TargetSize < 0 ||
                patch.TargetSha256.Length != 64 ||
                !patch.TargetSha256.All(Uri.IsHexDigit) ||
                patch.Operations.Count == 0)
            {
                throw new PackageException("delta.invalidPatch", $"Service delta patch '{patch.Path}' is invalid.");
            }

            foreach (var operation in patch.Operations)
            {
                if (operation.Type is not ("copy" or "data") ||
                    operation.Offset < 0 ||
                    operation.DataOffset < 0 ||
                    operation.Length <= 0)
                {
                    throw new PackageException("delta.invalidPatchOperation", $"Service delta patch operation for '{patch.Path}' is invalid.");
                }

                if (operation.Type.Equals("copy", StringComparison.OrdinalIgnoreCase) &&
                    !IsRangeWithin(operation.Offset, operation.Length, patch.BaseSize))
                {
                    throw new PackageException("delta.invalidPatchOperation", $"Service delta patch copy operation for '{patch.Path}' exceeds the base file.");
                }
            }

            var outputLength = patch.Operations.Sum(static operation => (long)operation.Length);
            if (outputLength != patch.TargetSize)
            {
                throw new PackageException("delta.invalidPatchOperation", $"Service delta patch '{patch.Path}' does not produce the target file size.");
            }
        }

        foreach (var path in delta.Delete)
        {
            NormalizePackagePath(path);
        }
    }

    private static void ValidateDeltaPayload(ServiceDeltaManifest delta, string deltaDirectory)
    {
        foreach (var patch in delta.Patches)
        {
            var dataPath = PackageValidator.ResolvePackagePath(deltaDirectory, patch.DataPath);
            if (!File.Exists(dataPath))
            {
                throw new PackageException("delta.patchDataMissing", $"Service delta patch data '{patch.DataPath}' is missing.");
            }

            var dataSize = new FileInfo(dataPath).Length;
            foreach (var operation in patch.Operations.Where(static operation => operation.Type.Equals("data", StringComparison.OrdinalIgnoreCase)))
            {
                if (!IsRangeWithin(operation.DataOffset, operation.Length, dataSize))
                {
                    throw new PackageException("delta.invalidPatchOperation", $"Service delta patch data operation for '{patch.Path}' exceeds '{patch.DataPath}'.");
                }
            }
        }
    }

    private static DeltaBuildStats BuildDeltaTargetVersion(
        string baseDirectory,
        string deltaDirectory,
        string targetDirectory,
        ChecksumManifest baseChecksums,
        ChecksumManifest targetChecksums,
        ServiceDeltaManifest delta)
    {
        var changedPaths = new HashSet<string>(
            delta.Files.Select(file => NormalizePackagePath(file.Path)),
            StringComparer.OrdinalIgnoreCase);
        var patchPaths = new HashSet<string>(
            delta.Patches.Select(file => NormalizePackagePath(file.Path)),
            StringComparer.OrdinalIgnoreCase);
        var deletedPaths = new HashSet<string>(
            delta.Delete.Select(NormalizePackagePath),
            StringComparer.OrdinalIgnoreCase);
        var baseFiles = baseChecksums.Files.ToDictionary(
            entry => NormalizePackagePath(entry.Path),
            StringComparer.OrdinalIgnoreCase);
        var targetFiles = targetChecksums.Files.ToDictionary(
            entry => NormalizePackagePath(entry.Path),
            StringComparer.OrdinalIgnoreCase);

        foreach (var changedPath in changedPaths)
        {
            if (!targetFiles.ContainsKey(changedPath))
            {
                throw new PackageException("delta.changedFileNotInTarget", $"Service delta changed file '{changedPath}' is not in target checksums.");
            }
        }

        foreach (var patchPath in patchPaths)
        {
            if (!targetFiles.TryGetValue(patchPath, out var targetFile))
            {
                throw new PackageException("delta.patchFileNotInTarget", $"Service delta patch file '{patchPath}' is not in target checksums.");
            }

            var patch = delta.Patches.Single(item => NormalizePackagePath(item.Path).Equals(patchPath, StringComparison.OrdinalIgnoreCase));
            if (!targetFile.Sha256.Equals(patch.TargetSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("delta.patchHashMismatch", $"Service delta patch file '{patchPath}' target hash does not match checksums.");
            }

            if (!baseFiles.TryGetValue(patchPath, out var baseFile) ||
                !baseFile.Sha256.Equals(patch.BaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("delta.patchBaseMismatch", $"Service delta patch file '{patchPath}' base hash does not match checksums.");
            }
        }

        foreach (var deletedPath in deletedPaths)
        {
            if (targetFiles.ContainsKey(deletedPath))
            {
                throw new PackageException("delta.deletedFileInTarget", $"Service delta delete path '{deletedPath}' is still in target checksums.");
            }
        }

        Directory.CreateDirectory(targetDirectory);
        var copied = 0;
        var linked = 0;
        foreach (var targetPath in targetFiles.Keys.Order(StringComparer.Ordinal))
        {
            var destination = PackageValidator.ResolvePackagePath(targetDirectory, targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (patchPaths.Contains(targetPath))
            {
                var patch = delta.Patches.Single(item => NormalizePackagePath(item.Path).Equals(targetPath, StringComparison.OrdinalIgnoreCase));
                ApplyPatch(baseDirectory, deltaDirectory, patch, destination);
                var patchedHash = ComputeSha256(destination);
                if (!patchedHash.Equals(patch.TargetSha256, StringComparison.OrdinalIgnoreCase) ||
                    new FileInfo(destination).Length != patch.TargetSize)
                {
                    throw new PackageException("delta.patchValidationFailed", $"Patched service file '{targetPath}' failed validation.");
                }

                copied++;
                continue;
            }

            if (changedPaths.Contains(targetPath) ||
                !baseFiles.TryGetValue(targetPath, out var baseFile) ||
                !baseFile.Sha256.Equals(targetFiles[targetPath].Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var source = PackageValidator.ResolvePackagePath(deltaDirectory, targetPath);
                if (!File.Exists(source))
                {
                    throw new PackageException("delta.changedFileMissing", $"Service delta changed file '{targetPath}' is missing.");
                }

                File.Copy(source, destination);
                copied++;
                continue;
            }

            var baseSource = PackageValidator.ResolvePackagePath(baseDirectory, targetPath);
            if (!File.Exists(baseSource))
            {
                throw new PackageException("delta.baseFileMissing", $"Base service file '{targetPath}' is missing.");
            }

            if (TryCreateHardLink(destination, baseSource))
            {
                linked++;
            }
            else
            {
                File.Copy(baseSource, destination);
                copied++;
            }
        }

        var signatureSource = Path.Combine(deltaDirectory, "signature.sig");
        if (!File.Exists(signatureSource))
        {
            throw new PackageException("signature.missing", "signature.sig is missing.");
        }

        var checksumsSource = Path.Combine(deltaDirectory, "checksums.json");
        if (!File.Exists(checksumsSource))
        {
            throw new PackageException("checksums.missing", "checksums.json is missing.");
        }

        File.Copy(checksumsSource, Path.Combine(targetDirectory, "checksums.json"));
        File.Copy(signatureSource, Path.Combine(targetDirectory, "signature.sig"));
        return new DeltaBuildStats(copied, linked, deletedPaths.Count);
    }

    private static void ApplyPatch(string baseDirectory, string deltaDirectory, FilePatchEntry patch, string destination)
    {
        var basePath = PackageValidator.ResolvePackagePath(baseDirectory, patch.Path);
        var dataPath = PackageValidator.ResolvePackagePath(deltaDirectory, patch.DataPath);
        if (!File.Exists(basePath))
        {
            throw new PackageException("delta.baseFileMissing", $"Base service file '{patch.Path}' is missing.");
        }

        if (!File.Exists(dataPath))
        {
            throw new PackageException("delta.patchDataMissing", $"Service delta patch data '{patch.DataPath}' is missing.");
        }

        if (new FileInfo(basePath).Length != patch.BaseSize)
        {
            throw new PackageException("delta.baseSizeMismatch", $"Base service file '{patch.Path}' has an invalid size.");
        }

        if (!ComputeSha256(basePath).Equals(patch.BaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException("delta.baseHashMismatch", $"Base service file '{patch.Path}' has an invalid SHA-256.");
        }

        using var baseStream = File.Open(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dataStream = File.Open(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = File.Create(destination);
        var buffer = new byte[81920];
        foreach (var operation in patch.Operations)
        {
            Stream source;
            long offset;
            if (operation.Type.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                source = baseStream;
                offset = operation.Offset;
            }
            else if (operation.Type.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                source = dataStream;
                offset = operation.DataOffset;
            }
            else
            {
                throw new PackageException("delta.invalidPatchOperation", $"Unsupported patch operation '{operation.Type}'.");
            }

            source.Seek(offset, SeekOrigin.Begin);
            if (!IsRangeWithin(offset, operation.Length, source.Length))
            {
                throw new PackageException("delta.patchUnexpectedEnd", $"Patch operation for '{patch.Path}' exceeded source length.");
            }

            var remaining = operation.Length;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new PackageException("delta.patchUnexpectedEnd", $"Patch operation for '{patch.Path}' exceeded source length.");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsRangeWithin(long offset, int length, long sourceSize) =>
        offset >= 0 &&
        length > 0 &&
        sourceSize >= 0 &&
        offset <= sourceSize - length;

    private static string NormalizePackagePath(string path)
    {
        var normalized = PackageValidator.NormalizeRelativePath(path);
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':', StringComparison.Ordinal) ||
            normalized.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
        {
            throw new PackageException("delta.invalidPath", $"Service delta path '{path}' is invalid.");
        }

        return normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static bool TryCreateHardLink(string destination, string source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return CreateHardLink(destination, source, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private sealed record DeltaBuildStats(int CopiedFiles, int LinkedFiles, int DeletedFiles);

    private static async Task DeleteServiceMetadataExceptDataAsync(
        string serviceRoot,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(serviceRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.Equals("data", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("logs", StringComparison.OrdinalIgnoreCase))
            {
                await DeleteDirectoryWithRetryAsync(directory, cancellationToken);
            }
        }

        foreach (var file in Directory.EnumerateFiles(serviceRoot))
        {
            await DeleteFileWithRetryAsync(file, cancellationToken);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await RetryDeleteAsync(
            path,
            Directory.Exists,
            () => Directory.Delete(path, recursive: true),
            cancellationToken);
    }

    private static async Task DeleteFileWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await RetryDeleteAsync(
            path,
            File.Exists,
            () => File.Delete(path),
            cancellationToken);
    }

    private static async Task RetryDeleteAsync(
        string path,
        Func<string, bool> exists,
        Action delete,
        CancellationToken cancellationToken)
    {
        const int attempts = 12;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!exists(path))
            {
                return;
            }

            try
            {
                delete();
                return;
            }
            catch (Exception exception) when (
                attempt < attempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
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

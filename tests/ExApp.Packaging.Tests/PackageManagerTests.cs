using ExApp.Packaging.Models;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExApp.Packaging.Tests;

public sealed class PackageManagerTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "ExApp.PackageTests", Guid.NewGuid().ToString("N"));
    private readonly TestPackageBuilder _packages;

    public PackageManagerTests()
    {
        Directory.CreateDirectory(_temporaryRoot);
        _packages = new TestPackageBuilder(_temporaryRoot);
    }

    [Fact]
    public async Task InstallAsync_ValidPackage_InstallsAndRegistersService()
    {
        var manager = CreateManager();
        var result = await manager.InstallAsync(_packages.Create());

        Assert.False(result.AlreadyInstalled);
        Assert.Equal("test-service", result.Manifest.Id);
        Assert.Equal("1.0.0", result.State.CurrentVersion);
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "service.manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "bin", "Test.Service.exe")));
        Assert.Equal(result.InstallDirectory, manager.ResolveCurrentVersionDirectory("test-service"));

        var registered = Assert.Single(manager.GetInstalledServices());
        Assert.Equal("test-service", registered.Id);
        Assert.Equal("1.0.0", registered.CurrentVersion);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_temporaryRoot, "runtime", "packages", "cache"), "*.svcpkg"));
    }

    [Fact]
    public async Task InstallAsync_SameVersion_IsIdempotent()
    {
        var manager = CreateManager();
        var package = _packages.Create();
        await manager.InstallAsync(package);

        var secondResult = await manager.InstallAsync(package);

        Assert.True(secondResult.AlreadyInstalled);
        Assert.Equal("1.0.0", secondResult.State.CurrentVersion);
    }

    [Fact]
    public async Task InstallAsync_DamagedCurrentVersion_ReinstallsPackage()
    {
        var manager = CreateManager();
        var package = _packages.Create();
        var firstResult = await manager.InstallAsync(package);
        File.Delete(Path.Combine(firstResult.InstallDirectory, "service.manifest.json"));

        var repaired = await manager.InstallAsync(package);

        Assert.False(repaired.AlreadyInstalled);
        Assert.True(File.Exists(Path.Combine(repaired.InstallDirectory, "service.manifest.json")));
        Assert.True(File.Exists(Path.Combine(repaired.InstallDirectory, "bin", "Test.Service.exe")));
        Assert.Equal("1.0.0", manager.GetState("test-service")?.CurrentVersion);
        Assert.Null(manager.GetState("test-service")?.PreviousVersion);
    }

    [Fact]
    public async Task InstallAsync_InvalidManifest_RejectsPackage()
    {
        var manager = CreateManager();

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create(id: "Invalid Service")));

        Assert.Equal("manifest.invalidId", exception.Code);
        Assert.Empty(manager.GetInstalledServices());
    }

    [Fact]
    public async Task InstallAsync_CorruptedPayload_RejectsPackage()
    {
        var manager = CreateManager();

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create(corruptChecksum: true)));

        Assert.Equal("checksums.mismatch", exception.Code);
    }

    [Fact]
    public async Task InstallAsync_WrongExpectedPackageHash_RejectsPackage()
    {
        var manager = CreateManager();

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create(), new string('0', 64)));

        Assert.Equal("package.hashMismatch", exception.Code);
    }

    [Fact]
    public async Task InstallAsync_SignedPackageWithConfiguredKey_Installs()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manager = CreateManager(signingKey.ExportSubjectPublicKeyInfoPem());

        var result = await manager.InstallAsync(_packages.Create(signingKey: signingKey));

        Assert.Equal("test-service", result.Manifest.Id);
        Assert.Equal("1.0.0", result.State.CurrentVersion);
    }

    [Fact]
    public async Task InstallAsync_UnsignedPackageWithConfiguredKey_RejectsPackage()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manager = CreateManager(signingKey.ExportSubjectPublicKeyInfoPem());

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create()));

        Assert.Equal("signature.unsigned", exception.Code);
    }

    [Fact]
    public async Task InstallAsync_SignedPackageWithWrongConfiguredKey_RejectsPackage()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manager = CreateManager(wrongKey.ExportSubjectPublicKeyInfoPem());

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create(signingKey: signingKey)));

        Assert.Equal("signature.verificationFailed", exception.Code);
    }

    [Fact]
    public async Task InstallAsync_IncompatibleAppVersion_RejectsPackage()
    {
        var manager = CreateManager();

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create(minAppVersion: "9.0.0")));

        Assert.Equal("manifest.incompatibleAppVersion", exception.Code);
    }

    [Fact]
    public async Task InstallAsync_UnsupportedApiVersion_RejectsPackage()
    {
        var manager = CreateManager();

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(_packages.Create(apiVersion: 2)));

        Assert.Equal("manifest.unsupportedApiVersion", exception.Code);
    }

    [Fact]
    public async Task InstallAsync_PathTraversalEntry_RejectsPackageAndCleansStaging()
    {
        var manager = CreateManager();
        var packagePath = Path.Combine(_temporaryRoot, "path-traversal.svcpkg");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
            writer.Write("escape");
        }

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallAsync(packagePath));

        Assert.Equal("package.pathTraversal", exception.Code);
        Assert.False(File.Exists(Path.Combine(_temporaryRoot, "runtime", "packages", "escaped.txt")));
        var stagingRoot = Path.Combine(_temporaryRoot, "runtime", "packages", "staging");
        Assert.False(Directory.Exists(stagingRoot) && Directory.EnumerateFileSystemEntries(stagingRoot).Any());
    }

    [Fact]
    public async Task RollbackAsync_AfterUpdate_SwitchesToPreviousVersion()
    {
        var manager = CreateManager();
        await manager.InstallAsync(_packages.Create(version: "1.0.0"));
        await manager.InstallAsync(_packages.Create(version: "1.1.0"));

        var state = await manager.RollbackAsync("test-service");

        Assert.Equal("1.0.0", state.CurrentVersion);
        Assert.Equal("1.1.0", state.PreviousVersion);
        Assert.EndsWith(Path.Combine("versions", "1.0.0"), manager.ResolveCurrentVersionDirectory("test-service"));
        Assert.Equal("1.0.0", Assert.Single(manager.GetInstalledServices()).CurrentVersion);
    }

    [Fact]
    public async Task InstallDeltaAsync_ValidDelta_BuildsTargetVersionFromCurrentVersion()
    {
        var manager = CreateManager();
        var basePackage = _packages.Create(version: "1.0.0");
        var targetPackage = _packages.Create(version: "1.1.0");
        await manager.InstallAsync(basePackage);

        var deltaPackage = _packages.CreateDelta(basePackage, targetPackage);
        var result = await manager.InstallDeltaAsync(deltaPackage);

        Assert.True(result.AppliedDelta);
        Assert.Equal("1.1.0", result.State.CurrentVersion);
        Assert.Equal("1.0.0", result.State.PreviousVersion);
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "bin", "Test.Service.exe")));
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "bin", "shared.dat")));
        Assert.Equal("payload-1.1.0", File.ReadAllText(Path.Combine(result.InstallDirectory, "bin", "Test.Service.exe")));
        Assert.Equal("shared-payload", File.ReadAllText(Path.Combine(result.InstallDirectory, "bin", "shared.dat")));
        Assert.Equal("1.1.0", manager.GetState("test-service")?.CurrentVersion);
    }

    [Fact]
    public async Task InstallDeltaAsync_PatchDelta_BuildsTargetFileFromBaseChunks()
    {
        var manager = CreateManager();
        var basePackage = _packages.Create(version: "1.0.0", largePayloadMarker: "base-marker");
        var targetPackage = _packages.Create(version: "1.1.0", largePayloadMarker: "target-marker");
        await manager.InstallAsync(basePackage);

        var deltaPackage = _packages.CreateDeltaWithPatch(basePackage, targetPackage);
        var result = await manager.InstallDeltaAsync(deltaPackage);

        Assert.True(result.AppliedDelta);
        Assert.Equal("1.1.0", result.State.CurrentVersion);
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "bin", "large.dat")));

        using var targetArchive = ZipFile.OpenRead(targetPackage);
        var targetEntry = targetArchive.GetEntry("bin/large.dat")!;
        using var expected = new MemoryStream();
        await targetEntry.Open().CopyToAsync(expected);
        var installedBytes = await File.ReadAllBytesAsync(Path.Combine(result.InstallDirectory, "bin", "large.dat"));
        Assert.Equal(expected.ToArray(), installedBytes);
    }

    [Fact]
    public async Task InstallDeltaAsync_PatchDataOutsideBounds_RejectsDelta()
    {
        var manager = CreateManager();
        var basePackage = _packages.Create(version: "1.0.0", largePayloadMarker: "base-marker");
        var targetPackage = _packages.Create(version: "1.1.0", largePayloadMarker: "target-marker");
        await manager.InstallAsync(basePackage);

        var deltaPackage = RewriteDeltaManifest(
            _packages.CreateDeltaWithPatch(basePackage, targetPackage),
            manifest => manifest["patches"]![0]!["operations"]![1]!["dataOffset"] = 10_000_000);

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallDeltaAsync(deltaPackage));

        Assert.Equal("delta.invalidPatchOperation", exception.Code);
        Assert.Equal("1.0.0", manager.GetState("test-service")?.CurrentVersion);
    }

    [Fact]
    public async Task InstallDeltaAsync_PatchTargetSizeMismatch_RejectsDelta()
    {
        var manager = CreateManager();
        var basePackage = _packages.Create(version: "1.0.0", largePayloadMarker: "base-marker");
        var targetPackage = _packages.Create(version: "1.1.0", largePayloadMarker: "target-marker");
        await manager.InstallAsync(basePackage);

        var deltaPackage = RewriteDeltaManifest(
            _packages.CreateDeltaWithPatch(basePackage, targetPackage),
            manifest => manifest["patches"]![0]!["targetSize"] = 1);

        var exception = await Assert.ThrowsAsync<PackageException>(
            () => manager.InstallDeltaAsync(deltaPackage));

        Assert.Equal("delta.invalidPatchOperation", exception.Code);
        Assert.Equal("1.0.0", manager.GetState("test-service")?.CurrentVersion);
    }

    [Fact]
    public async Task UninstallAsync_PreserveData_KeepsDataAndRemovesBinaries()
    {
        var manager = CreateManager();
        await manager.InstallAsync(_packages.Create());
        var serviceRoot = Path.Combine(_temporaryRoot, "runtime", "services", "test-service");
        var dataFile = Path.Combine(serviceRoot, "data", "settings.json");
        File.WriteAllText(dataFile, "{}");

        await manager.UninstallAsync("test-service", deleteData: false);

        Assert.True(File.Exists(dataFile));
        Assert.False(Directory.Exists(Path.Combine(serviceRoot, "versions")));
        Assert.Null(manager.GetState("test-service"));
        Assert.Empty(manager.GetInstalledServices());
    }

    [Fact]
    public async Task UninstallAsync_DeleteData_RemovesServiceDirectory()
    {
        var manager = CreateManager();
        await manager.InstallAsync(_packages.Create());
        var serviceRoot = Path.Combine(_temporaryRoot, "runtime", "services", "test-service");
        File.WriteAllText(Path.Combine(serviceRoot, "data", "settings.json"), "{}");

        await manager.UninstallAsync("test-service", deleteData: true);

        Assert.False(Directory.Exists(serviceRoot));
        Assert.Empty(manager.GetInstalledServices());
    }

    [Fact]
    public async Task UninstallAsync_TemporarilyLockedBinary_RetriesAndSucceeds()
    {
        var manager = CreateManager();
        var installed = await manager.InstallAsync(_packages.Create());
        var executable = Path.Combine(installed.InstallDirectory, "bin", "Test.Service.exe");

        var lockStream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var uninstallTask = manager.UninstallAsync("test-service", deleteData: true);
            await Task.Delay(350);
            await lockStream.DisposeAsync();
            lockStream = null!;
            await uninstallTask;
        }
        finally
        {
            if (lockStream is not null)
            {
                await lockStream.DisposeAsync();
            }
        }

        Assert.False(Directory.Exists(installed.InstallDirectory));
        Assert.Empty(manager.GetInstalledServices());
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }

    private PackageManager CreateManager(string? servicePackageSigningPublicKeyPem = null) => new(new PackageManagerOptions
    {
        RootDirectory = Path.Combine(_temporaryRoot, "runtime"),
        AppVersion = "1.0.0",
        AgentVersion = "1.0.0",
        Architecture = "x64",
        ServicePackageSigningPublicKeyPem = servicePackageSigningPublicKeyPem
    });

    private string RewriteDeltaManifest(string packagePath, Action<JsonNode> mutate)
    {
        var workDirectory = Path.Combine(_temporaryRoot, $"rewrite-delta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        ZipFile.ExtractToDirectory(packagePath, workDirectory);

        var manifestPath = Path.Combine(workDirectory, "service-delta.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("service-delta.json is empty.");
        mutate(manifest);
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var rewrittenPackagePath = Path.Combine(_temporaryRoot, $"rewritten-delta-{Guid.NewGuid():N}.svcdelta");
        ZipFile.CreateFromDirectory(workDirectory, rewrittenPackagePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(workDirectory, recursive: true);
        return rewrittenPackagePath;
    }
}

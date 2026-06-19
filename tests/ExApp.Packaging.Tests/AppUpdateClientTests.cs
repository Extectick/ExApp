using System.Security.Cryptography;
using System.Text.Json;
using ExApp.Core.Updates;

namespace ExApp.Packaging.Tests;

public sealed class AppUpdateClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ExApp.UpdateTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task LocalManifestCanBeCheckedAndDownloaded()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "exapp-0.2.0-win-x64.zip");
        await File.WriteAllTextAsync(packagePath, "test-package");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath)))
            .ToLowerInvariant();
        var manifestPath = Path.Combine(_root, "exapp-update.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new AppReleaseManifest
            {
                ManifestVersion = 1,
                Version = "0.2.0",
                Channel = "stable",
                PublishedAt = DateTimeOffset.UtcNow,
                Package = new AppReleasePackage
                {
                    Url = packagePath,
                    Sha256 = hash,
                    Size = new FileInfo(packagePath).Length
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var previousSource = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL");
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", manifestPath);
        try
        {
            using var client = new AppUpdateClient();
            var result = await client.CheckAsync("0.1.0", "stable");
            var download = await client.DownloadAsync(result.Release!, "0.1.0", Path.Combine(_root, "downloads"));

            Assert.True(result.IsUpdateAvailable);
            Assert.Equal("0.2.0", result.Release!.Version);
            Assert.True(File.Exists(download));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download))).ToLowerInvariant());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", previousSource);
        }
    }

    [Fact]
    public async Task DownloadAsync_UsesDeltaWhenBaseVersionMatches()
    {
        Directory.CreateDirectory(_root);
        var fullPackagePath = Path.Combine(_root, "exapp-0.2.0-win-x64.zip");
        var deltaPackagePath = Path.Combine(_root, "exapp-delta-0.1.0-to-0.2.0-win-x64.zip");
        await File.WriteAllTextAsync(fullPackagePath, "full-package");
        await File.WriteAllTextAsync(deltaPackagePath, "delta-package");
        var fullHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPackagePath)))
            .ToLowerInvariant();
        var deltaHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(deltaPackagePath)))
            .ToLowerInvariant();

        using var client = new AppUpdateClient();
        var manifest = new AppReleaseManifest
        {
            ManifestVersion = 1,
            Version = "0.2.0",
            Channel = "stable",
            PublishedAt = DateTimeOffset.UtcNow,
            Package = new AppReleasePackage
            {
                Url = fullPackagePath,
                Sha256 = fullHash,
                Size = new FileInfo(fullPackagePath).Length
            },
            Delta = new AppDeltaPackage
            {
                BaseVersion = "0.1.0",
                Url = deltaPackagePath,
                Sha256 = deltaHash,
                Size = new FileInfo(deltaPackagePath).Length,
                ChangedFiles = 1,
                DeletedFiles = 0
            }
        };

        var download = await client.DownloadAsync(manifest, "0.1.0", Path.Combine(_root, "downloads"));

        Assert.EndsWith("exapp-delta-0.1.0-to-0.2.0-win-x64.zip", download);
        Assert.Equal(deltaHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download))).ToLowerInvariant());
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToFullPackageWhenDeltaBaseDoesNotMatch()
    {
        Directory.CreateDirectory(_root);
        var fullPackagePath = Path.Combine(_root, "exapp-0.2.0-win-x64.zip");
        var deltaPackagePath = Path.Combine(_root, "exapp-delta-0.1.0-to-0.2.0-win-x64.zip");
        await File.WriteAllTextAsync(fullPackagePath, "full-package");
        await File.WriteAllTextAsync(deltaPackagePath, "delta-package");
        var fullHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPackagePath)))
            .ToLowerInvariant();
        var deltaHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(deltaPackagePath)))
            .ToLowerInvariant();

        using var client = new AppUpdateClient();
        var manifest = new AppReleaseManifest
        {
            ManifestVersion = 1,
            Version = "0.2.0",
            Channel = "stable",
            PublishedAt = DateTimeOffset.UtcNow,
            Package = new AppReleasePackage
            {
                Url = fullPackagePath,
                Sha256 = fullHash,
                Size = new FileInfo(fullPackagePath).Length
            },
            Delta = new AppDeltaPackage
            {
                BaseVersion = "0.1.0",
                Url = deltaPackagePath,
                Sha256 = deltaHash,
                Size = new FileInfo(deltaPackagePath).Length,
                ChangedFiles = 1,
                DeletedFiles = 0
            }
        };

        var download = await client.DownloadAsync(manifest, "0.1.5", Path.Combine(_root, "downloads"));

        Assert.EndsWith("exapp-0.2.0-win-x64.zip", download);
        Assert.Equal(fullHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download))).ToLowerInvariant());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

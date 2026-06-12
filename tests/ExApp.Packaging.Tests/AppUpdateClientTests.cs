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
            var download = await client.DownloadAsync(result.Release!, Path.Combine(_root, "downloads"));

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

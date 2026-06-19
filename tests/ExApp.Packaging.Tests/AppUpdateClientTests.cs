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

    [Fact]
    public async Task DownloadAsync_UsesSmallestMatchingDeltaFromDeltas()
    {
        Directory.CreateDirectory(_root);
        var fullPackagePath = Path.Combine(_root, "exapp-0.3.0-win-x64.zip");
        var largerDeltaPath = Path.Combine(_root, "exapp-delta-0.2.0-to-0.3.0-win-x64.zip");
        var smallerDeltaPath = Path.Combine(_root, "exapp-delta-0.2.0-to-0.3.0-win-x64-small.zip");
        await File.WriteAllTextAsync(fullPackagePath, "full-package");
        await File.WriteAllTextAsync(largerDeltaPath, "larger-delta-package");
        await File.WriteAllTextAsync(smallerDeltaPath, "small");
        var fullHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPackagePath)))
            .ToLowerInvariant();
        var largerDeltaHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(largerDeltaPath)))
            .ToLowerInvariant();
        var smallerDeltaHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(smallerDeltaPath)))
            .ToLowerInvariant();

        using var client = new AppUpdateClient();
        var manifest = new AppReleaseManifest
        {
            ManifestVersion = 1,
            Version = "0.3.0",
            Channel = "stable",
            PublishedAt = DateTimeOffset.UtcNow,
            Package = new AppReleasePackage
            {
                Url = fullPackagePath,
                Sha256 = fullHash,
                Size = new FileInfo(fullPackagePath).Length
            },
            Deltas =
            [
                new AppDeltaPackage
                {
                    BaseVersion = "0.2.0",
                    Url = largerDeltaPath,
                    Sha256 = largerDeltaHash,
                    Size = new FileInfo(largerDeltaPath).Length,
                    ChangedFiles = 2,
                    DeletedFiles = 0
                },
                new AppDeltaPackage
                {
                    BaseVersion = "0.2.0",
                    Url = smallerDeltaPath,
                    Sha256 = smallerDeltaHash,
                    Size = new FileInfo(smallerDeltaPath).Length,
                    ChangedFiles = 1,
                    DeletedFiles = 0
                }
            ]
        };

        var download = await client.DownloadAsync(manifest, "0.2.0", Path.Combine(_root, "downloads"));

        Assert.EndsWith("exapp-delta-0.2.0-to-0.3.0-win-x64-small.zip", download);
        Assert.Equal(smallerDeltaHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download))).ToLowerInvariant());
    }

    [Fact]
    public async Task DownloadPackageAsync_CanForceFullPackageWhenDeltaMatches()
    {
        Directory.CreateDirectory(_root);
        var fullPackagePath = Path.Combine(_root, "exapp-0.3.1-win-x64.zip");
        var deltaPackagePath = Path.Combine(_root, "exapp-delta-0.3.0-to-0.3.1-win-x64.zip");
        await File.WriteAllTextAsync(fullPackagePath, "full-package-for-fallback");
        await File.WriteAllTextAsync(deltaPackagePath, "delta-package-for-fallback");
        var fullHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPackagePath)))
            .ToLowerInvariant();
        var deltaHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(deltaPackagePath)))
            .ToLowerInvariant();

        using var client = new AppUpdateClient();
        var manifest = new AppReleaseManifest
        {
            ManifestVersion = 1,
            Version = "0.3.1",
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
                BaseVersion = "0.3.0",
                Url = deltaPackagePath,
                Sha256 = deltaHash,
                Size = new FileInfo(deltaPackagePath).Length,
                ChangedFiles = 1,
                DeletedFiles = 0
            }
        };

        var download = await client.DownloadPackageAsync(
            manifest,
            "0.3.0",
            Path.Combine(_root, "downloads"),
            preferDelta: false);

        Assert.False(download.IsDelta);
        Assert.EndsWith("exapp-0.3.1-win-x64.zip", download.PackagePath);
        Assert.Equal(fullHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download.PackagePath))).ToLowerInvariant());
    }

    [Fact]
    public async Task CheckAsync_AcceptsSignedManifest()
    {
        Directory.CreateDirectory(_root);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var packagePath = Path.Combine(_root, "exapp-0.4.0-win-x64.zip");
        await File.WriteAllTextAsync(packagePath, "signed-package");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath)))
            .ToLowerInvariant();
        var manifestPath = Path.Combine(_root, "exapp-update.json");
        await File.WriteAllTextAsync(
            manifestPath,
            CreateSignedManifestJson(
                ecdsa,
                new AppReleaseManifest
                {
                    ManifestVersion = 1,
                    Version = "0.4.0",
                    Channel = "stable",
                    PublishedAt = DateTimeOffset.UtcNow,
                    Package = new AppReleasePackage
                    {
                        Url = packagePath,
                        Sha256 = hash,
                        Size = new FileInfo(packagePath).Length
                    }
                }));

        var previousSource = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL");
        var previousPublicKey = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM");
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", manifestPath);
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM", ecdsa.ExportSubjectPublicKeyInfoPem());
        try
        {
            using var client = new AppUpdateClient();
            var result = await client.CheckAsync("0.3.0", "stable");

            Assert.True(result.IsUpdateAvailable);
            Assert.Equal("0.4.0", result.Release!.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", previousSource);
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM", previousPublicKey);
        }
    }

    [Fact]
    public async Task CheckAsync_AcceptsSignedManifestWithBase64PublicKeyEnvironment()
    {
        Directory.CreateDirectory(_root);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var packagePath = Path.Combine(_root, "exapp-0.4.1-win-x64.zip");
        await File.WriteAllTextAsync(packagePath, "signed-package-base64-key");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath)))
            .ToLowerInvariant();
        var manifestPath = Path.Combine(_root, "exapp-update-base64-key.json");
        await File.WriteAllTextAsync(
            manifestPath,
            CreateSignedManifestJson(
                ecdsa,
                new AppReleaseManifest
                {
                    ManifestVersion = 1,
                    Version = "0.4.1",
                    Channel = "stable",
                    PublishedAt = DateTimeOffset.UtcNow,
                    Package = new AppReleasePackage
                    {
                        Url = packagePath,
                        Sha256 = hash,
                        Size = new FileInfo(packagePath).Length
                    }
                }));

        var previousSource = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL");
        var previousPublicKey = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM");
        var previousPublicKeyBase64 = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_BASE64");
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", manifestPath);
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM", null);
        Environment.SetEnvironmentVariable(
            "EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_BASE64",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ecdsa.ExportSubjectPublicKeyInfoPem())));
        try
        {
            using var client = new AppUpdateClient();
            var result = await client.CheckAsync("0.4.0", "stable");

            Assert.True(result.IsUpdateAvailable);
            Assert.Equal("0.4.1", result.Release!.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", previousSource);
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM", previousPublicKey);
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_BASE64", previousPublicKeyBase64);
        }
    }

    [Fact]
    public async Task CheckAsync_RejectsTamperedSignedManifest()
    {
        Directory.CreateDirectory(_root);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var packagePath = Path.Combine(_root, "exapp-0.4.0-win-x64.zip");
        await File.WriteAllTextAsync(packagePath, "signed-package");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath)))
            .ToLowerInvariant();
        var manifestPath = Path.Combine(_root, "exapp-update.json");
        var signedJson = CreateSignedManifestJson(
            ecdsa,
            new AppReleaseManifest
            {
                ManifestVersion = 1,
                Version = "0.4.0",
                Channel = "stable",
                PublishedAt = DateTimeOffset.UtcNow,
                Package = new AppReleasePackage
                {
                    Url = packagePath,
                    Sha256 = hash,
                    Size = new FileInfo(packagePath).Length
                }
            });
        await File.WriteAllTextAsync(manifestPath, signedJson.Replace("\"0.4.0\"", "\"9.9.9\"", StringComparison.Ordinal));

        var previousSource = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL");
        var previousPublicKey = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM");
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", manifestPath);
        Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM", ecdsa.ExportSubjectPublicKeyInfoPem());
        try
        {
            using var client = new AppUpdateClient();

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckAsync("0.3.0", "stable"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL", previousSource);
            Environment.SetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM", previousPublicKey);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreateSignedManifestJson(ECDsa ecdsa, AppReleaseManifest manifest)
    {
        var unsignedJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(unsignedJson);
        var canonicalPayload = AppUpdateSignatureVerifier.CanonicalizeWithoutSignature(document.RootElement);
        var signature = ecdsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(canonicalPayload),
            HashAlgorithmName.SHA256);
        var signedManifest = manifest with
        {
            Signature = new AppManifestSignature
            {
                Algorithm = "ecdsa-p256-sha256",
                KeyId = "test-key",
                Value = Convert.ToBase64String(signature)
            }
        };

        return JsonSerializer.Serialize(signedManifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

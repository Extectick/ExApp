using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExApp.Packaging.Tests;

internal sealed class TestPackageBuilder(string rootDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Create(
        string version = "1.0.0",
        string id = "test-service",
        string minAppVersion = "0.1.0",
        int apiVersion = 1,
        bool corruptChecksum = false,
        string? largePayloadMarker = null,
        ECDsa? signingKey = null)
    {
        var sourceDirectory = Path.Combine(rootDirectory, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "bin"));
        File.WriteAllText(Path.Combine(sourceDirectory, "bin", "Test.Service.exe"), $"payload-{version}");
        File.WriteAllText(Path.Combine(sourceDirectory, "bin", "shared.dat"), "shared-payload");
        if (largePayloadMarker is not null)
        {
            var largePayload = new byte[196608];
            Array.Fill<byte>(largePayload, 65, 0, 65536);
            Array.Fill<byte>(largePayload, 66, 65536, 65536);
            Array.Fill<byte>(largePayload, 67, 131072, 65536);
            var marker = System.Text.Encoding.UTF8.GetBytes(largePayloadMarker);
            Array.Copy(marker, 0, largePayload, 65536, marker.Length);
            File.WriteAllBytes(Path.Combine(sourceDirectory, "bin", "large.dat"), largePayload);
        }

        var manifest = new
        {
            manifestVersion = 1,
            id,
            name = "Test Service",
            description = "Package Manager test service.",
            version,
            publisher = new { id = "tests", name = "Tests" },
            category = "development",
            platform = "windows",
            architecture = "x64",
            apiVersion,
            minAppVersion,
            minAgentVersion = "0.1.0",
            entry = new
            {
                type = "process",
                executable = "bin/Test.Service.exe",
                arguments = Array.Empty<string>()
            },
            permissions = new[] { "background" },
            requiresAdmin = false,
            dataPolicy = new { preserveOnUninstall = true }
        };
        File.WriteAllText(
            Path.Combine(sourceDirectory, "service.manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                path = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/'),
                sha256 = corruptChecksum
                    ? new string('0', 64)
                    : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            })
            .OrderBy(item => item.path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(
            Path.Combine(sourceDirectory, "checksums.json"),
            JsonSerializer.Serialize(new { algorithm = "sha256", files }, JsonOptions));
        WriteSignature(sourceDirectory, signingKey);

        var packagePath = Path.Combine(rootDirectory, $"{id}-{version}-{Guid.NewGuid():N}.svcpkg");
        ZipFile.CreateFromDirectory(sourceDirectory, packagePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(sourceDirectory, recursive: true);
        return packagePath;
    }

    public string CreateDeltaWithPatch(string basePackagePath, string targetPackagePath)
    {
        var workDirectory = Path.Combine(rootDirectory, $"delta-patch-{Guid.NewGuid():N}");
        var baseDirectory = Path.Combine(workDirectory, "base");
        var targetDirectory = Path.Combine(workDirectory, "target");
        var deltaDirectory = Path.Combine(workDirectory, "delta");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(deltaDirectory);
        ZipFile.ExtractToDirectory(basePackagePath, baseDirectory);
        ZipFile.ExtractToDirectory(targetPackagePath, targetDirectory);

        var baseManifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(baseDirectory, "service.manifest.json")));
        var targetManifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(targetDirectory, "service.manifest.json")));
        var serviceId = targetManifest.GetProperty("id").GetString()!;
        var baseVersion = baseManifest.GetProperty("version").GetString()!;
        var targetVersion = targetManifest.GetProperty("version").GetString()!;
        var baseChecksums = JsonSerializer.Deserialize<ChecksumDocument>(File.ReadAllText(Path.Combine(baseDirectory, "checksums.json")), JsonOptions)!;
        var targetChecksums = JsonSerializer.Deserialize<ChecksumDocument>(File.ReadAllText(Path.Combine(targetDirectory, "checksums.json")), JsonOptions)!;
        var baseFiles = baseChecksums.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var changedFiles = new List<ChecksumFile>();
        foreach (var targetFile in targetChecksums.Files)
        {
            if (targetFile.Path.Equals("bin/large.dat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!baseFiles.TryGetValue(targetFile.Path, out var baseFile) ||
                !baseFile.Sha256.Equals(targetFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changedFiles.Add(targetFile);
                var source = Path.Combine(targetDirectory, targetFile.Path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(deltaDirectory, targetFile.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        var largeBase = baseChecksums.Files.Single(file => file.Path == "bin/large.dat");
        var largeTarget = targetChecksums.Files.Single(file => file.Path == "bin/large.dat");
        var patchDataPath = Path.Combine(deltaDirectory, ".patch-data", "bin_large.dat.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(patchDataPath)!);
        var targetLargeBytes = File.ReadAllBytes(Path.Combine(targetDirectory, "bin", "large.dat"));
        File.WriteAllBytes(patchDataPath, targetLargeBytes.Skip(65536).Take(65536).ToArray());

        File.Copy(Path.Combine(targetDirectory, "service.manifest.json"), Path.Combine(deltaDirectory, "service.manifest.json"), overwrite: true);
        File.Copy(Path.Combine(targetDirectory, "checksums.json"), Path.Combine(deltaDirectory, "checksums.json"), overwrite: true);
        File.Copy(Path.Combine(targetDirectory, "signature.sig"), Path.Combine(deltaDirectory, "signature.sig"), overwrite: true);
        File.WriteAllText(
            Path.Combine(deltaDirectory, "service-delta.json"),
            JsonSerializer.Serialize(new
            {
                manifestVersion = 1,
                serviceId,
                baseVersion,
                version = targetVersion,
                generatedAt = DateTimeOffset.UtcNow,
                files = changedFiles,
                patches = new[]
                {
                    new
                    {
                        path = "bin/large.dat",
                        blockSize = 65536,
                        baseSize = 196608,
                        baseSha256 = largeBase.Sha256,
                        targetSize = 196608,
                        targetSha256 = largeTarget.Sha256,
                        dataPath = ".patch-data/bin_large.dat.bin",
                        operations = new object[]
                        {
                            new { type = "copy", offset = 0, dataOffset = 0, length = 65536 },
                            new { type = "data", offset = 0, dataOffset = 0, length = 65536 },
                            new { type = "copy", offset = 131072, dataOffset = 0, length = 65536 }
                        }
                    }
                },
                delete = Array.Empty<string>()
            }, JsonOptions));

        var packagePath = Path.Combine(rootDirectory, $"{serviceId}-delta-{baseVersion}-to-{targetVersion}-{Guid.NewGuid():N}.svcdelta");
        ZipFile.CreateFromDirectory(deltaDirectory, packagePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(workDirectory, recursive: true);
        return packagePath;
    }

    public string CreateDelta(string basePackagePath, string targetPackagePath)
    {
        var workDirectory = Path.Combine(rootDirectory, $"delta-{Guid.NewGuid():N}");
        var baseDirectory = Path.Combine(workDirectory, "base");
        var targetDirectory = Path.Combine(workDirectory, "target");
        var deltaDirectory = Path.Combine(workDirectory, "delta");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(deltaDirectory);
        ZipFile.ExtractToDirectory(basePackagePath, baseDirectory);
        ZipFile.ExtractToDirectory(targetPackagePath, targetDirectory);

        var baseManifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(baseDirectory, "service.manifest.json")));
        var targetManifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(targetDirectory, "service.manifest.json")));
        var serviceId = targetManifest.GetProperty("id").GetString()!;
        var baseVersion = baseManifest.GetProperty("version").GetString()!;
        var targetVersion = targetManifest.GetProperty("version").GetString()!;
        var baseChecksums = JsonSerializer.Deserialize<ChecksumDocument>(File.ReadAllText(Path.Combine(baseDirectory, "checksums.json")), JsonOptions)!;
        var targetChecksums = JsonSerializer.Deserialize<ChecksumDocument>(File.ReadAllText(Path.Combine(targetDirectory, "checksums.json")), JsonOptions)!;
        var baseFiles = baseChecksums.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var changedFiles = new List<ChecksumFile>();
        foreach (var targetFile in targetChecksums.Files)
        {
            if (!baseFiles.TryGetValue(targetFile.Path, out var baseFile) ||
                !baseFile.Sha256.Equals(targetFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changedFiles.Add(targetFile);
                var source = Path.Combine(targetDirectory, targetFile.Path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(deltaDirectory, targetFile.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        var targetFiles = targetChecksums.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedFiles = baseChecksums.Files
            .Where(file => !targetFiles.Contains(file.Path))
            .Select(file => file.Path)
            .ToArray();

        File.Copy(Path.Combine(targetDirectory, "service.manifest.json"), Path.Combine(deltaDirectory, "service.manifest.json"), overwrite: true);
        File.Copy(Path.Combine(targetDirectory, "checksums.json"), Path.Combine(deltaDirectory, "checksums.json"), overwrite: true);
        File.Copy(Path.Combine(targetDirectory, "signature.sig"), Path.Combine(deltaDirectory, "signature.sig"), overwrite: true);
        File.WriteAllText(
            Path.Combine(deltaDirectory, "service-delta.json"),
            JsonSerializer.Serialize(new
            {
                manifestVersion = 1,
                serviceId,
                baseVersion,
                version = targetVersion,
                generatedAt = DateTimeOffset.UtcNow,
                files = changedFiles,
                delete = deletedFiles
            }, JsonOptions));

        var packagePath = Path.Combine(rootDirectory, $"{serviceId}-delta-{baseVersion}-to-{targetVersion}-{Guid.NewGuid():N}.svcdelta");
        ZipFile.CreateFromDirectory(deltaDirectory, packagePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(workDirectory, recursive: true);
        return packagePath;
    }

    private sealed record ChecksumDocument(string Algorithm, IReadOnlyList<ChecksumFile> Files);
    private sealed record ChecksumFile(string Path, string Sha256);

    private static void WriteSignature(string sourceDirectory, ECDsa? signingKey)
    {
        var signaturePath = Path.Combine(sourceDirectory, "signature.sig");
        if (signingKey is null)
        {
            File.WriteAllText(signaturePath, "test-placeholder");
            return;
        }

        var checksumsBytes = File.ReadAllBytes(Path.Combine(sourceDirectory, "checksums.json"));
        var signature = signingKey.SignData(checksumsBytes, HashAlgorithmName.SHA256);
        File.WriteAllText(
            signaturePath,
            JsonSerializer.Serialize(new
            {
                algorithm = "ecdsa-p256-sha256",
                keyId = "test-service-package-key",
                signedFile = "checksums.json",
                value = Convert.ToBase64String(signature)
            }, JsonOptions));
    }
}

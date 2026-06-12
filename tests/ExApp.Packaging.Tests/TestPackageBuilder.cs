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
        bool corruptChecksum = false)
    {
        var sourceDirectory = Path.Combine(rootDirectory, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "bin"));
        File.WriteAllText(Path.Combine(sourceDirectory, "bin", "Test.Service.exe"), $"payload-{version}");

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
        File.WriteAllText(Path.Combine(sourceDirectory, "signature.sig"), "test-placeholder");

        var packagePath = Path.Combine(rootDirectory, $"{id}-{version}-{Guid.NewGuid():N}.svcpkg");
        ZipFile.CreateFromDirectory(sourceDirectory, packagePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(sourceDirectory, recursive: true);
        return packagePath;
    }
}

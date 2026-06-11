using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MyApp.Packaging.Models;

namespace MyApp.Packaging;

internal sealed class ServicePackageReader(PackageManagerOptions options)
{
    public async Task<string> ComputePackageHashAsync(string packagePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(packagePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void ExtractSecure(string packagePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;

        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > options.MaximumPackageEntries)
        {
            throw new PackageException("package.tooManyEntries", $"Package contains more than {options.MaximumPackageEntries} entries.");
        }

        foreach (var entry in archive.Entries)
        {
            var relativePath = PackageValidator.NormalizeRelativePath(entry.FullName);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("package.pathTraversal", $"Archive entry '{entry.FullName}' escapes the package root.");
            }

            if (!extractedPaths.Add(destinationPath))
            {
                throw new PackageException("package.duplicateEntry", $"Archive entry '{entry.FullName}' is duplicated.");
            }

            expandedBytes += entry.Length;
            if (expandedBytes > options.MaximumExpandedPackageBytes)
            {
                throw new PackageException("package.tooLarge", $"Expanded package exceeds {options.MaximumExpandedPackageBytes} bytes.");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    public ServiceManifest ReadManifest(string packageDirectory) =>
        ReadRequiredJson<ServiceManifest>(Path.Combine(packageDirectory, "service.manifest.json"), "manifest.missing", "service.manifest.json is missing.");

    public ChecksumManifest ReadChecksums(string packageDirectory) =>
        ReadRequiredJson<ChecksumManifest>(Path.Combine(packageDirectory, "checksums.json"), "checksums.missing", "checksums.json is missing.");

    private static T ReadRequiredJson<T>(string path, string missingCode, string missingMessage)
    {
        if (!File.Exists(path))
        {
            throw new PackageException(missingCode, missingMessage);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), PackageJson.Options)
                ?? throw new PackageException("package.invalidJson", $"{Path.GetFileName(path)} is empty.");
        }
        catch (JsonException exception)
        {
            throw new PackageException("package.invalidJson", $"{Path.GetFileName(path)} contains invalid JSON.", exception);
        }
    }
}

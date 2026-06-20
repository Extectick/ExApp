using System.IO.Compression;

namespace ExApp.Core.Updates;

public static class AppPackageExtractor
{
    private const int MaximumAppPackageEntries = 20000;
    private const long MaximumExpandedAppPackageBytes = 2L * 1024 * 1024 * 1024;

    public static void ExtractSecure(string packagePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count > MaximumAppPackageEntries)
            {
                throw new InvalidOperationException($"Application update package contains more than {MaximumAppPackageEntries} entries.");
            }

            foreach (var entry in archive.Entries)
            {
                var relativePath = NormalizeArchivePath(entry.FullName);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                ValidateArchiveRelativePath(relativePath);
                var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Application update archive entry '{entry.FullName}' escapes the staging directory.");
                }

                var duplicateKey = destinationPath.TrimEnd(Path.DirectorySeparatorChar);
                if (!extractedPaths.Add(duplicateKey))
                {
                    throw new InvalidOperationException($"Application update archive entry '{entry.FullName}' is duplicated.");
                }

                expandedBytes += entry.Length;
                if (expandedBytes > MaximumExpandedAppPackageBytes)
                {
                    throw new InvalidOperationException($"Expanded application update package exceeds {MaximumExpandedAppPackageBytes} bytes.");
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
        catch
        {
            TryDeleteDirectory(destinationDirectory);
            throw;
        }
    }

    private static void ValidateArchiveRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(static segment => segment == ".."))
        {
            throw new InvalidOperationException($"Application update archive entry '{relativePath}' is not a safe relative path.");
        }
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

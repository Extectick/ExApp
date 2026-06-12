using System.Security.Cryptography;
using System.Text.Json;

namespace MyApp.Desktop.Services;

internal sealed class ServiceCatalogClient
{
    private const string ServiceReleasesApiUrl =
        "https://api.github.com/repos/Extectick/ExApp/releases?per_page=20";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new();
    private readonly string _catalogRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExApp",
        "catalog");

    public ServiceCatalogClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ExApp/0.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ServiceCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_catalogRoot);
        var cachePath = Path.Combine(_catalogRoot, "services.stable.json");
        var source = Environment.GetEnvironmentVariable("EXAPP_CATALOG_URL");

        try
        {
            var json = string.IsNullOrWhiteSpace(source)
                ? await ReadLatestServiceCatalogAsync(cancellationToken)
                : await ReadSourceAsync(source, cancellationToken);
            var catalog = Parse(json);
            await File.WriteAllTextAsync(cachePath, json, cancellationToken);
            return catalog;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or JsonException or InvalidOperationException)
        {
            AppLogger.Info($"Catalog source failed, falling back. {exception.Message}");
        }

        if (File.Exists(cachePath))
        {
            return Parse(await File.ReadAllTextAsync(cachePath, cancellationToken));
        }

        return Parse(await File.ReadAllTextAsync(GetBundledCatalogPath(), cancellationToken));
    }

    public async Task<CatalogPackageResolution> ResolvePackageAsync(
        ServiceCatalogItem item,
        CancellationToken cancellationToken = default)
    {
        if (item.Package is null)
        {
            throw new InvalidOperationException($"Service '{item.Id}' has no package.");
        }

        var url = item.Package.Url;
        var packagePath = IsHttp(url)
            ? await DownloadPackageAsync(item, cancellationToken)
            : ResolveLocalPackagePath(url);

        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException($"Package '{packagePath}' was not found.", packagePath);
        }

        var actualHash = await ComputeSha256Async(packagePath, cancellationToken);
        if (!actualHash.Equals(item.Package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Catalog package SHA-256 does not match the downloaded package.");
        }

        return new CatalogPackageResolution(packagePath, item.Package.Sha256);
    }

    private static ServiceCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<ServiceCatalog>(json, JsonOptions)
            ?? throw new InvalidOperationException("Service catalog is empty.");
        if (catalog.CatalogVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported service catalog version '{catalog.CatalogVersion}'.");
        }

        ValidateSignaturePlaceholder(catalog);
        return catalog;
    }

    private static void ValidateSignaturePlaceholder(ServiceCatalog catalog)
    {
        if (!catalog.Signature.Algorithm.Equals("dev-placeholder", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(catalog.Signature.KeyId) ||
            string.IsNullOrWhiteSpace(catalog.Signature.Value))
        {
            throw new InvalidOperationException("Service catalog signature placeholder is invalid.");
        }
    }

    private async Task<string> ReadSourceAsync(string source, CancellationToken cancellationToken)
    {
        if (IsHttp(source))
        {
            return await _httpClient.GetStringAsync(source, cancellationToken);
        }

        return await File.ReadAllTextAsync(ResolveLocalCatalogPath(source), cancellationToken);
    }

    private async Task<string> ReadLatestServiceCatalogAsync(CancellationToken cancellationToken)
    {
        var releasesJson = await _httpClient.GetStringAsync(ServiceReleasesApiUrl, cancellationToken);
        using var document = JsonDocument.Parse(releasesJson);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() ||
                !release.GetProperty("tag_name").GetString()!.StartsWith("services-v", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "services.stable.json")
                {
                    var url = asset.GetProperty("browser_download_url").GetString()
                        ?? throw new InvalidOperationException("The service catalog asset has no download URL.");
                    return await _httpClient.GetStringAsync(url, cancellationToken);
                }
            }
        }

        throw new InvalidOperationException("No published services-v* release contains services.stable.json.");
    }

    private async Task<string> DownloadPackageAsync(ServiceCatalogItem item, CancellationToken cancellationToken)
    {
        var package = item.Package!;
        var packagesRoot = Path.Combine(_catalogRoot, "packages");
        Directory.CreateDirectory(packagesRoot);
        var targetPath = Path.Combine(packagesRoot, $"{item.Id}-{item.Version}-{package.Sha256[..12]}.svcpkg");
        if (File.Exists(targetPath))
        {
            var cachedHash = await ComputeSha256Async(targetPath, cancellationToken);
            if (cachedHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return targetPath;
            }

            File.Delete(targetPath);
        }

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.download";
        try
        {
            await using var remote = await _httpClient.GetStreamAsync(package.Url, cancellationToken);
            await using (var local = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await remote.CopyToAsync(local, cancellationToken);
            }

            var downloadedHash = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!downloadedHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Catalog package SHA-256 does not match the downloaded package.");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            return targetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveLocalCatalogPath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string ResolveLocalPackagePath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, "catalog", path));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.GetFullPath(Path.Combine(current.FullName, path));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string GetBundledCatalogPath() =>
        Path.Combine(AppContext.BaseDirectory, "catalog", "services.stable.json");

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

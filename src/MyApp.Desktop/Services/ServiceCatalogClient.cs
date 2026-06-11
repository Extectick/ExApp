using System.Security.Cryptography;
using System.Text.Json;

namespace MyApp.Desktop.Services;

internal sealed class ServiceCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new();
    private readonly string _catalogRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExApp",
        "catalog");

    public async Task<ServiceCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_catalogRoot);
        var cachePath = Path.Combine(_catalogRoot, "services.stable.json");
        var source = GetConfiguredSource();

        if (!string.IsNullOrWhiteSpace(source))
        {
            try
            {
                var json = await ReadSourceAsync(source, cancellationToken);
                var catalog = Parse(json);
                await File.WriteAllTextAsync(cachePath, json, cancellationToken);
                return catalog;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException or InvalidOperationException)
            {
                AppLogger.Info($"Catalog source failed, falling back. {exception.Message}");
            }
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

    private async Task<string> DownloadPackageAsync(ServiceCatalogItem item, CancellationToken cancellationToken)
    {
        var package = item.Package!;
        var packagesRoot = Path.Combine(_catalogRoot, "packages");
        Directory.CreateDirectory(packagesRoot);
        var targetPath = Path.Combine(packagesRoot, $"{item.Id}-{item.Version}-{package.Sha256[..12]}.svcpkg");
        if (File.Exists(targetPath))
        {
            return targetPath;
        }

        await using var remote = await _httpClient.GetStreamAsync(package.Url, cancellationToken);
        await using var local = File.Create(targetPath);
        await remote.CopyToAsync(local, cancellationToken);
        return targetPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? GetConfiguredSource() =>
        Environment.GetEnvironmentVariable("EXAPP_CATALOG_URL") ?? GetBundledCatalogPath();

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

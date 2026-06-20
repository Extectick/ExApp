using System.Security.Cryptography;
using System.Text.Json;

namespace ExApp.Desktop.Services;

internal sealed class ServiceCatalogClient
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
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
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ExApp/0.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ServiceCatalog> LoadAsync(CancellationToken cancellationToken = default)
        => (await LoadWithMetadataAsync(cancellationToken: cancellationToken)).Catalog;

    public async Task<ServiceCatalogLoadResult> LoadWithMetadataAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_catalogRoot);
        var cachePath = Path.Combine(_catalogRoot, "services.stable.json");
        var source = Environment.GetEnvironmentVariable("EXAPP_CATALOG_URL");

        if (!forceRefresh &&
            string.IsNullOrWhiteSpace(source) &&
            File.Exists(cachePath) &&
            DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheLifetime)
        {
            try
            {
                return await ReadCatalogAsync(cachePath, ServiceCatalogSource.Cache, isOffline: false, requireSignature: true, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidOperationException)
            {
                AppLogger.Info($"Fresh catalog cache is invalid, refreshing. {exception.Message}");
            }
        }

        try
        {
            var json = string.IsNullOrWhiteSpace(source)
                ? await ReadLatestServiceCatalogAsync(cancellationToken)
                : await ReadSourceAsync(source, cancellationToken);
            var requireSignature = string.IsNullOrWhiteSpace(source) || IsHttp(source);
            var catalog = Parse(json, requireSignature);
            await WriteAtomicallyAsync(cachePath, json, cancellationToken);
            return new ServiceCatalogLoadResult(
                catalog,
                string.IsNullOrWhiteSpace(source) ? ServiceCatalogSource.Remote : ServiceCatalogSource.Override,
                DateTimeOffset.Now,
                IsOffline: false);
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or JsonException or InvalidOperationException)
        {
            AppLogger.Info($"Catalog source failed, falling back. {exception.Message}");
        }

        if (File.Exists(cachePath))
        {
            try
            {
                return await ReadCatalogAsync(cachePath, ServiceCatalogSource.Cache, isOffline: true, requireSignature: true, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidOperationException)
            {
                AppLogger.Info($"Catalog cache is invalid, using bundled catalog. {exception.Message}");
            }
        }

        return await ReadCatalogAsync(
            GetBundledCatalogPath(),
            ServiceCatalogSource.Bundled,
            isOffline: true,
            requireSignature: false,
            cancellationToken);
    }

    public async Task<CatalogPackageResolution> ResolvePackageAsync(
        ServiceCatalogItem item,
        CancellationToken cancellationToken = default) =>
        await ResolvePackageAsync(item, currentVersion: null, cancellationToken);

    public async Task<CatalogPackageResolution> ResolvePackageAsync(
        ServiceCatalogItem item,
        string? currentVersion = null,
        CancellationToken cancellationToken = default,
        bool preferDelta = true)
    {
        var delta = preferDelta ? SelectDelta(item, currentVersion, item.Package?.Size) : null;
        var useDelta = delta is not null;
        var package = useDelta
            ? new PackageDescriptor(delta!.Url, delta.Sha256, delta.Size, ".svcdelta")
            : item.Package is not null
                ? new PackageDescriptor(item.Package.Url, item.Package.Sha256, item.Package.Size, ".svcpkg")
                : null;
        if (package is null)
        {
            throw new InvalidOperationException($"Service '{item.Id}' has no package.");
        }

        var packagePath = IsHttp(package.Url)
            ? await DownloadPackageAsync(item, package, cancellationToken)
            : ResolveLocalPackagePath(package.Url);

        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException($"Package '{packagePath}' was not found.", packagePath);
        }

        var actualHash = await ComputeSha256Async(packagePath, cancellationToken);
        if (!actualHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Catalog package SHA-256 does not match the downloaded package.");
        }

        return new CatalogPackageResolution(packagePath, package.Sha256, useDelta);
    }

    private static ServiceCatalog Parse(string json, bool requireSignature)
    {
        var catalog = JsonSerializer.Deserialize<ServiceCatalog>(json, JsonOptions)
            ?? throw new InvalidOperationException("Service catalog is empty.");
        if (catalog.CatalogVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported service catalog version '{catalog.CatalogVersion}'.");
        }

        ServiceCatalogSignatureVerifier.Verify(json, catalog, requireSignature);
        ValidateServices(catalog);
        return catalog;
    }

    private static void ValidateServices(ServiceCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Channel))
        {
            throw new InvalidOperationException("Service catalog channel is missing.");
        }

        var serviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in catalog.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Id) ||
                string.IsNullOrWhiteSpace(service.Name) ||
                string.IsNullOrWhiteSpace(service.Version))
            {
                throw new InvalidOperationException("A catalog service has incomplete identity metadata.");
            }

            if (!serviceIds.Add(service.Id))
            {
                throw new InvalidOperationException($"Service catalog contains duplicate id '{service.Id}'.");
            }

            if (!service.Status.Equals("available", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (service.Package is null ||
                string.IsNullOrWhiteSpace(service.Package.Url) ||
                service.Package.Size <= 0 ||
                service.Package.Sha256.Length != 64 ||
                !service.Package.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException($"Service '{service.Id}' has invalid package metadata.");
            }

            if (EnumerateDeltas(service).Any(static delta => !IsValidDelta(delta)))
            {
                throw new InvalidOperationException($"Service '{service.Id}' has invalid delta metadata.");
            }
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

    private static async Task<ServiceCatalogLoadResult> ReadCatalogAsync(
        string path,
        ServiceCatalogSource source,
        bool isOffline,
        bool requireSignature,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return new ServiceCatalogLoadResult(
            Parse(json, requireSignature),
            source,
            File.GetLastWriteTimeUtc(path),
            isOffline);
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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

    private async Task<string> DownloadPackageAsync(
        ServiceCatalogItem item,
        PackageDescriptor package,
        CancellationToken cancellationToken)
    {
        var packagesRoot = Path.Combine(_catalogRoot, "packages");
        Directory.CreateDirectory(packagesRoot);
        var targetPath = Path.Combine(packagesRoot, $"{item.Id}-{item.Version}-{package.Sha256[..12]}{package.Extension}");
        if (File.Exists(targetPath))
        {
            var cachedHash = await ComputeSha256Async(targetPath, cancellationToken);
            if (cachedHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return targetPath;
            }

            File.Delete(targetPath);
        }

        var temporaryPath = $"{targetPath}.download";
        if (File.Exists(temporaryPath) &&
            package.Size > 0 &&
            new FileInfo(temporaryPath).Length == package.Size &&
            (await ComputeSha256Async(temporaryPath, cancellationToken)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(temporaryPath, targetPath, overwrite: true);
            return targetPath;
        }

        await DownloadHttpWithResumeAsync(package.Url, temporaryPath, package.Size, cancellationToken);

        var downloadedHash = await ComputeSha256Async(temporaryPath, cancellationToken);
        if (!downloadedHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("Catalog package SHA-256 does not match the downloaded package.");
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
        return targetPath;
    }

    private static ServiceCatalogDeltaPackage? SelectDelta(ServiceCatalogItem item, string? currentVersion, long? fullPackageSize)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return null;
        }

        return EnumerateDeltas(item)
            .Where(delta => delta.BaseVersion.Equals(currentVersion, StringComparison.OrdinalIgnoreCase))
            .Where(delta => fullPackageSize is null || delta.Size < fullPackageSize)
            .OrderBy(static delta => delta.Size)
            .FirstOrDefault();
    }

    private static IEnumerable<ServiceCatalogDeltaPackage> EnumerateDeltas(ServiceCatalogItem item)
    {
        foreach (var delta in item.Deltas)
        {
            yield return delta;
        }

        if (item.Delta is not null &&
            item.Deltas.All(delta => !delta.BaseVersion.Equals(item.Delta.BaseVersion, StringComparison.OrdinalIgnoreCase)))
        {
            yield return item.Delta;
        }
    }

    private static bool IsValidDelta(ServiceCatalogDeltaPackage delta) =>
        !string.IsNullOrWhiteSpace(delta.BaseVersion) &&
        !string.IsNullOrWhiteSpace(delta.Url) &&
        delta.Size > 0 &&
        delta.Sha256.Length == 64 &&
        delta.Sha256.All(Uri.IsHexDigit) &&
        delta.ChangedFiles >= 0 &&
        delta.PatchedFiles >= 0 &&
        delta.DeletedFiles >= 0;

    private async Task DownloadHttpWithResumeAsync(
        string url,
        string temporaryPath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadHttpAttemptAsync(url, temporaryPath, expectedSize, cancellationToken);
                return;
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                exception is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), cancellationToken);
            }
        }

        await DownloadHttpAttemptAsync(url, temporaryPath, expectedSize, cancellationToken);
    }

    private async Task DownloadHttpAttemptAsync(
        string url,
        string temporaryPath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        if (expectedSize > 0 && existingLength > expectedSize)
        {
            File.Delete(temporaryPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (existingLength > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            File.Delete(temporaryPath);
            existingLength = 0;
        }
        else if (existingLength > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode();
            throw new IOException("The service package server did not accept a ranged download request.");
        }

        response.EnsureSuccessStatusCode();
        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var local = new FileStream(
            temporaryPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await remote.CopyToAsync(local, cancellationToken);
        await local.FlushAsync(cancellationToken);
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

    private sealed record PackageDescriptor(string Url, string Sha256, long Size, string Extension);
}

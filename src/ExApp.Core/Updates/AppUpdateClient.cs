using System.Security.Cryptography;
using System.Text.Json;

namespace ExApp.Core.Updates;

public sealed class AppUpdateClient : IDisposable
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/Extectick/ExApp/releases?per_page=30";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public AppUpdateClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ExApp/0.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppUpdateCheckResult> CheckAsync(
        string currentVersion,
        string channel,
        CancellationToken cancellationToken = default)
    {
        var source = Environment.GetEnvironmentVariable("EXAPP_UPDATE_MANIFEST_URL");
        var manifest = string.IsNullOrWhiteSpace(source)
            ? await ReadLatestManifestAsync(channel, cancellationToken)
            : await ReadManifestAsync(source, cancellationToken);
        if (manifest is null)
        {
            return new AppUpdateCheckResult(currentVersion, null, IsUpdateAvailable: false);
        }

        Validate(manifest);
        return new AppUpdateCheckResult(
            currentVersion,
            manifest,
            ParseVersion(manifest.Version) > ParseVersion(currentVersion));
    }

    public async Task<string> DownloadAsync(
        AppReleaseManifest manifest,
        string currentVersion,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var download = await DownloadPackageAsync(
            manifest,
            currentVersion,
            destinationDirectory,
            progress,
            cancellationToken);
        return download.PackagePath;
    }

    public async Task<AppPackageDownload> DownloadPackageAsync(
        AppReleaseManifest manifest,
        string currentVersion,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        bool preferDelta = true)
    {
        Directory.CreateDirectory(destinationDirectory);
        var package = SelectPackage(manifest, currentVersion, preferDelta);
        var targetPath = Path.Combine(destinationDirectory, GetPackageFileName(package.Url, manifest.Version, package.BaseVersion));
        var isDelta = package.BaseVersion is not null;
        if (File.Exists(targetPath) &&
            await ComputeSha256Async(targetPath, cancellationToken) == package.Sha256)
        {
            progress?.Report(1);
            return new AppPackageDownload(targetPath, isDelta);
        }

        var temporaryPath = $"{targetPath}.download";
        if (!IsHttp(package.Url))
        {
            File.Copy(Path.GetFullPath(package.Url), temporaryPath, overwrite: true);
            progress?.Report(1);
        }
        else
        {
            if (File.Exists(temporaryPath) &&
                package.Size > 0 &&
                new FileInfo(temporaryPath).Length == package.Size &&
                (await ComputeSha256Async(temporaryPath, cancellationToken)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
                progress?.Report(1);
                return new AppPackageDownload(targetPath, isDelta);
            }

            await DownloadHttpWithResumeAsync(package.Url, temporaryPath, package.Size, progress, cancellationToken);
        }

        var hash = await ComputeSha256Async(temporaryPath, cancellationToken);
        if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("ExApp update SHA-256 does not match the release manifest.");
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
        progress?.Report(1);
        return new AppPackageDownload(targetPath, isDelta);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<AppReleaseManifest?> ReadLatestManifestAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        var releasesJson = await _httpClient.GetStringAsync(ReleasesApiUrl, cancellationToken);
        using var document = JsonDocument.Parse(releasesJson);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || !MatchesChannel(release, channel))
            {
                continue;
            }

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "exapp-update.json")
                {
                    var url = asset.GetProperty("browser_download_url").GetString()
                        ?? throw new InvalidOperationException("Update manifest asset has no URL.");
                    return await ReadManifestAsync(url, cancellationToken);
                }
            }
        }

        return null;
    }

    private async Task<AppReleaseManifest> ReadManifestAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var requireSignature = Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        var json = requireSignature
            ? await _httpClient.GetStringAsync(uri!, cancellationToken)
            : await File.ReadAllTextAsync(Path.GetFullPath(source), cancellationToken);
        var manifest = JsonSerializer.Deserialize<AppReleaseManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("ExApp update manifest is empty.");
        AppUpdateSignatureVerifier.Verify(json, manifest, requireSignature);
        return manifest;
    }

    private static bool MatchesChannel(JsonElement release, string channel)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        var prerelease = release.GetProperty("prerelease").GetBoolean();
        return tag.StartsWith("app-v", StringComparison.OrdinalIgnoreCase) &&
               (channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? prerelease : !prerelease);
    }

    private static void Validate(AppReleaseManifest manifest)
    {
        if (manifest.ManifestVersion != 1 ||
            !TryParseVersion(manifest.Version, out _) ||
            string.IsNullOrWhiteSpace(manifest.Channel) ||
            string.IsNullOrWhiteSpace(manifest.Package.Url) ||
            manifest.Package.Size <= 0 ||
            manifest.Package.Sha256.Length != 64 ||
            !manifest.Package.Sha256.All(Uri.IsHexDigit) ||
            (manifest.Delta is not null && !IsValidDelta(manifest.Delta)) ||
            manifest.Deltas.Any(static delta => !IsValidDelta(delta)))
        {
            throw new InvalidOperationException("ExApp update manifest is invalid.");
        }
    }

    private static AppPackageDescriptor SelectPackage(
        AppReleaseManifest manifest,
        string currentVersion,
        bool preferDelta)
    {
        if (!preferDelta)
        {
            return new AppPackageDescriptor(manifest.Package.Url, manifest.Package.Sha256, manifest.Package.Size, BaseVersion: null);
        }

        var normalizedCurrent = NormalizeVersion(currentVersion);
        var delta = EnumerateDeltas(manifest)
            .Where(delta => NormalizeVersion(delta.BaseVersion).Equals(normalizedCurrent, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static delta => delta.Size)
            .FirstOrDefault();
        return delta is not null
            ? new AppPackageDescriptor(delta.Url, delta.Sha256, delta.Size, delta.BaseVersion)
            : new AppPackageDescriptor(manifest.Package.Url, manifest.Package.Sha256, manifest.Package.Size, BaseVersion: null);
    }

    private static IEnumerable<AppDeltaPackage> EnumerateDeltas(AppReleaseManifest manifest)
    {
        foreach (var delta in manifest.Deltas)
        {
            yield return delta;
        }

        if (manifest.Delta is not null &&
            manifest.Deltas.All(delta => !delta.BaseVersion.Equals(manifest.Delta.BaseVersion, StringComparison.OrdinalIgnoreCase)))
        {
            yield return manifest.Delta;
        }
    }

    private static bool IsValidDelta(AppDeltaPackage delta) =>
        TryParseVersion(delta.BaseVersion, out _) &&
        !string.IsNullOrWhiteSpace(delta.Url) &&
        delta.Size > 0 &&
        delta.Sha256.Length == 64 &&
        delta.Sha256.All(Uri.IsHexDigit) &&
        delta.ChangedFiles >= 0 &&
        delta.PatchedFiles >= 0 &&
        delta.DeletedFiles >= 0;

    private static string GetPackageFileName(string source, string version, string? baseVersion)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return Path.GetFileName(uri.AbsolutePath);
        }

        var fileName = Path.GetFileName(source);
        return string.IsNullOrWhiteSpace(fileName)
            ? baseVersion is null
                ? $"exapp-{version}-win-x64.zip"
                : $"exapp-delta-{baseVersion}-to-{version}-win-x64.zip"
            : fileName;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private async Task DownloadHttpWithResumeAsync(
        string url,
        string temporaryPath,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadHttpAttemptAsync(url, temporaryPath, expectedSize, progress, cancellationToken);
                return;
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                exception is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), cancellationToken);
            }
        }

        await DownloadHttpAttemptAsync(url, temporaryPath, expectedSize, progress, cancellationToken);
    }

    private async Task DownloadHttpAttemptAsync(
        string url,
        string temporaryPath,
        long expectedSize,
        IProgress<double>? progress,
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
            throw new IOException("The update server did not accept a ranged download request.");
        }

        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var total = expectedSize > 0 ? expectedSize : existingLength + contentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            temporaryPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        var received = existingLength;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)received / total, 0, 1));
            }
        }

        await output.FlushAsync(cancellationToken);
    }

    private static Version ParseVersion(string value) =>
        TryParseVersion(value, out var version)
            ? version
            : throw new InvalidOperationException($"Version '{value}' is invalid.");

    private static bool TryParseVersion(string value, out Version version) =>
        Version.TryParse(value.Split('-', 2)[0], out version!);

    private static string NormalizeVersion(string value) => value.Split('-', 2)[0];

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private sealed record AppPackageDescriptor(
        string Url,
        string Sha256,
        long Size,
        string? BaseVersion);
}

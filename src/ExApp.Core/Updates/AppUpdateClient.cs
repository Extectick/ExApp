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
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var targetPath = Path.Combine(destinationDirectory, $"exapp-{manifest.Version}-win-x64.zip");
        if (File.Exists(targetPath) &&
            await ComputeSha256Async(targetPath, cancellationToken) == manifest.Package.Sha256)
        {
            progress?.Report(1);
            return targetPath;
        }

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.download";
        try
        {
            if (!IsHttp(manifest.Package.Url))
            {
                File.Copy(Path.GetFullPath(manifest.Package.Url), temporaryPath, overwrite: true);
                progress?.Report(1);
            }
            else
            {
                using var response = await _httpClient.GetAsync(
                    manifest.Package.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? manifest.Package.Size;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[81920];
                long received = 0;
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
            var hash = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!hash.Equals(manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ExApp update SHA-256 does not match the release manifest.");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            progress?.Report(1);
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
        var json = Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? await _httpClient.GetStringAsync(uri, cancellationToken)
            : await File.ReadAllTextAsync(Path.GetFullPath(source), cancellationToken);
        return JsonSerializer.Deserialize<AppReleaseManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("ExApp update manifest is empty.");
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
            !manifest.Package.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("ExApp update manifest is invalid.");
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static Version ParseVersion(string value) =>
        TryParseVersion(value, out var version)
            ? version
            : throw new InvalidOperationException($"Version '{value}' is invalid.");

    private static bool TryParseVersion(string value, out Version version) =>
        Version.TryParse(value.Split('-', 2)[0], out version!);

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

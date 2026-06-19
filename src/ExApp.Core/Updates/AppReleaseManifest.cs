namespace ExApp.Core.Updates;

public sealed record AppReleaseManifest
{
    public int ManifestVersion { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public DateTimeOffset PublishedAt { get; init; }
    public string? ReleaseNotes { get; init; }
    public AppReleasePackage Package { get; init; } = new();
    public AppDeltaPackage? Delta { get; init; }
}

public sealed record AppReleasePackage
{
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
}

public sealed record AppDeltaPackage
{
    public string BaseVersion { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public int ChangedFiles { get; init; }
    public int DeletedFiles { get; init; }
}

public sealed record AppUpdateCheckResult(
    string CurrentVersion,
    AppReleaseManifest? Release,
    bool IsUpdateAvailable);

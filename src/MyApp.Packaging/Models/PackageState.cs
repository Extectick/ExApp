namespace MyApp.Packaging.Models;

public sealed record PackageState
{
    public string ServiceId { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string? PreviousVersion { get; init; }
    public string State { get; init; } = "installed";
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record InstalledServiceRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public DateTimeOffset InstalledAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record InstalledServicesRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<InstalledServiceRecord> Services { get; init; } = [];
}

public sealed record PackageInstallResult(
    ServiceManifest Manifest,
    PackageState State,
    string InstallDirectory,
    bool AlreadyInstalled);

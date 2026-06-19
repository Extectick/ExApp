namespace ExApp.Packaging.Models;

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
    bool AlreadyInstalled)
{
    public bool AppliedDelta { get; init; }
    public int CopiedFiles { get; init; }
    public int LinkedFiles { get; init; }
    public int DeletedFiles { get; init; }
}

public sealed record ServiceDeltaManifest
{
    public int ManifestVersion { get; init; }
    public string ServiceId { get; init; } = string.Empty;
    public string BaseVersion { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<ChecksumEntry> Files { get; init; } = [];
    public IReadOnlyList<FilePatchEntry> Patches { get; init; } = [];
    public IReadOnlyList<string> Delete { get; init; } = [];
}

public sealed record FilePatchEntry
{
    public string Path { get; init; } = string.Empty;
    public int BlockSize { get; init; }
    public long BaseSize { get; init; }
    public string BaseSha256 { get; init; } = string.Empty;
    public long TargetSize { get; init; }
    public string TargetSha256 { get; init; } = string.Empty;
    public string DataPath { get; init; } = string.Empty;
    public IReadOnlyList<FilePatchOperation> Operations { get; init; } = [];
}

public sealed record FilePatchOperation
{
    public string Type { get; init; } = string.Empty;
    public long Offset { get; init; }
    public long DataOffset { get; init; }
    public int Length { get; init; }
}

namespace ExApp.Packaging.Models;

public sealed record ChecksumManifest
{
    public string Algorithm { get; init; } = string.Empty;
    public IReadOnlyList<ChecksumEntry> Files { get; init; } = [];
}

public sealed record ChecksumEntry
{
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

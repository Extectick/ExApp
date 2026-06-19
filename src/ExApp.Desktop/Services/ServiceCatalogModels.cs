namespace ExApp.Desktop.Services;

internal sealed record ServiceCatalog
{
    public int CatalogVersion { get; init; }
    public string Channel { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
    public ServiceCatalogSignature Signature { get; init; } = new();
    public IReadOnlyList<ServiceCatalogItem> Services { get; init; } = [];
}

internal sealed record ServiceCatalogSignature
{
    public string Algorithm { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

internal sealed record ServiceCatalogItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public string Version { get; init; } = string.Empty;
    public ServiceCatalogPublisher Publisher { get; init; } = new();
    public string Category { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public ServiceCatalogPackage? Package { get; init; }
    public ServiceCatalogDeltaPackage? Delta { get; init; }
    public IReadOnlyList<ServiceCatalogDeltaPackage> Deltas { get; init; } = [];
}

internal sealed record ServiceCatalogPublisher
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

internal sealed record ServiceCatalogPackage
{
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
}

internal sealed record ServiceCatalogDeltaPackage
{
    public string BaseVersion { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public int ChangedFiles { get; init; }
    public int PatchedFiles { get; init; }
    public int DeletedFiles { get; init; }
}

internal sealed record CatalogPackageResolution(string PackagePath, string Sha256, bool IsDelta);

internal enum ServiceCatalogSource
{
    Remote,
    Override,
    Cache,
    Bundled
}

internal sealed record ServiceCatalogLoadResult(
    ServiceCatalog Catalog,
    ServiceCatalogSource Source,
    DateTimeOffset LoadedAt,
    bool IsOffline);

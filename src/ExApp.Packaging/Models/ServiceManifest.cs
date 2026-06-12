namespace ExApp.Packaging.Models;

public sealed record ServiceManifest
{
    public int ManifestVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public string Version { get; init; } = string.Empty;
    public ServicePublisher Publisher { get; init; } = new();
    public string Category { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ApiVersion { get; init; }
    public string MinAppVersion { get; init; } = string.Empty;
    public string MinAgentVersion { get; init; } = string.Empty;
    public ServiceEntry Entry { get; init; } = new();
    public ServiceUiDefinition? Ui { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public bool RequiresAdmin { get; init; }
    public ServiceDataPolicy DataPolicy { get; init; } = new();
}

public sealed record ServicePublisher
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record ServiceEntry
{
    public string Type { get; init; } = string.Empty;
    public string Executable { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public sealed record ServiceUiDefinition
{
    public string Type { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
}

public sealed record ServiceDataPolicy
{
    public bool PreserveOnUninstall { get; init; } = true;
}

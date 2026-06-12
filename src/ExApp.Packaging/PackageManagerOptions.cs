using System.Runtime.InteropServices;

namespace ExApp.Packaging;

public sealed record PackageManagerOptions
{
    public required string RootDirectory { get; init; }
    public string AppVersion { get; init; } = "0.1.0";
    public string AgentVersion { get; init; } = "0.1.0";
    public string Platform { get; init; } = "windows";
    public string Architecture { get; init; } = GetCurrentArchitecture();
    public int SupportedManifestVersion { get; init; } = 1;
    public int SupportedApiVersion { get; init; } = 1;
    public long MaximumExpandedPackageBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumPackageEntries { get; init; } = 4096;

    private static string GetCurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
    };
}

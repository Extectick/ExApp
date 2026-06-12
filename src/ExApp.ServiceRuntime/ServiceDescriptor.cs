using ExApp.Packaging.Models;

namespace ExApp.ServiceRuntime;

public sealed record ServiceDescriptor(
    string ServiceId,
    ServiceManifest Manifest,
    string? ExecutablePath,
    bool Installed,
    string RuntimeDirectory);

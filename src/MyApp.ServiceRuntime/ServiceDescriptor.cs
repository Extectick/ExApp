using MyApp.Packaging.Models;

namespace MyApp.ServiceRuntime;

public sealed record ServiceDescriptor(
    string ServiceId,
    ServiceManifest Manifest,
    string? ExecutablePath,
    bool Installed,
    string RuntimeDirectory);

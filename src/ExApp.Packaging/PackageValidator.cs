using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExApp.Packaging.Models;

namespace ExApp.Packaging;

internal sealed partial class PackageValidator(PackageManagerOptions options)
{
    private const string ServicePackageSignatureAlgorithm = "ecdsa-p256-sha256";
    private const string ServicePackagePublicKeyEnvironmentVariable = "EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_PEM";
    private const string ServicePackagePublicKeyBase64EnvironmentVariable = "EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_BASE64";
    private const string AllowUnsignedEnvironmentVariable = "EXAPP_ALLOW_UNSIGNED_SERVICE_PACKAGES";

    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.Ordinal)
    {
        "network",
        "background",
        "notifications",
        "filesystem.appData",
        "filesystem.downloads",
        "tun",
        "dns",
        "firewall",
        "routes",
        "admin"
    };

    public void ValidateManifest(ServiceManifest manifest, string packageDirectory)
    {
        if (manifest.ManifestVersion != options.SupportedManifestVersion)
        {
            throw new PackageException("manifest.unsupportedVersion", $"Manifest version {manifest.ManifestVersion} is not supported.");
        }

        Require(ServiceIdRegex().IsMatch(manifest.Id), "manifest.invalidId", "Service id must contain only lowercase letters, digits and hyphens.");
        Require(!string.IsNullOrWhiteSpace(manifest.Name), "manifest.nameRequired", "Service name is required.");
        Require(IsValidVersion(manifest.Version), "manifest.invalidVersion", "Service version must be a numeric semantic version.");
        Require(!string.IsNullOrWhiteSpace(manifest.Publisher.Id), "manifest.publisherRequired", "Publisher id is required.");
        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            if (IsGlyphIcon(manifest.Icon))
            {
                // Glyph icons are built into the app font and do not require package payload files.
            }
            else
            {
                Require(IsSafeRelativePath(manifest.Icon), "manifest.invalidIconPath", "Service icon path is invalid.");
                Require(File.Exists(ResolvePackagePath(packageDirectory, manifest.Icon)), "manifest.iconMissing", $"Service icon '{manifest.Icon}' is missing.");
            }
        }

        Require(manifest.Platform.Equals(options.Platform, StringComparison.OrdinalIgnoreCase), "manifest.incompatiblePlatform", $"Package platform '{manifest.Platform}' is not supported.");
        Require(manifest.Architecture.Equals(options.Architecture, StringComparison.OrdinalIgnoreCase), "manifest.incompatibleArchitecture", $"Package architecture '{manifest.Architecture}' is not supported.");
        Require(manifest.ApiVersion == options.SupportedApiVersion, "manifest.unsupportedApiVersion", $"Service API version {manifest.ApiVersion} is not supported.");
        Require(IsCompatible(options.AppVersion, manifest.MinAppVersion), "manifest.incompatibleAppVersion", $"Package requires app version {manifest.MinAppVersion} or newer.");
        Require(IsCompatible(options.AgentVersion, manifest.MinAgentVersion), "manifest.incompatibleAgentVersion", $"Package requires agent version {manifest.MinAgentVersion} or newer.");
        Require(manifest.Entry.Type.Equals("process", StringComparison.Ordinal), "manifest.unsupportedEntryType", "Only process service entries are supported.");
        Require(IsSafeRelativePath(manifest.Entry.Executable), "manifest.invalidExecutable", "Service executable path is invalid.");

        var executablePath = ResolvePackagePath(packageDirectory, manifest.Entry.Executable);
        Require(File.Exists(executablePath), "manifest.executableMissing", $"Service executable '{manifest.Entry.Executable}' is missing.");

        if (manifest.Ui is not null)
        {
            Require(IsSafeRelativePath(manifest.Ui.File), "manifest.invalidUiPath", "Service UI path is invalid.");
            Require(File.Exists(ResolvePackagePath(packageDirectory, manifest.Ui.File)), "manifest.uiMissing", $"Service UI file '{manifest.Ui.File}' is missing.");
        }

        var invalidPermission = manifest.Permissions.FirstOrDefault(permission => !SupportedPermissions.Contains(permission));
        Require(invalidPermission is null, "manifest.unsupportedPermission", $"Permission '{invalidPermission}' is not supported.");
    }

    public void ValidateChecksums(string packageDirectory, ChecksumManifest checksums)
    {
        Require(checksums.Algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase), "checksums.unsupportedAlgorithm", "Only SHA-256 checksums are supported.");
        Require(checksums.Files.Count > 0, "checksums.empty", "Checksum manifest must contain at least one file.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in checksums.Files)
        {
            Require(IsSafeRelativePath(entry.Path), "checksums.invalidPath", $"Checksum path '{entry.Path}' is invalid.");
            Require(paths.Add(NormalizeRelativePath(entry.Path)), "checksums.duplicatePath", $"Checksum path '{entry.Path}' is duplicated.");
            Require(Sha256Regex().IsMatch(entry.Sha256), "checksums.invalidHash", $"Checksum for '{entry.Path}' is invalid.");

            var filePath = ResolvePackagePath(packageDirectory, entry.Path);
            Require(File.Exists(filePath), "checksums.fileMissing", $"Checksummed file '{entry.Path}' is missing.");
            var actualHash = ComputeSha256(filePath);
            Require(actualHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase), "checksums.mismatch", $"Checksum mismatch for '{entry.Path}'.");
        }

        var payloadFiles = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(packageDirectory, path)))
            .Where(path => !path.Equals("checksums.json", StringComparison.OrdinalIgnoreCase) &&
                           !path.Equals("signature.sig", StringComparison.OrdinalIgnoreCase));
        var uncheckedFile = payloadFiles.FirstOrDefault(path => !paths.Contains(path));
        Require(uncheckedFile is null, "checksums.fileNotListed", $"Package file '{uncheckedFile}' is not listed in checksums.json.");
    }

    public void ValidateSignature(string packageDirectory)
    {
        var signaturePath = Path.Combine(packageDirectory, "signature.sig");
        Require(File.Exists(signaturePath), "signature.missing", "signature.sig is missing.");
        Require(new FileInfo(signaturePath).Length > 0, "signature.empty", "signature.sig is empty.");

        var publicKeyPem = ResolvePublicKeyPem();
        var signatureText = File.ReadAllText(signaturePath);
        if (IsDevelopmentPlaceholder(signatureText))
        {
            if (!string.IsNullOrWhiteSpace(publicKeyPem) && !AllowUnsignedForDevelopment())
            {
                throw new PackageException("signature.unsigned", "Service package is not signed.");
            }

            return;
        }

        ServicePackageSignature signature;
        try
        {
            signature = JsonSerializer.Deserialize<ServicePackageSignature>(signatureText, PackageJson.Options)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new PackageException("signature.invalidJson", "signature.sig contains invalid JSON.", exception);
        }

        Require(signature.Algorithm.Equals(ServicePackageSignatureAlgorithm, StringComparison.OrdinalIgnoreCase), "signature.unsupportedAlgorithm", "Service package signature algorithm is not supported.");
        Require(!string.IsNullOrWhiteSpace(signature.KeyId), "signature.keyMissing", "Service package signature key id is missing.");
        Require(signature.SignedFile.Equals("checksums.json", StringComparison.OrdinalIgnoreCase), "signature.unsupportedPayload", "Service package signature must sign checksums.json.");
        Require(!string.IsNullOrWhiteSpace(signature.Value), "signature.valueMissing", "Service package signature value is missing.");
        Require(!string.IsNullOrWhiteSpace(publicKeyPem), "signature.publicKeyMissing", "Service package signing public key is not configured.");

        var checksumsPath = Path.Combine(packageDirectory, "checksums.json");
        Require(File.Exists(checksumsPath), "checksums.missing", "checksums.json is missing.");

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Value);
        }
        catch (FormatException exception)
        {
            throw new PackageException("signature.invalidValue", "Service package signature value is not valid Base64.", exception);
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        var payload = File.ReadAllBytes(checksumsPath);
        Require(ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256), "signature.verificationFailed", "Service package signature verification failed.");
    }

    public static string ResolvePackagePath(string packageDirectory, string relativePath)
    {
        var root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, NormalizeRelativePath(relativePath)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException("package.pathTraversal", $"Package path '{relativePath}' escapes the package root.");
        }

        return fullPath;
    }

    public static string NormalizeRelativePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Contains(':', StringComparison.Ordinal) &&
        !NormalizeRelativePath(path).Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal);

    private static bool IsGlyphIcon(string icon) =>
        icon.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase) &&
        GlyphIconRegex().IsMatch(icon["glyph:".Length..]);

    private static bool IsCompatible(string currentVersion, string minimumVersion) =>
        Version.TryParse(currentVersion, out var current) &&
        Version.TryParse(minimumVersion, out var minimum) &&
        current >= minimum;

    private static bool IsValidVersion(string version) => Version.TryParse(version, out _);

    private static void Require(bool condition, string code, string message)
    {
        if (!condition)
        {
            throw new PackageException(code, message);
        }
    }

    private string? ResolvePublicKeyPem() =>
        NormalizePem(options.ServicePackageSigningPublicKeyPem)
        ?? ResolvePemEnvironment(ServicePackagePublicKeyEnvironmentVariable, ServicePackagePublicKeyBase64EnvironmentVariable);

    private static string? ResolvePemEnvironment(string pemName, string base64Name)
    {
        var pem = Environment.GetEnvironmentVariable(pemName);
        if (!string.IsNullOrWhiteSpace(pem))
        {
            return NormalizePem(pem);
        }

        var base64 = Environment.GetEnvironmentVariable(base64Name);
        return string.IsNullOrWhiteSpace(base64)
            ? null
            : Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static string? NormalizePem(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);

    private static bool IsDevelopmentPlaceholder(string value) =>
        value.Trim().Equals("dev-placeholder", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("test-placeholder", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("integration-test", StringComparison.OrdinalIgnoreCase);

    private static bool AllowUnsignedForDevelopment() =>
        string.Equals(
            Environment.GetEnvironmentVariable(AllowUnsignedEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record ServicePackageSignature(
        string Algorithm,
        string KeyId,
        string SignedFile,
        string Value);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdRegex();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[a-fA-F0-9]{4,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex GlyphIconRegex();
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExApp.Core.Updates;

internal static class AppUpdateSignatureVerifier
{
    private const string Algorithm = "ecdsa-p256-sha256";
    private const string PublicKeyEnvironmentVariable = "EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM";
    private const string PublicKeyBase64EnvironmentVariable = "EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_BASE64";
    private const string AllowUnsignedEnvironmentVariable = "EXAPP_ALLOW_UNSIGNED_UPDATE_MANIFEST";
    private const string PublicKeyMetadataKey = "ExAppUpdateSigningPublicKeyPem";

    public static void Verify(string manifestJson, AppReleaseManifest manifest, bool requireSignature)
    {
        if (manifest.Signature is null)
        {
            if (requireSignature && !AllowUnsignedForDevelopment())
            {
                throw new InvalidOperationException("ExApp update manifest is not signed.");
            }

            return;
        }

        if (!manifest.Signature.Algorithm.Equals(Algorithm, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.Signature.KeyId) ||
            string.IsNullOrWhiteSpace(manifest.Signature.Value))
        {
            throw new InvalidOperationException("ExApp update manifest signature is invalid.");
        }

        var publicKeyPem = ResolvePublicKeyPem();
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new InvalidOperationException("ExApp update signing public key is not configured.");
        }

        using var document = JsonDocument.Parse(manifestJson);
        var canonicalPayload = CanonicalizeWithoutSignature(document.RootElement);
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var signature = Convert.FromBase64String(manifest.Signature.Value);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("ExApp update manifest signature verification failed.");
        }
    }

    internal static string CanonicalizeWithoutSignature(JsonElement root) =>
        Canonicalize(root, skipSignatureProperty: true);

    private static string Canonicalize(JsonElement element, bool skipSignatureProperty)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => CanonicalizeObject(element, skipSignatureProperty),
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(item => Canonicalize(item, skipSignatureProperty: false))) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => throw new InvalidOperationException("Unsupported JSON value in update manifest.")
        };
    }

    private static string CanonicalizeObject(JsonElement element, bool skipSignatureProperty)
    {
        var properties = element.EnumerateObject()
            .Where(property => !skipSignatureProperty ||
                               !property.NameEquals("signature"))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonicalize(property.Value, skipSignatureProperty: false));

        return "{" + string.Join(",", properties) + "}";
    }

    private static string? ResolvePublicKeyPem()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(PublicKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return NormalizePem(fromEnvironment);
        }

        var fromBase64Environment = Environment.GetEnvironmentVariable(PublicKeyBase64EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromBase64Environment))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(fromBase64Environment));
        }

        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(PublicKeyMetadataKey, StringComparison.Ordinal))
            ?.Value;
    }

    private static string NormalizePem(string value) =>
        value.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);

    private static bool AllowUnsignedForDevelopment() =>
        string.Equals(
            Environment.GetEnvironmentVariable(AllowUnsignedEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);
}

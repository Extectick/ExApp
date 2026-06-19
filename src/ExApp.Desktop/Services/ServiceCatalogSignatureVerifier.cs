using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ExApp.Core.Updates;

namespace ExApp.Desktop.Services;

internal static class ServiceCatalogSignatureVerifier
{
    private const string Algorithm = "ecdsa-p256-sha256";
    private const string ServicePublicKeyEnvironmentVariable = "EXAPP_SERVICE_CATALOG_PUBLIC_KEY_PEM";
    private const string ServicePublicKeyBase64EnvironmentVariable = "EXAPP_SERVICE_CATALOG_PUBLIC_KEY_BASE64";
    private const string AppUpdatePublicKeyEnvironmentVariable = "EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_PEM";
    private const string AppUpdatePublicKeyBase64EnvironmentVariable = "EXAPP_UPDATE_MANIFEST_PUBLIC_KEY_BASE64";
    private const string AllowUnsignedEnvironmentVariable = "EXAPP_ALLOW_UNSIGNED_SERVICE_CATALOG";
    private const string AppUpdatePublicKeyMetadataKey = "ExAppUpdateSigningPublicKeyPem";

    public static void Verify(string catalogJson, ServiceCatalog catalog, bool requireSignature)
    {
        if (IsDevelopmentPlaceholder(catalog.Signature))
        {
            if (requireSignature && !AllowUnsignedForDevelopment())
            {
                throw new InvalidOperationException("Service catalog is not signed.");
            }

            return;
        }

        if (!catalog.Signature.Algorithm.Equals(Algorithm, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(catalog.Signature.KeyId) ||
            string.IsNullOrWhiteSpace(catalog.Signature.Value))
        {
            throw new InvalidOperationException("Service catalog signature is invalid.");
        }

        var publicKeyPem = ResolvePublicKeyPem();
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new InvalidOperationException("Service catalog signing public key is not configured.");
        }

        using var document = JsonDocument.Parse(catalogJson);
        var canonicalPayload = Canonicalize(document.RootElement, skipSignatureProperty: true);
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var signature = Convert.FromBase64String(catalog.Signature.Value);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("Service catalog signature verification failed.");
        }
    }

    private static bool IsDevelopmentPlaceholder(ServiceCatalogSignature signature) =>
        signature.Algorithm.Equals("dev-placeholder", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(signature.KeyId) &&
        !string.IsNullOrWhiteSpace(signature.Value);

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
            _ => throw new InvalidOperationException("Unsupported JSON value in service catalog.")
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
        var serviceKey = Environment.GetEnvironmentVariable(ServicePublicKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(serviceKey))
        {
            return NormalizePem(serviceKey);
        }

        var serviceKeyBase64 = Environment.GetEnvironmentVariable(ServicePublicKeyBase64EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(serviceKeyBase64))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(serviceKeyBase64));
        }

        var appUpdateKey = Environment.GetEnvironmentVariable(AppUpdatePublicKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(appUpdateKey))
        {
            return NormalizePem(appUpdateKey);
        }

        var appUpdateKeyBase64 = Environment.GetEnvironmentVariable(AppUpdatePublicKeyBase64EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(appUpdateKeyBase64))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(appUpdateKeyBase64));
        }

        return typeof(AppUpdateClient).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(AppUpdatePublicKeyMetadataKey, StringComparison.Ordinal))
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

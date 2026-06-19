param(
    [string]$Path = "artifacts/app/exapp-update.json",
    [string]$PublicKeyPem,
    [string]$PublicKeyBase64
)

$ErrorActionPreference = "Stop"

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExApp.Tools;

public static class AppUpdateManifestVerifier
{
    public static void Verify(string manifestJson, string publicKeyPem)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("signature", out var signatureElement))
        {
            throw new InvalidOperationException("Update manifest signature is missing.");
        }

        var algorithm = signatureElement.GetProperty("algorithm").GetString();
        var value = signatureElement.GetProperty("value").GetString();
        if (!string.Equals(algorithm, "ecdsa-p256-sha256", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Update manifest signature is invalid.");
        }

        var canonical = Canonicalize(root, skipSignatureProperty: true);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        if (!ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromBase64String(value),
                HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("Update manifest signature verification failed.");
        }
    }

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
}
'@

if (-not $PublicKeyPem -and $PublicKeyBase64) {
    $PublicKeyPem = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($PublicKeyBase64))
}

if (-not $PublicKeyPem) {
    throw "Application update manifest public key was not provided."
}

$manifestJson = Get-Content -Raw (Resolve-Path $Path)
[ExApp.Tools.AppUpdateManifestVerifier]::Verify($manifestJson, $PublicKeyPem)
Write-Output "Application update manifest signature is valid."

param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$PrivateKeyPem,
    [string]$PrivateKeyBase64,
    [string]$KeyId = "exapp-service-package-2026",
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

if (-not $PrivateKeyPem -and $PrivateKeyBase64) {
    $PrivateKeyPem = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PrivateKeyBase64))
}

$resolvedPath = Resolve-Path $Path
if ([string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
    if ($RequireSignature) {
        throw "Service package signing key was not provided."
    }

    Set-Content -Encoding ASCII -Path (Join-Path $resolvedPath "signature.sig") -Value "dev-placeholder"
    Write-Warning "No service package signing key was provided. Package will use a development placeholder signature."
    return
}

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class ExAppServicePackageSigner
{
    public static void Sign(string packageDirectory, string privateKeyPem, string keyId)
    {
        var checksumsPath = Path.Combine(packageDirectory, "checksums.json");
        if (!File.Exists(checksumsPath))
        {
            throw new InvalidOperationException("checksums.json was not found.");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = Convert.ToBase64String(ecdsa.SignData(File.ReadAllBytes(checksumsPath), HashAlgorithmName.SHA256));
        var node = new JsonObject
        {
            ["algorithm"] = "ecdsa-p256-sha256",
            ["keyId"] = keyId,
            ["signedFile"] = "checksums.json",
            ["value"] = signature
        };
        File.WriteAllText(
            Path.Combine(packageDirectory, "signature.sig"),
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
'@

[ExAppServicePackageSigner]::Sign($resolvedPath, $PrivateKeyPem, $KeyId)
Write-Output (Join-Path $resolvedPath "signature.sig")

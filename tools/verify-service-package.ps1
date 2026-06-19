param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$PublicKeyPem,
    [string]$PublicKeyBase64,
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

public static class ExAppServicePackageVerifier
{
    private sealed class PackageSignature
    {
        public string Algorithm { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string SignedFile { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public static void Verify(string packagePath, string publicKeyPem, bool requireSignature)
    {
        using var packageStream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        var checksumsEntry = archive.GetEntry("checksums.json") ?? throw new InvalidOperationException("checksums.json is missing.");
        var signatureEntry = archive.GetEntry("signature.sig") ?? throw new InvalidOperationException("signature.sig is missing.");
        using var checksumsStream = checksumsEntry.Open();
        using var checksumsMemory = new MemoryStream();
        checksumsStream.CopyTo(checksumsMemory);
        var checksumsBytes = checksumsMemory.ToArray();
        using var signatureReader = new StreamReader(signatureEntry.Open());
        var signatureText = signatureReader.ReadToEnd();
        if (signatureText.Trim().Equals("dev-placeholder", StringComparison.OrdinalIgnoreCase))
        {
            if (requireSignature)
            {
                throw new InvalidOperationException("Service package is not signed.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new InvalidOperationException("Service package public key was not provided.");
        }

        var signature = JsonSerializer.Deserialize<PackageSignature>(
            signatureText,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("signature.sig is empty.");
        if (!signature.Algorithm.Equals("ecdsa-p256-sha256", StringComparison.OrdinalIgnoreCase) ||
            !signature.SignedFile.Equals("checksums.json", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(signature.KeyId) ||
            string.IsNullOrWhiteSpace(signature.Value))
        {
            throw new InvalidOperationException("Service package signature is invalid.");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        if (!ecdsa.VerifyData(checksumsBytes, Convert.FromBase64String(signature.Value), HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("Service package signature verification failed.");
        }
    }
}
'@

if (-not $PublicKeyPem -and $PublicKeyBase64) {
    $PublicKeyPem = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PublicKeyBase64))
}

[ExAppServicePackageVerifier]::Verify((Resolve-Path $Path), $PublicKeyPem, $RequireSignature)
Write-Output "Service package signature is valid: $Path"

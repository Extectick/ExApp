param(
    [string]$Path = "artifacts/app/exapp-update.json",
    [string]$PrivateKeyPem,
    [string]$PrivateKeyBase64,
    [string]$KeyId = "exapp-app-update-2026",
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExApp.Tools;

public static class AppUpdateManifestSigner
{
    public static string SignAndAttach(string manifestJson, string privateKeyPem, string keyId)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var canonical = CanonicalizeWithoutSignature(document.RootElement);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));

        var node = JsonNode.Parse(manifestJson) as JsonObject
            ?? throw new InvalidOperationException("Update manifest root must be an object.");
        node.Remove("signature");
        node["signature"] = new JsonObject
        {
            ["algorithm"] = "ecdsa-p256-sha256",
            ["keyId"] = keyId,
            ["value"] = signature
        };

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string CanonicalizeWithoutSignature(JsonElement root) =>
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
}
'@

function ConvertTo-CanonicalJson {
    param([object]$Value, [bool]$SkipSignature = $false)

    if ($null -eq $Value) {
        return "null"
    }

    if ($Value -is [string]) {
        return ConvertTo-JsonString -Value ([string]$Value)
    }

    if ($Value -is [datetime]) {
        return ConvertTo-JsonString -Value $Value.ToUniversalTime().ToString("o")
    }

    if ($Value -is [datetimeoffset]) {
        return ConvertTo-JsonString -Value $Value.ToUniversalTime().ToString("o")
    }

    if ($Value -is [bool]) {
        return $(if ($Value) { "true" } else { "false" })
    }

    if ($Value -is [int] -or
        $Value -is [long] -or
        $Value -is [double] -or
        $Value -is [decimal]) {
        return [System.Convert]::ToString($Value, [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($Value -is [System.Collections.IEnumerable] -and
        $Value -isnot [System.Collections.IDictionary] -and
        $Value -isnot [pscustomobject]) {
        $items = @()
        foreach ($item in $Value) {
            $items += ConvertTo-CanonicalJson -Value $item
        }

        return "[" + ($items -join ",") + "]"
    }

    $properties = @()
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            if ($SkipSignature -and $key -eq "signature") {
                continue
            }

            $properties += [pscustomobject]@{ Name = [string]$key; Value = $Value[$key] }
        }
    }
    else {
        foreach ($property in $Value.PSObject.Properties) {
            if ($SkipSignature -and $property.Name -eq "signature") {
                continue
            }

            $properties += [pscustomobject]@{ Name = $property.Name; Value = $property.Value }
        }
    }

    $parts = @()
    foreach ($property in @($properties | Sort-Object Name)) {
        $parts += (ConvertTo-JsonString -Value $property.Name) + ":" + (ConvertTo-CanonicalJson -Value $property.Value)
    }

    return "{" + ($parts -join ",") + "}"
}

function ConvertTo-JsonString {
    param([string]$Value)
    return ConvertTo-Json -Compress -InputObject $Value
}

$resolvedPath = Resolve-Path $Path
if (-not $PrivateKeyPem -and $PrivateKeyBase64) {
    $PrivateKeyPem = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($PrivateKeyBase64))
}

if (-not $PrivateKeyPem) {
    if ($RequireSignature) {
        throw "Application update manifest signing is required, but no private key was provided."
    }

    Write-Warning "No application update manifest signing key was provided. Manifest will remain unsigned."
    return
}

$manifestJson = Get-Content -Raw $resolvedPath
$signedJson = [ExApp.Tools.AppUpdateManifestSigner]::SignAndAttach($manifestJson, $PrivateKeyPem, $KeyId)
$signedJson | Set-Content -Encoding UTF8 $resolvedPath
Write-Output $resolvedPath

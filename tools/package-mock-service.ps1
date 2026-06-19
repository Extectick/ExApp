param(
    [string]$Configuration = "Debug",
    [string]$OutputDirectory = "artifacts",
    [string]$ServicePackageSigningPrivateKeyPem,
    [string]$ServicePackageSigningPrivateKeyBase64,
    [string]$ServicePackageSigningKeyId = "exapp-service-package-2026",
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "package-service.ps1") `
    -ServiceDirectory "services\MockService" `
    -Configuration $Configuration `
    -OutputDirectory $OutputDirectory `
    -ServicePackageSigningPrivateKeyPem $ServicePackageSigningPrivateKeyPem `
    -ServicePackageSigningPrivateKeyBase64 $ServicePackageSigningPrivateKeyBase64 `
    -ServicePackageSigningKeyId $ServicePackageSigningKeyId `
    -RequireSignature:$RequireSignature

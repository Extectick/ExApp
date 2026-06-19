param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,
    [Parameter(Mandatory = $true)]
    [string]$Repository,
    [ValidateSet("stable", "beta")]
    [string]$Channel = "stable",
    [string]$ArtifactsDirectory = "artifacts/app",
    [string]$OutputPath = "artifacts/app/exapp-update.json",
    [string]$DeltaPackagePath,
    [string]$DeltaBaseVersion
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsRoot = Join-Path $repoRoot $ArtifactsDirectory
$packageName = "exapp-$Version-win-x64.zip"
$packagePath = Join-Path $artifactsRoot $packageName
if (-not (Test-Path $packagePath -PathType Leaf)) {
    throw "Application package '$packageName' was not found."
}

$hash = (Get-FileHash -Algorithm SHA256 $packagePath).Hash.ToLowerInvariant()
$size = (Get-Item $packagePath).Length
$manifest = [ordered]@{
    manifestVersion = 1
    version = $Version
    channel = $Channel
    publishedAt = (Get-Date).ToUniversalTime().ToString("o")
    releaseNotes = $null
    package = [ordered]@{
        url = "https://github.com/$Repository/releases/download/$ReleaseTag/$packageName"
        sha256 = $hash
        size = $size
    }
}

if ($DeltaPackagePath) {
    $resolvedDeltaPackagePath = Resolve-Path $DeltaPackagePath
    $deltaName = Split-Path $resolvedDeltaPackagePath -Leaf
    $deltaHash = (Get-FileHash -Algorithm SHA256 $resolvedDeltaPackagePath).Hash.ToLowerInvariant()
    $deltaSize = (Get-Item $resolvedDeltaPackagePath).Length
    $deltaManifestPath = Join-Path (Split-Path $resolvedDeltaPackagePath -Parent) "delta-work\delta\app-delta.json"
    $changedFiles = 0
    $deletedFiles = 0
    if (Test-Path $deltaManifestPath) {
        $deltaManifest = Get-Content -Raw $deltaManifestPath | ConvertFrom-Json
        $changedFiles = @($deltaManifest.files).Count
        $deletedFiles = @($deltaManifest.delete).Count
    }

    $manifest["delta"] = [ordered]@{
        baseVersion = $DeltaBaseVersion
        url = "https://github.com/$Repository/releases/download/$ReleaseTag/$deltaName"
        sha256 = $deltaHash
        size = $deltaSize
        changedFiles = $changedFiles
        deletedFiles = $deletedFiles
    }
}

$resolvedOutput = Join-Path $repoRoot $OutputPath
New-Item -ItemType Directory -Force (Split-Path $resolvedOutput -Parent) | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $resolvedOutput
$hash | Set-Content -Encoding ASCII "$packagePath.sha256"
$size | Set-Content -Encoding ASCII "$packagePath.size"
if ($DeltaPackagePath) {
    $deltaPath = Resolve-Path $DeltaPackagePath
    (Get-FileHash -Algorithm SHA256 $deltaPath).Hash.ToLowerInvariant() | Set-Content -Encoding ASCII "$deltaPath.sha256"
    (Get-Item $deltaPath).Length | Set-Content -Encoding ASCII "$deltaPath.size"
}
Write-Output $resolvedOutput

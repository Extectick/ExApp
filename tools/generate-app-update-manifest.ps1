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
    [string]$OutputPath = "artifacts/app/exapp-update.json"
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

$resolvedOutput = Join-Path $repoRoot $OutputPath
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $resolvedOutput
$hash | Set-Content -Encoding ASCII "$packagePath.sha256"
$size | Set-Content -Encoding ASCII "$packagePath.size"
Write-Output $resolvedOutput

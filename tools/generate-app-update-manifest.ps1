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
    [string[]]$DeltaPackagePath,
    [string[]]$DeltaBaseVersion
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
    if ($DeltaBaseVersion.Count -ne $DeltaPackagePath.Count) {
        throw "DeltaPackagePath and DeltaBaseVersion must have the same number of entries."
    }

    $deltas = @()
    for ($index = 0; $index -lt $DeltaPackagePath.Count; $index++) {
        $resolvedDeltaPackagePath = Resolve-Path $DeltaPackagePath[$index]
        $deltaName = Split-Path $resolvedDeltaPackagePath -Leaf
        $deltaHash = (Get-FileHash -Algorithm SHA256 $resolvedDeltaPackagePath).Hash.ToLowerInvariant()
        $deltaSize = (Get-Item $resolvedDeltaPackagePath).Length
        if ($deltaSize -ge $size) {
            Write-Warning "Skipping app delta '$deltaName' because it is not smaller than the full package."
            Remove-Item -Force $resolvedDeltaPackagePath, "$resolvedDeltaPackagePath.sha256", "$resolvedDeltaPackagePath.size" -ErrorAction SilentlyContinue
            continue
        }

        $deltaInspectRoot = Join-Path (Split-Path $resolvedDeltaPackagePath -Parent) "inspect-$($deltaName -replace '[^a-zA-Z0-9.-]', '-')"
        $changedFiles = 0
        $patchedFiles = 0
        $deletedFiles = 0
        try {
            Remove-Item -Recurse -Force $deltaInspectRoot -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $deltaInspectRoot | Out-Null
            Expand-Archive -Path $resolvedDeltaPackagePath -DestinationPath $deltaInspectRoot -Force
            $deltaManifestPath = Join-Path $deltaInspectRoot "app-delta.json"
            if (-not (Test-Path $deltaManifestPath)) {
                throw "Delta manifest app-delta.json was not found in '$deltaName'."
            }

            $deltaManifest = Get-Content -Raw $deltaManifestPath | ConvertFrom-Json
            $changedFiles = @($deltaManifest.files).Count
            $patchedFiles = @($deltaManifest.patches).Count
            $deletedFiles = @($deltaManifest.delete).Count
        }
        finally {
            Remove-Item -Recurse -Force $deltaInspectRoot -ErrorAction SilentlyContinue
        }

        $deltas += [ordered]@{
            baseVersion = $DeltaBaseVersion[$index]
            url = "https://github.com/$Repository/releases/download/$ReleaseTag/$deltaName"
            sha256 = $deltaHash
            size = $deltaSize
            changedFiles = $changedFiles
            patchedFiles = $patchedFiles
            deletedFiles = $deletedFiles
        }
    }

    $deltas = @($deltas | Sort-Object { $_.size })
    if ($deltas.Count -gt 0) {
        $manifest["delta"] = $deltas[0]
        $manifest["deltas"] = $deltas
    }
}

$resolvedOutput = Join-Path $repoRoot $OutputPath
New-Item -ItemType Directory -Force (Split-Path $resolvedOutput -Parent) | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $resolvedOutput
$hash | Set-Content -Encoding ASCII "$packagePath.sha256"
$size | Set-Content -Encoding ASCII "$packagePath.size"
if ($DeltaPackagePath) {
    foreach ($path in $DeltaPackagePath) {
        $deltaPath = Resolve-Path $path -ErrorAction SilentlyContinue
        if (-not $deltaPath) {
            continue
        }

        (Get-FileHash -Algorithm SHA256 $deltaPath).Hash.ToLowerInvariant() | Set-Content -Encoding ASCII "$deltaPath.sha256"
        (Get-Item $deltaPath).Length | Set-Content -Encoding ASCII "$deltaPath.size"
    }
}
Write-Output $resolvedOutput

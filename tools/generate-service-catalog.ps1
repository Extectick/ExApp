param(
    [string]$CatalogPath = "catalog/services.stable.json",
    [string]$ArtifactsDirectory = "artifacts",
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,
    [Parameter(Mandatory = $true)]
    [string]$Repository,
    [string]$OutputPath = "artifacts/services.stable.json"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

$resolvedCatalogPath = Resolve-RepoPath $CatalogPath
$resolvedArtifactsDirectory = Resolve-RepoPath $ArtifactsDirectory
$resolvedOutputPath = Resolve-RepoPath $OutputPath
$catalog = Get-Content -Raw $resolvedCatalogPath | ConvertFrom-Json -Depth 20
$ids = @{}
$availableCount = 0

foreach ($service in $catalog.services) {
    if ([string]::IsNullOrWhiteSpace($service.id) -or
        [string]::IsNullOrWhiteSpace($service.version)) {
        throw "A catalog service has incomplete identity metadata."
    }

    if ($ids.ContainsKey($service.id)) {
        throw "Duplicate service id '$($service.id)' in catalog."
    }
    $ids[$service.id] = $true

    if ($service.status -ne "available") {
        continue
    }

    $availableCount++
    $platformToken = if ($service.platform -eq "windows") { "win" } else { $service.platform }
    $packageName = "$($service.id)-$($service.version)-$platformToken-$($service.architecture).svcpkg"
    $packagePath = Join-Path $resolvedArtifactsDirectory $packageName
    if (-not (Test-Path $packagePath -PathType Leaf)) {
        throw "Package '$packageName' for service '$($service.id)' was not generated."
    }

    $hash = (Get-FileHash -Algorithm SHA256 $packagePath).Hash.ToLowerInvariant()
    $size = (Get-Item $packagePath).Length
    $hash | Set-Content -Encoding ASCII (Join-Path $resolvedArtifactsDirectory "$packageName.sha256")
    $size | Set-Content -Encoding ASCII (Join-Path $resolvedArtifactsDirectory "$packageName.size")

    if ($null -eq $service.package) {
        $service.package = [pscustomobject]@{
            url = ""
            sha256 = ""
            size = 0
        }
    }
    $service.package.url = "https://github.com/$Repository/releases/download/$ReleaseTag/$packageName"
    $service.package.sha256 = $hash
    $service.package.size = [int64]$size
}

if ($availableCount -eq 0) {
    throw "The release catalog has no available services."
}

$catalog.updatedAt = (Get-Date).ToUniversalTime().ToString("o")
$catalog.signature.algorithm = "dev-placeholder"
$catalog.signature.keyId = "exapp-dev-2026"
$catalog.signature.value = "unsigned-dev-catalog"

New-Item -ItemType Directory -Force (Split-Path $resolvedOutputPath) | Out-Null
$catalog | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $resolvedOutputPath
Write-Output $resolvedOutputPath

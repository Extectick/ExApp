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

foreach ($manifestPath in Get-ChildItem -Path (Join-Path $repoRoot "services") -Filter "service.manifest.json" -Recurse -File) {
    $manifest = Get-Content -Raw $manifestPath.FullName | ConvertFrom-Json -Depth 20
    $platformToken = if ($manifest.platform -eq "windows") { "win" } else { $manifest.platform }
    $packageName = "$($manifest.id)-$($manifest.version)-$platformToken-$($manifest.architecture).svcpkg"
    $packagePath = Join-Path $resolvedArtifactsDirectory $packageName
    if (-not (Test-Path $packagePath -PathType Leaf)) {
        continue
    }

    $existing = @($catalog.services | Where-Object { $_.id -eq $manifest.id }) | Select-Object -First 1
    if ($existing) {
        $existing.name = $manifest.name
        $existing.description = $manifest.description
        if ($manifest.icon) {
            if ($existing.PSObject.Properties.Name -contains "icon") {
                $existing.icon = $manifest.icon
            }
            else {
                $existing | Add-Member -NotePropertyName icon -NotePropertyValue $manifest.icon
            }
        }
        $existing.version = $manifest.version
        $existing.publisher = $manifest.publisher
        $existing.category = $manifest.category
        $existing.platform = $manifest.platform
        $existing.architecture = $manifest.architecture
        $existing.status = "available"
        $existing.permissions = @($manifest.permissions)
    }
    else {
        $catalog.services += [pscustomobject]@{
            id = $manifest.id
            name = $manifest.name
            description = $manifest.description
            icon = $manifest.icon
            version = $manifest.version
            publisher = $manifest.publisher
            category = $manifest.category
            platform = $manifest.platform
            architecture = $manifest.architecture
            status = "available"
            permissions = @($manifest.permissions)
        }
    }
}

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
        $service | Add-Member -NotePropertyName package -NotePropertyValue ([pscustomobject]@{
            url = ""
            sha256 = ""
            size = 0
        }) -Force
    }
    $service.package.url = "https://github.com/$Repository/releases/download/$ReleaseTag/$packageName"
    $service.package.sha256 = $hash
    $service.package.size = [int64]$size

    $deltaPackages = @(Get-ChildItem -Path $resolvedArtifactsDirectory -Filter "$($service.id)-delta-*-to-$($service.version)-$platformToken-$($service.architecture).svcdelta" -File -ErrorAction SilentlyContinue)
    if ($deltaPackages.Count -gt 0) {
        $deltas = @()
        foreach ($deltaPackage in $deltaPackages) {
            $deltaWorkRoot = Join-Path $resolvedArtifactsDirectory "catalog-delta-$($service.id)-$($deltaPackage.BaseName)"
            Remove-Item -Recurse -Force $deltaWorkRoot -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $deltaWorkRoot | Out-Null
            try {
                Expand-Archive -Path $deltaPackage.FullName -DestinationPath $deltaWorkRoot -Force
                $deltaManifest = Get-Content -Raw (Join-Path $deltaWorkRoot "service-delta.json") | ConvertFrom-Json
                if ($deltaManifest.serviceId -ne $service.id -or $deltaManifest.version -ne $service.version) {
                    throw "Delta package '$($deltaPackage.Name)' does not match service '$($service.id)' $($service.version)."
                }

                $deltaHash = (Get-FileHash -Algorithm SHA256 $deltaPackage.FullName).Hash.ToLowerInvariant()
                $deltaSize = $deltaPackage.Length
                if ($deltaSize -ge $size) {
                    Write-Warning "Skipping service delta '$($deltaPackage.Name)' because it is not smaller than the full package."
                    Remove-Item -Force $deltaPackage.FullName, "$($deltaPackage.FullName).sha256", "$($deltaPackage.FullName).size" -ErrorAction SilentlyContinue
                    continue
                }

                $deltaHash | Set-Content -Encoding ASCII (Join-Path $resolvedArtifactsDirectory "$($deltaPackage.Name).sha256")
                $deltaSize | Set-Content -Encoding ASCII (Join-Path $resolvedArtifactsDirectory "$($deltaPackage.Name).size")
                $deltas += [pscustomobject]@{
                    baseVersion = $deltaManifest.baseVersion
                    url = "https://github.com/$Repository/releases/download/$ReleaseTag/$($deltaPackage.Name)"
                    sha256 = $deltaHash
                    size = [int64]$deltaSize
                    changedFiles = @($deltaManifest.files).Count
                    patchedFiles = @($deltaManifest.patches).Count
                    deletedFiles = @($deltaManifest.delete).Count
                }
            }
            finally {
                Remove-Item -Recurse -Force $deltaWorkRoot -ErrorAction SilentlyContinue
            }
        }

        $deltas = @($deltas | Sort-Object size)
        if ($deltas.Count -gt 0) {
            $service | Add-Member -NotePropertyName delta -NotePropertyValue $deltas[0] -Force
            $service | Add-Member -NotePropertyName deltas -NotePropertyValue $deltas -Force
        }
        else {
            if ($service.PSObject.Properties.Name -contains "delta") {
                $service.PSObject.Properties.Remove("delta")
            }

            if ($service.PSObject.Properties.Name -contains "deltas") {
                $service.PSObject.Properties.Remove("deltas")
            }
        }
    }
    else {
        if ($service.PSObject.Properties.Name -contains "delta") {
            $service.PSObject.Properties.Remove("delta")
        }

        if ($service.PSObject.Properties.Name -contains "deltas") {
            $service.PSObject.Properties.Remove("deltas")
        }
    }
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

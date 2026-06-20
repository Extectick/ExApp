param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceDirectory,

    [string]$Configuration = "Debug",
    [string]$OutputDirectory = "artifacts",
    [string]$VersionOverride,
    [string]$ServicePackageSigningPrivateKeyPem,
    [string]$ServicePackageSigningPrivateKeyBase64,
    [string]$ServicePackageSigningKeyId = "exapp-service-package-2026",
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath {
    param([string]$PathValue)
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path $repoRoot $PathValue
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$serviceRoot = Resolve-Path (Resolve-RepoPath $ServiceDirectory)
$projectPath = Get-ChildItem -Path $serviceRoot -Filter "*.csproj" -File | Select-Object -First 1
if ($null -eq $projectPath) {
    throw "No .csproj was found in '$serviceRoot'."
}

$manifestPath = Join-Path $serviceRoot "service.manifest.json"
if (-not (Test-Path $manifestPath -PathType Leaf)) {
    throw "service.manifest.json was not found in '$serviceRoot'."
}

$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
if (-not [string]::IsNullOrWhiteSpace($VersionOverride)) {
    $manifest.version = $VersionOverride
}
$publishRoot = Join-Path $serviceRoot "bin\$Configuration\net8.0"
$platformToken = if ($manifest.platform -eq "windows") { "win" } else { $manifest.platform }
$packageId = "$($manifest.id)-$($manifest.version)-$platformToken-$($manifest.architecture)"
$stagingRoot = Join-Path $repoRoot "artifacts\staging\$packageId"
$packageOutputDirectory = Resolve-RepoPath $OutputDirectory
$packagePath = Join-Path $packageOutputDirectory "$packageId.svcpkg"
$zipPath = Join-Path $packageOutputDirectory "$packageId.zip"

dotnet build $projectPath.FullName -c $Configuration | Out-Host

Remove-Item -Recurse -Force -Path $stagingRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $stagingRoot "bin") | Out-Null
New-Item -ItemType Directory -Force -Path $packageOutputDirectory | Out-Null

$manifest | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 -Path (Join-Path $stagingRoot "service.manifest.json")
if ($manifest.icon -and -not $manifest.icon.StartsWith("glyph:", [StringComparison]::OrdinalIgnoreCase)) {
    $iconSource = Join-Path $serviceRoot $manifest.icon
    if (-not (Test-Path $iconSource -PathType Leaf)) {
        throw "Manifest icon '$($manifest.icon)' was not found."
    }

    $iconDestination = Join-Path $stagingRoot $manifest.icon
    New-Item -ItemType Directory -Force -Path (Split-Path $iconDestination) | Out-Null
    Copy-Item -Path $iconSource -Destination $iconDestination -Force
}

Get-ChildItem -Path $publishRoot -File |
    Where-Object {
        $_.Name -like "$($projectPath.BaseName).*" -or
        $_.Extension -in @(".dll", ".json")
    } |
    ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $stagingRoot "bin\$($_.Name)") -Force
    }

$entryExecutable = Join-Path $stagingRoot ($manifest.entry.executable.Replace("/", [IO.Path]::DirectorySeparatorChar))
if (-not (Test-Path $entryExecutable -PathType Leaf)) {
    throw "Entry executable '$($manifest.entry.executable)' was not staged."
}

$checksumsPath = Join-Path $stagingRoot "checksums.json"
$files = Get-ChildItem -Path $stagingRoot -File -Recurse |
    Where-Object { $_.FullName -ne $checksumsPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($stagingRoot, $_.FullName).Replace("\", "/")
        [ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
        }
    }

[ordered]@{
    algorithm = "sha256"
    files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -Path $checksumsPath

& (Join-Path $PSScriptRoot "sign-service-package-directory.ps1") `
    -Path $stagingRoot `
    -PrivateKeyPem $ServicePackageSigningPrivateKeyPem `
    -PrivateKeyBase64 $ServicePackageSigningPrivateKeyBase64 `
    -KeyId $ServicePackageSigningKeyId `
    -RequireSignature:$RequireSignature | Out-Null

Remove-Item -Force -Path $packagePath -ErrorAction SilentlyContinue
Remove-Item -Force -Path $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force
Move-Item -Path $zipPath -Destination $packagePath -Force

Write-Output $packagePath

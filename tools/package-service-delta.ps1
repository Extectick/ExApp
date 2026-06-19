param(
    [Parameter(Mandatory = $true)]
    [string]$BasePackagePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetPackagePath,

    [string]$OutputDirectory = "artifacts"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ExAppDeltaPatchHelper.ps1")

function Normalize-PathToken {
    param([string]$Value)
    return $Value.Replace("\", "/")
}

function Resolve-RepoPath {
    param([string]$PathValue)
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path $repoRoot $PathValue
}

function Try-CreatePatch {
    param(
        [string]$RelativePath,
        [string]$BasePath,
        [string]$TargetPath,
        [object]$TargetFile,
        [string]$DeltaRoot
    )

    $safeName = ($RelativePath -replace '[^a-zA-Z0-9._-]', '_') + ".bin"
    $dataRelativePath = ".patch-data/$safeName"
    $dataPath = Join-Path $DeltaRoot ($dataRelativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))

    $patchJson = [ExApp.Tools.DeltaPatchHelper]::CreatePatch(
        $RelativePath,
        $BasePath,
        $TargetPath,
        $dataPath,
        $dataRelativePath,
        $TargetFile.sha256,
        0.85)
    if ([string]::IsNullOrWhiteSpace($patchJson)) {
        return $null
    }

    return $patchJson | ConvertFrom-Json
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$basePackage = Resolve-Path $BasePackagePath
$targetPackage = Resolve-Path $TargetPackagePath
$outputRoot = Resolve-RepoPath $OutputDirectory
$workRoot = Join-Path $outputRoot "service-delta-work"
$baseRoot = Join-Path $workRoot "base"
$targetRoot = Join-Path $workRoot "target"
$deltaRoot = Join-Path $workRoot "delta"

Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $baseRoot, $targetRoot, $deltaRoot, $outputRoot | Out-Null
Expand-Archive -Path $basePackage -DestinationPath $baseRoot -Force
Expand-Archive -Path $targetPackage -DestinationPath $targetRoot -Force

$baseManifest = Get-Content -Raw (Join-Path $baseRoot "service.manifest.json") | ConvertFrom-Json
$targetManifest = Get-Content -Raw (Join-Path $targetRoot "service.manifest.json") | ConvertFrom-Json
if ($baseManifest.id -ne $targetManifest.id) {
    throw "Base and target packages belong to different services."
}

$baseChecksums = Get-Content -Raw (Join-Path $baseRoot "checksums.json") | ConvertFrom-Json
$targetChecksums = Get-Content -Raw (Join-Path $targetRoot "checksums.json") | ConvertFrom-Json

$baseFiles = @{}
foreach ($file in $baseChecksums.files) {
    $baseFiles[(Normalize-PathToken $file.path)] = $file
}

$targetFiles = @{}
foreach ($file in $targetChecksums.files) {
    $targetFiles[(Normalize-PathToken $file.path)] = $file
}

$changedFiles = @()
$patches = @()
foreach ($path in ($targetFiles.Keys | Sort-Object)) {
    $targetFile = $targetFiles[$path]
    $baseFile = $baseFiles[$path]
    if ($null -eq $baseFile -or $baseFile.sha256 -ne $targetFile.sha256) {
        $source = Join-Path $targetRoot ($path.Replace("/", [IO.Path]::DirectorySeparatorChar))
        $baseSource = Join-Path $baseRoot ($path.Replace("/", [IO.Path]::DirectorySeparatorChar))
        $patch = if ($null -ne $baseFile) {
            Try-CreatePatch -RelativePath $path -BasePath $baseSource -TargetPath $source -TargetFile $targetFile -DeltaRoot $deltaRoot
        } else {
            $null
        }

        if ($null -ne $patch) {
            $patches += $patch
        }
        else {
            $changedFiles += $targetFile
            $destination = Join-Path $deltaRoot ($path.Replace("/", [IO.Path]::DirectorySeparatorChar))
            New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
            Copy-Item $source $destination -Force
        }
    }
}

$deletedFiles = @()
foreach ($path in ($baseFiles.Keys | Sort-Object)) {
    if (-not $targetFiles.ContainsKey($path)) {
        $deletedFiles += $path
    }
}

Copy-Item (Join-Path $targetRoot "service.manifest.json") (Join-Path $deltaRoot "service.manifest.json") -Force
Copy-Item (Join-Path $targetRoot "checksums.json") (Join-Path $deltaRoot "checksums.json") -Force
Copy-Item (Join-Path $targetRoot "signature.sig") (Join-Path $deltaRoot "signature.sig") -Force

[ordered]@{
    manifestVersion = 1
    serviceId = $targetManifest.id
    baseVersion = $baseManifest.version
    version = $targetManifest.version
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    files = $changedFiles
    patches = $patches
    delete = $deletedFiles
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $deltaRoot "service-delta.json")

$platformToken = if ($targetManifest.platform -eq "windows") { "win" } else { $targetManifest.platform }
$deltaName = "$($targetManifest.id)-delta-$($baseManifest.version)-to-$($targetManifest.version)-$platformToken-$($targetManifest.architecture).svcdelta"
$deltaPath = Join-Path $outputRoot $deltaName
Remove-Item -Force $deltaPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $deltaRoot "*") -DestinationPath $deltaPath -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -Path $deltaPath).Hash.ToLowerInvariant()
$size = (Get-Item $deltaPath).Length
$hash | Set-Content -Encoding ASCII "$deltaPath.sha256"
$size | Set-Content -Encoding ASCII "$deltaPath.size"
Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue

[pscustomobject]@{
    Path = $deltaPath
    ServiceId = $targetManifest.id
    BaseVersion = $baseManifest.version
    Version = $targetManifest.version
    ChangedFiles = $changedFiles.Count
    PatchedFiles = $patches.Count
    DeletedFiles = $deletedFiles.Count
    Sha256 = $hash
    Size = $size
} | ConvertTo-Json -Depth 5 | Write-Output

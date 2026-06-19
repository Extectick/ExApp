param(
    [Parameter(Mandatory = $true)]
    [string]$BasePackagePath,

    [Parameter(Mandatory = $true)]
    [string]$BaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PublishDirectory = "artifacts/app/publish/desktop",
    [string]$OutputDirectory = "artifacts/app"
)

$ErrorActionPreference = "Stop"

function Normalize-PathToken {
    param([string]$Value)
    return $Value.Replace("\", "/")
}

function Read-FileManifest {
    param([string]$Root, [string]$VersionValue)

    $manifestPath = Join-Path $Root "app-files.json"
    if (Test-Path $manifestPath) {
        return Get-Content -Raw $manifestPath | ConvertFrom-Json
    }

    $files = Get-ChildItem -Path $Root -Recurse -File |
        Where-Object {
            $relative = Normalize-PathToken ([IO.Path]::GetRelativePath($Root, $_.FullName))
            $relative -ne "app-files.json" -and $relative -ne "app-delta.json"
        } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                path = Normalize-PathToken ([IO.Path]::GetRelativePath($Root, $_.FullName))
                sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
                size = $_.Length
            }
        }

    return [pscustomobject]@{
        manifestVersion = 1
        version = $VersionValue
        generatedAt = (Get-Date).ToUniversalTime().ToString("o")
        files = $files
    }
}

function Resolve-RepoPath {
    param([string]$PathValue)
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path $repoRoot $PathValue
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$basePackage = Resolve-Path $BasePackagePath
$publishRoot = Resolve-Path (Resolve-RepoPath $PublishDirectory)
$outputRoot = Resolve-RepoPath $OutputDirectory
$workRoot = Join-Path $outputRoot "delta-work"
$baseRoot = Join-Path $workRoot "base"
$deltaRoot = Join-Path $workRoot "delta"
$deltaName = "exapp-delta-$BaseVersion-to-$Version-win-x64.zip"
$deltaPath = Join-Path $outputRoot $deltaName

Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $baseRoot, $deltaRoot, $outputRoot | Out-Null
Expand-Archive -Path $basePackage -DestinationPath $baseRoot -Force

$baseManifest = Read-FileManifest -Root $baseRoot -VersionValue $BaseVersion
$targetManifestPath = Join-Path $publishRoot "app-files.json"
if (-not (Test-Path $targetManifestPath)) {
    throw "Target app-files.json was not found. Run package-exapp.ps1 first."
}
$targetManifest = Get-Content -Raw $targetManifestPath | ConvertFrom-Json

$baseFiles = @{}
foreach ($file in $baseManifest.files) {
    $baseFiles[(Normalize-PathToken $file.path)] = $file
}

$targetFiles = @{}
foreach ($file in $targetManifest.files) {
    $targetFiles[(Normalize-PathToken $file.path)] = $file
}

$changedFiles = @()
foreach ($path in ($targetFiles.Keys | Sort-Object)) {
    $targetFile = $targetFiles[$path]
    $baseFile = $baseFiles[$path]
    if ($null -eq $baseFile -or
        $baseFile.sha256 -ne $targetFile.sha256 -or
        [int64]$baseFile.size -ne [int64]$targetFile.size) {
        $changedFiles += $targetFile
        $source = Join-Path $publishRoot ($path.Replace("/", [IO.Path]::DirectorySeparatorChar))
        $destination = Join-Path $deltaRoot ($path.Replace("/", [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item $source $destination -Force
    }
}

$deletedFiles = @()
foreach ($path in ($baseFiles.Keys | Sort-Object)) {
    if (-not $targetFiles.ContainsKey($path)) {
        $deletedFiles += $path
    }
}

Copy-Item $targetManifestPath (Join-Path $deltaRoot "app-files.json") -Force
[ordered]@{
    manifestVersion = 1
    baseVersion = $BaseVersion
    version = $Version
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    files = $changedFiles
    delete = $deletedFiles
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $deltaRoot "app-delta.json")

Remove-Item -Force $deltaPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $deltaRoot "*") -DestinationPath $deltaPath -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -Path $deltaPath).Hash.ToLowerInvariant()
$size = (Get-Item $deltaPath).Length
$hash | Set-Content -Encoding ASCII "$deltaPath.sha256"
$size | Set-Content -Encoding ASCII "$deltaPath.size"

[pscustomobject]@{
    Path = $deltaPath
    BaseVersion = $BaseVersion
    Version = $Version
    ChangedFiles = $changedFiles.Count
    DeletedFiles = $deletedFiles.Count
    Sha256 = $hash
    Size = $size
} | ConvertTo-Json -Depth 5 | Write-Output

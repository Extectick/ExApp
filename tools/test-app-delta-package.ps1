param(
    [Parameter(Mandatory = $true)]
    [string]$BasePackagePath,

    [Parameter(Mandatory = $true)]
    [string]$DeltaPackagePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$TargetVersion,

    [string]$OutputDirectory = "artifacts/app-delta-verify"
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath {
    param([string]$PathValue)
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path $repoRoot $PathValue
}

function Assert-FileManifest {
    param(
        [string]$Root,
        [object]$Manifest
    )

    foreach ($file in @($Manifest.files)) {
        $path = Join-Path $Root ($file.path.Replace("/", [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path $path -PathType Leaf)) {
            throw "Updated file '$($file.path)' is missing."
        }

        $actualSize = (Get-Item $path).Length
        if ($actualSize -ne [int64]$file.size) {
            throw "Updated file '$($file.path)' has size $actualSize, expected $($file.size)."
        }

        $actualHash = (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
        if ($actualHash -ne $file.sha256) {
            throw "Updated file '$($file.path)' has SHA-256 $actualHash, expected $($file.sha256)."
        }
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedBasePackage = Resolve-Path (Resolve-RepoPath $BasePackagePath)
$resolvedDeltaPackage = Resolve-Path (Resolve-RepoPath $DeltaPackagePath)
$resolvedTargetManifest = Resolve-Path (Resolve-RepoPath $TargetManifestPath)
$outputRoot = Resolve-RepoPath $OutputDirectory
$installedRoot = Join-Path $outputRoot "installed"
$stagingRoot = Join-Path $outputRoot "staging"
$runnerRoot = Join-Path $outputRoot "runner"
$updateRoot = Join-Path $outputRoot "state"

try {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $installedRoot, $stagingRoot, $runnerRoot, $updateRoot | Out-Null

    Expand-Archive -Path $resolvedBasePackage -DestinationPath $installedRoot -Force
    Expand-Archive -Path $resolvedDeltaPackage -DestinationPath $stagingRoot -Force

    $updaterRoot = Join-Path $installedRoot "updater"
    $updaterPath = Join-Path $updaterRoot "ExApp.Updater.exe"
    if (-not (Test-Path $updaterPath -PathType Leaf)) {
        throw "Bundled updater was not found in base package '$resolvedBasePackage'."
    }

    Copy-Item -Path (Join-Path $updaterRoot "*") -Destination $runnerRoot -Recurse -Force
    $runnerUpdaterPath = Join-Path $runnerRoot "ExApp.Updater.exe"
    $arguments = @(
        "--staging", $stagingRoot,
        "--target", $installedRoot,
        "--desktop-pid", "999999",
        "--version", $TargetVersion,
        "--no-restart", "true",
        "--update-root", $updateRoot
    )

    $process = Start-Process `
        -FilePath $runnerUpdaterPath `
        -WorkingDirectory $runnerRoot `
        -Wait `
        -PassThru `
        -WindowStyle Hidden `
        -ArgumentList $arguments
    if ($process.ExitCode -ne 0) {
        $logPath = Join-Path $updateRoot "updater.log"
        $logText = if (Test-Path $logPath -PathType Leaf) { Get-Content -Raw $logPath } else { "" }
        throw "Updater exited with code $($process.ExitCode) while verifying '$resolvedDeltaPackage'. $logText"
    }

    $targetManifest = Get-Content -Raw $resolvedTargetManifest | ConvertFrom-Json
    $installedManifestPath = Join-Path $installedRoot "app-files.json"
    if (-not (Test-Path $installedManifestPath -PathType Leaf)) {
        throw "Updated app-files.json was not written."
    }

    $installedManifest = Get-Content -Raw $installedManifestPath | ConvertFrom-Json
    if ($installedManifest.version -ne $TargetVersion) {
        throw "Updated app-files.json version is '$($installedManifest.version)', expected '$TargetVersion'."
    }

    Assert-FileManifest -Root $installedRoot -Manifest $targetManifest

    [pscustomobject]@{
        BasePackage = "$resolvedBasePackage"
        DeltaPackage = "$resolvedDeltaPackage"
        TargetVersion = $TargetVersion
        Verified = $true
    } | ConvertTo-Json -Depth 3 | Write-Output
}
finally {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
}

param(
    [string]$Configuration = "Debug",
    [string]$BaseVersion = "0.1.900",
    [string]$TargetVersion = "0.1.901",
    [string]$OutputDirectory = "artifacts/app-update-e2e",
    [switch]$KeepArtifacts
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
$outputRoot = Resolve-RepoPath $OutputDirectory
$baseOutput = Join-Path $outputRoot "base"
$targetOutput = Join-Path $outputRoot "target"
$deltaOutput = Join-Path $outputRoot "delta"
$installedRoot = Join-Path $outputRoot "installed"
$stagingRoot = Join-Path $outputRoot "staging"
$runnerRoot = Join-Path $outputRoot "runner"
$updateRoot = Join-Path $outputRoot "update-state"

try {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $baseOutput, $targetOutput, $deltaOutput, $installedRoot, $stagingRoot, $runnerRoot, $updateRoot | Out-Null

    $basePackage = & (Join-Path $PSScriptRoot "package-exapp.ps1") `
        -Configuration $Configuration `
        -Version $BaseVersion `
        -OutputDirectory $baseOutput |
        Select-Object -Last 1
    $targetPackage = & (Join-Path $PSScriptRoot "package-exapp.ps1") `
        -Configuration $Configuration `
        -Version $TargetVersion `
        -OutputDirectory $targetOutput |
        Select-Object -Last 1

    $deltaInfoJson = & (Join-Path $PSScriptRoot "package-exapp-delta.ps1") `
        -BasePackagePath $basePackage `
        -BaseVersion $BaseVersion `
        -Version $TargetVersion `
        -PublishDirectory (Join-Path $targetOutput "publish\desktop") `
        -OutputDirectory $deltaOutput
    $deltaInfo = $deltaInfoJson | ConvertFrom-Json

    Expand-Archive -Path $basePackage -DestinationPath $installedRoot -Force
    Expand-Archive -Path $deltaInfo.Path -DestinationPath $stagingRoot -Force

    $installedUpdaterRoot = Join-Path $installedRoot "updater"
    $installedUpdaterPath = Join-Path $installedUpdaterRoot "ExApp.Updater.exe"
    if (-not (Test-Path $installedUpdaterPath -PathType Leaf)) {
        throw "Bundled updater was not found in the base package."
    }

    Copy-Item -Path (Join-Path $installedUpdaterRoot "*") -Destination $runnerRoot -Recurse -Force
    $updaterPath = Join-Path $runnerRoot "ExApp.Updater.exe"
    $process = Start-Process -FilePath $updaterPath -WorkingDirectory (Split-Path $updaterPath -Parent) -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        "--staging", $stagingRoot,
        "--target", $installedRoot,
        "--desktop-pid", "999999",
        "--version", $TargetVersion,
        "--no-restart", "true",
        "--update-root", $updateRoot
    )
    if ($process.ExitCode -ne 0) {
        throw "Updater exited with code $($process.ExitCode)."
    }

    $installedVersion = (Get-Content -Raw (Join-Path $installedRoot "version.txt")).Trim()
    if ($installedVersion -ne $TargetVersion) {
        throw "Installed version is '$installedVersion', expected '$TargetVersion'."
    }

    $targetManifest = Get-Content -Raw (Join-Path $targetOutput "publish\desktop\app-files.json") | ConvertFrom-Json
    $installedManifest = Get-Content -Raw (Join-Path $installedRoot "app-files.json") | ConvertFrom-Json
    if ($installedManifest.version -ne $TargetVersion) {
        throw "Installed app-files.json version is '$($installedManifest.version)', expected '$TargetVersion'."
    }

    Assert-FileManifest -Root $installedRoot -Manifest $targetManifest
    $statePath = Join-Path $updateRoot "update-state.json"
    if (-not (Test-Path $statePath -PathType Leaf)) {
        throw "Updater state was not written."
    }

    $state = Get-Content -Raw $statePath | ConvertFrom-Json
    if ($state.status -ne "installed" -or $state.version -ne $TargetVersion) {
        throw "Updater state is '$($state.status)' for version '$($state.version)', expected installed/$TargetVersion."
    }

    [pscustomobject]@{
        BaseVersion = $BaseVersion
        TargetVersion = $TargetVersion
        DeltaPackage = $deltaInfo.Path
        DeltaSize = $deltaInfo.Size
        ChangedFiles = $deltaInfo.ChangedFiles
        PatchedFiles = $deltaInfo.PatchedFiles
        DeletedFiles = $deltaInfo.DeletedFiles
        InstalledRoot = $installedRoot
    } | ConvertTo-Json -Depth 5 | Write-Output
}
finally {
    if (-not $KeepArtifacts) {
        Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    }
}

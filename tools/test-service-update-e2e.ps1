param(
    [string]$Configuration = "Debug",
    [string]$BaseVersion = "0.1.900",
    [string]$TargetVersion = "0.1.901",
    [string]$ServiceDirectory = "services/MockService",
    [string]$OutputDirectory = "artifacts/service-update-e2e",
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

function Invoke-PackageTool {
    param([string[]]$ToolArguments)

    $output = & dotnet run --project (Join-Path $repoRoot "tools/ExApp.PackageTool/ExApp.PackageTool.csproj") -- @ToolArguments
    if ($LASTEXITCODE -ne 0) {
        throw "ExApp.PackageTool failed with exit code $LASTEXITCODE. Output: $output"
    }

    $text = $output -join [Environment]::NewLine
    $jsonStart = $text.IndexOf("{", [StringComparison]::Ordinal)
    if ($jsonStart -lt 0) {
        throw "ExApp.PackageTool did not return JSON. Output: $text"
    }

    return $text.Substring($jsonStart) | ConvertFrom-Json
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = Resolve-RepoPath $OutputDirectory
$packagesRoot = Join-Path $outputRoot "packages"
$installRoot = Join-Path $outputRoot "installed"

try {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $packagesRoot, $installRoot | Out-Null

    $basePackage = & (Join-Path $PSScriptRoot "package-service.ps1") `
        -ServiceDirectory $ServiceDirectory `
        -Configuration $Configuration `
        -VersionOverride $BaseVersion `
        -OutputDirectory $packagesRoot |
        Select-Object -Last 1

    $targetPackage = & (Join-Path $PSScriptRoot "package-service.ps1") `
        -ServiceDirectory $ServiceDirectory `
        -Configuration $Configuration `
        -VersionOverride $TargetVersion `
        -OutputDirectory $packagesRoot |
        Select-Object -Last 1

    $deltaInfo = & (Join-Path $PSScriptRoot "package-service-delta.ps1") `
        -BasePackagePath $basePackage `
        -TargetPackagePath $targetPackage `
        -OutputDirectory $packagesRoot |
        ConvertFrom-Json

    & (Join-Path $PSScriptRoot "test-service-delta-package.ps1") `
        -BasePackagePath $basePackage `
        -TargetPackagePath $targetPackage `
        -DeltaPackagePath $deltaInfo.Path `
        -OutputDirectory (Join-Path $outputRoot "release-delta-verify") |
        Out-Host

    $install = Invoke-PackageTool @("install", $basePackage, "--root", $installRoot)
    if ($install.version -ne $BaseVersion) {
        throw "Installed service version is '$($install.version)', expected '$BaseVersion'."
    }

    $update = Invoke-PackageTool @("update", $deltaInfo.Path, "--delta", "--root", $installRoot)
    if ($update.version -ne $TargetVersion -or -not $update.appliedDelta) {
        throw "Delta update result is version '$($update.version)' appliedDelta '$($update.appliedDelta)', expected $TargetVersion/true."
    }

    $state = Invoke-PackageTool @("state", $install.id, "--root", $installRoot)
    if ($state.currentVersion -ne $TargetVersion -or $state.previousVersion -ne $BaseVersion) {
        throw "Service state is current '$($state.currentVersion)' previous '$($state.previousVersion)', expected $TargetVersion/$BaseVersion."
    }

    [pscustomobject]@{
        ServiceId = $install.id
        BaseVersion = $BaseVersion
        TargetVersion = $TargetVersion
        DeltaPackage = $deltaInfo.Path
        DeltaSize = $deltaInfo.Size
        ChangedFiles = $deltaInfo.ChangedFiles
        PatchedFiles = $deltaInfo.PatchedFiles
        DeletedFiles = $deltaInfo.DeletedFiles
        InstalledRoot = $installRoot
    } | ConvertTo-Json -Depth 5 | Write-Output
}
finally {
    if (-not $KeepArtifacts) {
        Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    }
}

param(
    [Parameter(Mandatory = $true)]
    [string]$BasePackagePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetPackagePath,

    [Parameter(Mandatory = $true)]
    [string]$DeltaPackagePath,

    [string]$OutputDirectory = "artifacts/service-delta-verify",
    [string]$ServicePackageSigningPublicKeyPem,
    [string]$ServicePackageSigningPublicKeyBase64
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

    $previousPublicKeyPem = [Environment]::GetEnvironmentVariable("EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_PEM")
    $previousPublicKeyBase64 = [Environment]::GetEnvironmentVariable("EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_BASE64")
    try {
        if (-not [string]::IsNullOrWhiteSpace($ServicePackageSigningPublicKeyPem)) {
            [Environment]::SetEnvironmentVariable("EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_PEM", $ServicePackageSigningPublicKeyPem)
        }
        if (-not [string]::IsNullOrWhiteSpace($ServicePackageSigningPublicKeyBase64)) {
            [Environment]::SetEnvironmentVariable("EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_BASE64", $ServicePackageSigningPublicKeyBase64)
        }

        $output = & dotnet run --project (Join-Path $repoRoot "tools/ExApp.PackageTool/ExApp.PackageTool.csproj") -- @ToolArguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "ExApp.PackageTool failed with exit code $LASTEXITCODE. Output: $($output -join [Environment]::NewLine)"
        }

        $text = $output -join [Environment]::NewLine
        $jsonStart = $text.IndexOf("{", [StringComparison]::Ordinal)
        if ($jsonStart -lt 0) {
            throw "ExApp.PackageTool did not return JSON. Output: $text"
        }

        return $text.Substring($jsonStart) | ConvertFrom-Json
    }
    finally {
        [Environment]::SetEnvironmentVariable("EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_PEM", $previousPublicKeyPem)
        [Environment]::SetEnvironmentVariable("EXAPP_SERVICE_PACKAGE_PUBLIC_KEY_BASE64", $previousPublicKeyBase64)
    }
}

function Assert-TargetChecksums {
    param(
        [string]$TargetPackage,
        [string]$InstalledDirectory
    )

    $targetRoot = Join-Path $outputRoot "target"
    Remove-Item -Recurse -Force $targetRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $targetRoot | Out-Null
    Expand-Archive -Path $TargetPackage -DestinationPath $targetRoot -Force
    $checksums = Get-Content -Raw (Join-Path $targetRoot "checksums.json") | ConvertFrom-Json

    foreach ($file in @($checksums.files)) {
        $installedPath = Join-Path $InstalledDirectory ($file.path.Replace("/", [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path $installedPath -PathType Leaf)) {
            throw "Updated service file '$($file.path)' is missing."
        }

        $actualHash = (Get-FileHash -Algorithm SHA256 -Path $installedPath).Hash.ToLowerInvariant()
        if ($actualHash -ne $file.sha256) {
            throw "Updated service file '$($file.path)' has SHA-256 $actualHash, expected $($file.sha256)."
        }
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedBasePackage = Resolve-Path (Resolve-RepoPath $BasePackagePath)
$resolvedTargetPackage = Resolve-Path (Resolve-RepoPath $TargetPackagePath)
$resolvedDeltaPackage = Resolve-Path (Resolve-RepoPath $DeltaPackagePath)
$outputRoot = Resolve-RepoPath $OutputDirectory
$installRoot = Join-Path $outputRoot "installed"

try {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $installRoot | Out-Null

    $install = Invoke-PackageTool @("install", "$resolvedBasePackage", "--root", $installRoot)
    $update = Invoke-PackageTool @("update", "$resolvedDeltaPackage", "--delta", "--root", $installRoot)
    if (-not $update.appliedDelta) {
        throw "PackageTool did not report appliedDelta=true for '$resolvedDeltaPackage'."
    }

    $state = Invoke-PackageTool @("state", $install.id, "--root", $installRoot)
    if ($state.currentVersion -ne $update.version -or $state.previousVersion -ne $install.version) {
        throw "Service state is current '$($state.currentVersion)' previous '$($state.previousVersion)', expected $($update.version)/$($install.version)."
    }

    Assert-TargetChecksums -TargetPackage "$resolvedTargetPackage" -InstalledDirectory $update.installDirectory

    [pscustomobject]@{
        ServiceId = $install.id
        BaseVersion = $install.version
        TargetVersion = $update.version
        DeltaPackage = "$resolvedDeltaPackage"
        Verified = $true
    } | ConvertTo-Json -Depth 3 | Write-Output
}
finally {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
}

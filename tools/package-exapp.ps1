param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0",
    [string]$OutputDirectory = "artifacts/app",
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateBase64,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$AppUpdateSigningPublicKeyPem,
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

function Resolve-RepoPath {
    param([string]$PathValue)
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path $repoRoot $PathValue
}

$outputRoot = Resolve-RepoPath $OutputDirectory
$publishRoot = Join-Path $outputRoot "publish"
$desktopRoot = Join-Path $publishRoot "desktop"
$agentRoot = Join-Path $publishRoot "agent"
$updaterRoot = Join-Path $publishRoot "updater"
$packagePath = Join-Path $outputRoot "exapp-$Version-win-x64.zip"

Remove-Item -Recurse -Force $publishRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $desktopRoot, $agentRoot, $updaterRoot | Out-Null

dotnet publish (Join-Path $repoRoot "src/ExApp.Desktop/ExApp.Desktop.csproj") `
    -c $Configuration -p:Platform=x64 -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishTrimmed=false `
    -p:AppUpdateSigningPublicKeyPem="$AppUpdateSigningPublicKeyPem" `
    -o $desktopRoot | Out-Host
dotnet publish (Join-Path $repoRoot "src/ExApp.Agent/ExApp.Agent.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=false -o $agentRoot | Out-Host
dotnet publish (Join-Path $repoRoot "src/ExApp.Updater/ExApp.Updater.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=false -o $updaterRoot | Out-Host

Copy-Item $agentRoot (Join-Path $desktopRoot "agent") -Recurse -Force
Copy-Item $updaterRoot (Join-Path $desktopRoot "updater") -Recurse -Force
$desktopBuildRoot = Join-Path $repoRoot "src/ExApp.Desktop/bin/x64/$Configuration/net8.0-windows10.0.26100.0/win-x64"
if (Test-Path $desktopBuildRoot) {
    Get-ChildItem -Path $desktopBuildRoot -Include "*.xbf", "*.pri" -Recurse -File |
        Where-Object {
            $relative = [IO.Path]::GetRelativePath($desktopBuildRoot, $_.FullName)
            $relative -notlike "MsixContent\*" -and
            $relative -notlike "embed\*" -and
            $relative -notlike "AppxManifest.xml" -and
            (Split-Path $relative -Leaf) -notlike "Microsoft.*.pri"
        } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($desktopBuildRoot, $_.FullName)
            $target = Join-Path $desktopRoot $relative
            New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
            Copy-Item $_.FullName $target -Force
        }
}
Set-Content -Encoding ASCII (Join-Path $desktopRoot "version.txt") $Version

if ($CertificatePath -or $CertificateBase64 -or $RequireSignature) {
    & (Join-Path $PSScriptRoot "sign-windows-artifacts.ps1") `
        -Path $desktopRoot `
        -CertificatePath $CertificatePath `
        -CertificatePassword $CertificatePassword `
        -CertificateBase64 $CertificateBase64 `
        -TimestampUrl $TimestampUrl `
        -RequireSignature:$RequireSignature | Out-Host
}

$fileManifestPath = Join-Path $desktopRoot "app-files.json"
$files = Get-ChildItem -Path $desktopRoot -Recurse -File |
    Where-Object { $_.FullName -ne $fileManifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($desktopRoot, $_.FullName).Replace("\", "/")
        [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
            size = $_.Length
        }
    }

[ordered]@{
    manifestVersion = 1
    version = $Version
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $fileManifestPath

Remove-Item -Force $packagePath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $desktopRoot "*") -DestinationPath $packagePath -CompressionLevel Optimal
Write-Output $packagePath

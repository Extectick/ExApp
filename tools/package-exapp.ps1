param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0",
    [string]$OutputDirectory = "artifacts/app"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = Join-Path $repoRoot $OutputDirectory
$publishRoot = Join-Path $outputRoot "publish"
$desktopRoot = Join-Path $publishRoot "desktop"
$agentRoot = Join-Path $publishRoot "agent"
$updaterRoot = Join-Path $publishRoot "updater"
$packagePath = Join-Path $outputRoot "exapp-$Version-win-x64.zip"

Remove-Item -Recurse -Force $publishRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $desktopRoot, $agentRoot, $updaterRoot | Out-Null

dotnet publish (Join-Path $repoRoot "src/ExApp.Desktop/ExApp.Desktop.csproj") `
    -c $Configuration -p:Platform=x64 -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishTrimmed=false -o $desktopRoot | Out-Host
dotnet publish (Join-Path $repoRoot "src/ExApp.Agent/ExApp.Agent.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=true -o $agentRoot | Out-Host
dotnet publish (Join-Path $repoRoot "src/ExApp.Updater/ExApp.Updater.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=true -o $updaterRoot | Out-Host

Copy-Item (Join-Path $agentRoot "ExApp.Agent.exe") $desktopRoot -Force
Copy-Item (Join-Path $updaterRoot "ExApp.Updater.exe") $desktopRoot -Force
Set-Content -Encoding ASCII (Join-Path $desktopRoot "version.txt") $Version

Remove-Item -Force $packagePath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $desktopRoot "*") -DestinationPath $packagePath -CompressionLevel Optimal
Write-Output $packagePath

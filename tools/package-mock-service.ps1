param(
    [string]$Configuration = "Debug",
    [string]$OutputDirectory = "artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "services\MockService\ExApp.Service.MockService.csproj"
$publishRoot = Join-Path $repoRoot "services\MockService\bin\$Configuration\net8.0"
$manifestPath = Join-Path $repoRoot "services\MockService\service.manifest.json"
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$platformToken = if ($manifest.platform -eq "windows") { "win" } else { $manifest.platform }
$packageId = "$($manifest.id)-$($manifest.version)-$platformToken-$($manifest.architecture)"
$stagingRoot = Join-Path $repoRoot "artifacts\staging\$packageId"
$packageOutputDirectory = Join-Path $repoRoot $OutputDirectory
$packagePath = Join-Path $packageOutputDirectory "$packageId.svcpkg"
$zipPath = Join-Path $packageOutputDirectory "$packageId.zip"

dotnet build $projectPath -c $Configuration | Out-Host

Remove-Item -Recurse -Force -Path $stagingRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $stagingRoot "bin") | Out-Null
New-Item -ItemType Directory -Force -Path $packageOutputDirectory | Out-Null

Copy-Item -Path $manifestPath -Destination (Join-Path $stagingRoot "service.manifest.json") -Force
if ($manifest.icon -and -not $manifest.icon.StartsWith("glyph:", [StringComparison]::OrdinalIgnoreCase)) {
    $iconSource = Join-Path (Split-Path $manifestPath) $manifest.icon
    if (-not (Test-Path $iconSource -PathType Leaf)) {
        throw "Manifest icon '$($manifest.icon)' was not found."
    }

    $iconDestination = Join-Path $stagingRoot $manifest.icon
    New-Item -ItemType Directory -Force -Path (Split-Path $iconDestination) | Out-Null
    Copy-Item -Path $iconSource -Destination $iconDestination -Force
}
Copy-Item -Path (Join-Path $publishRoot "ExApp.Service.MockService.exe") -Destination (Join-Path $stagingRoot "bin\ExApp.Service.MockService.exe") -Force
Copy-Item -Path (Join-Path $publishRoot "ExApp.Service.MockService.dll") -Destination (Join-Path $stagingRoot "bin\ExApp.Service.MockService.dll") -Force
Copy-Item -Path (Join-Path $publishRoot "ExApp.Service.MockService.deps.json") -Destination (Join-Path $stagingRoot "bin\ExApp.Service.MockService.deps.json") -Force
Copy-Item -Path (Join-Path $publishRoot "ExApp.Service.MockService.runtimeconfig.json") -Destination (Join-Path $stagingRoot "bin\ExApp.Service.MockService.runtimeconfig.json") -Force

$files = Get-ChildItem -Path $stagingRoot -File -Recurse |
    Where-Object { $_.Name -ne "checksums.json" } |
    Sort-Object FullName |
    ForEach-Object {
        $rootWithSeparator = $stagingRoot.TrimEnd("\") + "\"
        $relativePath = $_.FullName.Substring($rootWithSeparator.Length).Replace("\", "/")
        [ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
        }
    }

[ordered]@{
    algorithm = "sha256"
    files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -Path (Join-Path $stagingRoot "checksums.json")

Set-Content -Encoding ASCII -Path (Join-Path $stagingRoot "signature.sig") -Value "dev-placeholder"

Remove-Item -Force -Path $packagePath -ErrorAction SilentlyContinue
Remove-Item -Force -Path $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force
Move-Item -Path $zipPath -Destination $packagePath -Force

Write-Output $packagePath

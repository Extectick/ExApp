param(
    [string]$OutputDirectory = "artifacts/delta-patch-helper"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ExAppDeltaPatchHelper.ps1")

function Resolve-RepoPath {
    param([string]$PathValue)
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path $repoRoot $PathValue
}

function Copy-Range {
    param(
        [IO.Stream]$Source,
        [IO.Stream]$Destination,
        [int64]$Offset,
        [int64]$Length
    )

    $buffer = New-Object byte[] 81920
    $Source.Seek($Offset, [IO.SeekOrigin]::Begin) | Out-Null
    $remaining = $Length
    while ($remaining -gt 0) {
        $readLength = [Math]::Min($buffer.Length, $remaining)
        $read = $Source.Read($buffer, 0, $readLength)
        if ($read -le 0) {
            throw "Patch operation exceeded source length."
        }

        $Destination.Write($buffer, 0, $read)
        $remaining -= $read
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = Resolve-RepoPath $OutputDirectory
$basePath = Join-Path $outputRoot "base.bin"
$targetPath = Join-Path $outputRoot "target.bin"
$patchDataPath = Join-Path $outputRoot "patch-data.bin"
$patchedPath = Join-Path $outputRoot "patched.bin"

try {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $outputRoot | Out-Null

    $baseBytes = New-Object byte[] (512KB)
    for ($index = 0; $index -lt $baseBytes.Length; $index++) {
        $baseBytes[$index] = [byte](($index * 31) % 251)
    }

    $targetBytes = [byte[]]$baseBytes.Clone()
    for ($index = 120000; $index -lt 126000; $index++) {
        $targetBytes[$index] = [byte](255 - ($index % 251))
    }
    for ($index = 300000; $index -lt 302000; $index++) {
        $targetBytes[$index] = [byte](($index * 17) % 251)
    }

    [IO.File]::WriteAllBytes($basePath, $baseBytes)
    [IO.File]::WriteAllBytes($targetPath, $targetBytes)
    $targetHash = (Get-FileHash -Algorithm SHA256 -Path $targetPath).Hash.ToLowerInvariant()

    $patchJson = [ExApp.Tools.DeltaPatchHelper]::CreatePatch(
        "target.bin",
        $basePath,
        $targetPath,
        $patchDataPath,
        ".patch-data/target.bin",
        $targetHash,
        0.85)
    if ([string]::IsNullOrWhiteSpace($patchJson)) {
        throw "Delta patch helper did not create a patch."
    }

    $patch = $patchJson | ConvertFrom-Json
    $operations = @($patch.operations)
    if (($operations | Where-Object { $_.type -eq "copy" }).Count -eq 0 -or
        ($operations | Where-Object { $_.type -eq "data" }).Count -eq 0) {
        throw "Patch must contain both copy and data operations."
    }

    $baseStream = [IO.File]::OpenRead($basePath)
    $dataStream = [IO.File]::OpenRead($patchDataPath)
    $outputStream = [IO.File]::Create($patchedPath)
    try {
        foreach ($operation in $operations) {
            if ($operation.type -eq "copy") {
                Copy-Range -Source $baseStream -Destination $outputStream -Offset ([int64]$operation.offset) -Length ([int64]$operation.length)
            }
            elseif ($operation.type -eq "data") {
                Copy-Range -Source $dataStream -Destination $outputStream -Offset ([int64]$operation.dataOffset) -Length ([int64]$operation.length)
            }
            else {
                throw "Unsupported patch operation '$($operation.type)'."
            }
        }
    }
    finally {
        $outputStream.Dispose()
        $dataStream.Dispose()
        $baseStream.Dispose()
    }

    $patchedHash = (Get-FileHash -Algorithm SHA256 -Path $patchedPath).Hash.ToLowerInvariant()
    if ($patchedHash -ne $targetHash) {
        throw "Patched output SHA-256 does not match target."
    }

    [pscustomobject]@{
        TargetSize = (Get-Item $targetPath).Length
        PatchDataSize = (Get-Item $patchDataPath).Length
        Operations = $operations.Count
        Hash = $patchedHash
    } | ConvertTo-Json -Depth 3 | Write-Output
}
finally {
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
}

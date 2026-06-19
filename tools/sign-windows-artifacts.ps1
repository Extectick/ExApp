param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateBase64,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string[]]$Include = @("*.exe", "*.dll", "*.msi"),
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -Path $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\signtool.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    throw "signtool.exe was not found. Install Windows SDK signing tools."
}

$resolvedPath = Resolve-Path $Path
$certificateFile = $CertificatePath
$temporaryCertificate = $null

if (-not $certificateFile -and $CertificateBase64) {
    $temporaryCertificate = Join-Path ([IO.Path]::GetTempPath()) "exapp-signing-$([Guid]::NewGuid().ToString('N')).pfx"
    [IO.File]::WriteAllBytes($temporaryCertificate, [Convert]::FromBase64String($CertificateBase64))
    $certificateFile = $temporaryCertificate
}

if (-not $certificateFile) {
    if ($RequireSignature) {
        throw "Code signing is required, but no certificate was provided."
    }

    Write-Warning "No signing certificate was provided. Artifacts will remain unsigned."
    return
}

try {
    $signTool = Find-SignTool
    $certificateFile = (Resolve-Path $certificateFile).Path
    $targets = @()
    foreach ($pattern in $Include) {
        if ((Get-Item $resolvedPath).PSIsContainer) {
            $targets += Get-ChildItem -Path $resolvedPath -Recurse -File -Filter $pattern
        }
        elseif ((Split-Path $resolvedPath -Leaf) -like $pattern) {
            $targets += Get-Item $resolvedPath
        }
    }

    $targets = $targets | Sort-Object FullName -Unique
    if ($targets.Count -eq 0) {
        Write-Warning "No signable artifacts were found under $resolvedPath."
        return
    }

    foreach ($target in $targets) {
        Write-Host "Signing $($target.FullName)"
        $arguments = @(
            "sign",
            "/fd", "SHA256",
            "/td", "SHA256",
            "/tr", $TimestampUrl,
            "/f", $certificateFile
        )
        if ($CertificatePassword) {
            $arguments += @("/p", $CertificatePassword)
        }
        $arguments += $target.FullName

        & $signTool @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $($target.FullName)."
        }

        & $signTool verify /pa /all $target.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed for $($target.FullName)."
        }
    }
}
finally {
    if ($temporaryCertificate -and (Test-Path $temporaryCertificate)) {
        Remove-Item -Force $temporaryCertificate
    }
}

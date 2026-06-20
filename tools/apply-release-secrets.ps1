param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [string]$Repository,
    [switch]$UseBase64Secrets,
    [switch]$EnableProductionGuards,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

function Resolve-Repository {
    if (-not [string]::IsNullOrWhiteSpace($Repository)) {
        return $Repository
    }

    $remote = & git remote get-url origin 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remote)) {
        throw "Repository was not provided and could not be resolved from git remote origin."
    }

    $text = $remote.Trim()
    if ($text -match "github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?$") {
        return "$($Matches.owner)/$($Matches.repo)"
    }

    throw "Could not parse GitHub repository from remote '$text'."
}

function Invoke-Gh {
    param([string[]]$Arguments)

    if ($WhatIf) {
        Write-Output ("gh " + ($Arguments -join " "))
        return
    }

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh command failed: gh $($Arguments -join ' ')"
    }
}

function Set-Secret {
    param(
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "Secret name/value is empty."
    }

    if ($WhatIf) {
        Write-Output "gh secret set $Name --repo $resolvedRepository --body ***"
        return
    }

    $Value | gh secret set $Name --repo $resolvedRepository --body-file -
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set GitHub secret '$Name'."
    }
}

function Set-Variable {
    param(
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "Variable name/value is empty."
    }

    Invoke-Gh @("variable", "set", $Name, "--repo", $resolvedRepository, "--body", $Value)
}

if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue) -and -not $WhatIf) {
    throw "GitHub CLI 'gh' was not found. Install gh or run with -WhatIf to preview commands."
}

$resolvedRepository = Resolve-Repository
$resolvedInputPath = Resolve-Path $InputPath
$items = @(Get-Content -Raw $resolvedInputPath | ConvertFrom-Json)
if ($items.Count -eq 0) {
    throw "No signing key entries were found in '$resolvedInputPath'."
}

foreach ($item in $items) {
    if ($UseBase64Secrets) {
        Set-Secret -Name $item.GitHubSecretBase64Name -Value $item.GitHubSecretBase64Value
        Set-Variable -Name $item.GitHubVariableBase64Name -Value $item.GitHubVariableBase64Value
    }
    else {
        Set-Secret -Name $item.GitHubSecretName -Value $item.GitHubSecretValue
        Set-Variable -Name $item.GitHubVariableName -Value $item.GitHubVariableValue
    }

    Set-Variable -Name $item.KeyIdVariableName -Value $item.KeyIdVariableValue
}

if ($EnableProductionGuards) {
    Set-Variable -Name "REQUIRE_SERVICE_PACKAGE_SIGNATURE" -Value "true"
    Set-Variable -Name "REQUIRE_CODE_SIGNING" -Value "true"
}

[pscustomobject]@{
    Repository = $resolvedRepository
    EntriesApplied = $items.Count
    UsedBase64 = $UseBase64Secrets.IsPresent
    ProductionGuardsEnabled = $EnableProductionGuards.IsPresent
    WhatIf = $WhatIf.IsPresent
} | ConvertTo-Json -Depth 3

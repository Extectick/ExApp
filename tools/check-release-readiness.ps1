param(
    [string]$Repository,
    [ValidateSet("all", "app", "services")]
    [string]$Scope = "all",
    [switch]$Production,
    [switch]$EnvironmentOnly
)

$ErrorActionPreference = "Stop"

function Resolve-Repository {
    if (-not [string]::IsNullOrWhiteSpace($Repository)) {
        return $Repository
    }

    $remote = & git remote get-url origin 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remote)) {
        return $null
    }

    $text = $remote.Trim()
    if ($text -match "github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?$") {
        return "$($Matches.owner)/$($Matches.repo)"
    }

    return $null
}

function Get-GitHubNames {
    param(
        [string]$Kind,
        [string]$RepositoryName
    )

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh -or [string]::IsNullOrWhiteSpace($RepositoryName)) {
        return $null
    }

    $output = & gh $Kind list --repo $RepositoryName 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return @($output | ForEach-Object {
        if ($_ -match "^(?<name>\S+)") {
            $Matches.name
        }
    })
}

function Get-GitHubVariableValue {
    param(
        [string]$Name,
        [string]$RepositoryName
    )

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh -or [string]::IsNullOrWhiteSpace($RepositoryName)) {
        return $null
    }

    $output = & gh variable get $Name --repo $RepositoryName 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Test-AnyName {
    param(
        [string[]]$Names,
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if ($Names -contains $candidate) {
            return $true
        }
    }

    return $false
}

function Test-AnyEnvironment {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($candidate))) {
            return $true
        }
    }

    return $false
}

function Test-ExpectedEnvironment {
    param(
        [string[]]$Candidates,
        [string]$ExpectedValue
    )

    foreach ($candidate in $Candidates) {
        if ([Environment]::GetEnvironmentVariable($candidate) -eq $ExpectedValue) {
            return $true
        }
    }

    return $false
}

function Test-ExpectedGitHubVariable {
    param(
        [string[]]$Candidates,
        [string]$ExpectedValue,
        [string]$RepositoryName
    )

    foreach ($candidate in $Candidates) {
        $value = Get-GitHubVariableValue -Name $candidate -RepositoryName $RepositoryName
        if ($value -eq $ExpectedValue) {
            return $true
        }
    }

    return $false
}

function Add-Check {
    param(
        [string]$Name,
        [string]$Kind,
        [string[]]$Candidates,
        [bool]$Required,
        [string[]]$AvailableNames,
        [string]$ExpectedValue
    )

    $configured = if (-not [string]::IsNullOrWhiteSpace($ExpectedValue)) {
        if ($EnvironmentOnly -or $null -eq $AvailableNames) {
            Test-ExpectedEnvironment -Candidates $Candidates -ExpectedValue $ExpectedValue
        }
        elseif ($Kind -eq "variable") {
            Test-ExpectedGitHubVariable -Candidates $Candidates -ExpectedValue $ExpectedValue -RepositoryName $resolvedRepository
        }
        else {
            Test-AnyName -Names $AvailableNames -Candidates $Candidates
        }
    }
    elseif ($EnvironmentOnly -or $null -eq $AvailableNames) {
        Test-AnyEnvironment $Candidates
    }
    else {
        Test-AnyName -Names $AvailableNames -Candidates $Candidates
    }

    $script:checks += [pscustomobject]@{
        Name = $Name
        Kind = $Kind
        Required = $Required
        Configured = $configured
        AcceptedNames = $Candidates -join ", "
        ExpectedValue = if ([string]::IsNullOrWhiteSpace($ExpectedValue)) { $null } else { $ExpectedValue }
    }
}

$resolvedRepository = Resolve-Repository
$secretNames = if ($EnvironmentOnly) { $null } else { Get-GitHubNames -Kind "secret" -RepositoryName $resolvedRepository }
$variableNames = if ($EnvironmentOnly) { $null } else { Get-GitHubNames -Kind "variable" -RepositoryName $resolvedRepository }
$usingGitHub = -not $EnvironmentOnly -and $null -ne $secretNames -and $null -ne $variableNames
$checks = @()
$requiresAppRelease = $Scope -in @("all", "app")
$requiresServiceRelease = $Scope -in @("all", "services")

Add-Check `
    -Name "App update manifest private signing key" `
    -Kind "secret" `
    -Candidates @("APP_UPDATE_SIGNING_PRIVATE_KEY_PEM", "APP_UPDATE_SIGNING_PRIVATE_KEY_BASE64") `
    -Required $requiresAppRelease `
    -AvailableNames $secretNames

Add-Check `
    -Name "App update manifest public verification key" `
    -Kind "variable" `
    -Candidates @("APP_UPDATE_SIGNING_PUBLIC_KEY_PEM", "APP_UPDATE_SIGNING_PUBLIC_KEY_BASE64") `
    -Required $requiresAppRelease `
    -AvailableNames $variableNames

Add-Check `
    -Name "Service catalog private signing key" `
    -Kind "secret" `
    -Candidates @("SERVICE_CATALOG_SIGNING_PRIVATE_KEY_PEM", "SERVICE_CATALOG_SIGNING_PRIVATE_KEY_BASE64", "APP_UPDATE_SIGNING_PRIVATE_KEY_PEM", "APP_UPDATE_SIGNING_PRIVATE_KEY_BASE64") `
    -Required $requiresServiceRelease `
    -AvailableNames $secretNames

Add-Check `
    -Name "Service catalog public verification key" `
    -Kind "variable" `
    -Candidates @("SERVICE_CATALOG_SIGNING_PUBLIC_KEY_PEM", "SERVICE_CATALOG_SIGNING_PUBLIC_KEY_BASE64", "APP_UPDATE_SIGNING_PUBLIC_KEY_PEM", "APP_UPDATE_SIGNING_PUBLIC_KEY_BASE64") `
    -Required $requiresServiceRelease `
    -AvailableNames $variableNames

Add-Check `
    -Name "Service package private signing key" `
    -Kind "secret" `
    -Candidates @("SERVICE_PACKAGE_SIGNING_PRIVATE_KEY_PEM", "SERVICE_PACKAGE_SIGNING_PRIVATE_KEY_BASE64") `
    -Required ($Production.IsPresent -and $requiresServiceRelease) `
    -AvailableNames $secretNames

Add-Check `
    -Name "Service package public verification key" `
    -Kind "variable" `
    -Candidates @("SERVICE_PACKAGE_SIGNING_PUBLIC_KEY_PEM", "SERVICE_PACKAGE_SIGNING_PUBLIC_KEY_BASE64") `
    -Required ($Production.IsPresent -and $requiresServiceRelease) `
    -AvailableNames $variableNames

Add-Check `
    -Name "Code-signing certificate" `
    -Kind "secret" `
    -Candidates @("SIGNING_CERTIFICATE_BASE64") `
    -Required ($Production.IsPresent -and $requiresAppRelease) `
    -AvailableNames $secretNames

Add-Check `
    -Name "Code-signing certificate password" `
    -Kind "secret" `
    -Candidates @("SIGNING_CERTIFICATE_PASSWORD") `
    -Required ($Production.IsPresent -and $requiresAppRelease) `
    -AvailableNames $secretNames

Add-Check `
    -Name "Require service package signatures" `
    -Kind "variable" `
    -Candidates @("REQUIRE_SERVICE_PACKAGE_SIGNATURE") `
    -Required ($Production.IsPresent -and $requiresServiceRelease) `
    -AvailableNames $variableNames `
    -ExpectedValue $(if ($Production.IsPresent -and $requiresServiceRelease) { "true" } else { $null })

Add-Check `
    -Name "Require Windows code signing" `
    -Kind "variable" `
    -Candidates @("REQUIRE_CODE_SIGNING") `
    -Required ($Production.IsPresent -and $requiresAppRelease) `
    -AvailableNames $variableNames `
    -ExpectedValue $(if ($Production.IsPresent -and $requiresAppRelease) { "true" } else { $null })

$missingRequired = @($checks | Where-Object { $_.Required -and -not $_.Configured })

[pscustomobject]@{
    Repository = $resolvedRepository
    Source = if ($usingGitHub) { "github" } else { "environment" }
    Scope = $Scope
    Production = $Production.IsPresent
    Ready = $missingRequired.Count -eq 0
    Checks = $checks
} | ConvertTo-Json -Depth 5

if ($missingRequired.Count -gt 0) {
    exit 1
}

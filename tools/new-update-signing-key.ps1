param(
    [ValidateSet("app-update", "service-catalog", "service-package", "all")]
    [string]$Purpose = "all"
)

$ErrorActionPreference = "Stop"

$definitions = [ordered]@{
    "app-update" = [pscustomobject]@{
        Purpose = "app-update"
        SecretName = "APP_UPDATE_SIGNING_PRIVATE_KEY_PEM"
        SecretBase64Name = "APP_UPDATE_SIGNING_PRIVATE_KEY_BASE64"
        VariableName = "APP_UPDATE_SIGNING_PUBLIC_KEY_PEM"
        VariableBase64Name = "APP_UPDATE_SIGNING_PUBLIC_KEY_BASE64"
        KeyIdVariableName = "APP_UPDATE_SIGNING_KEY_ID"
        KeyIdValue = "exapp-app-update-2026"
    }
    "service-catalog" = [pscustomobject]@{
        Purpose = "service-catalog"
        SecretName = "SERVICE_CATALOG_SIGNING_PRIVATE_KEY_PEM"
        SecretBase64Name = "SERVICE_CATALOG_SIGNING_PRIVATE_KEY_BASE64"
        VariableName = "SERVICE_CATALOG_SIGNING_PUBLIC_KEY_PEM"
        VariableBase64Name = "SERVICE_CATALOG_SIGNING_PUBLIC_KEY_BASE64"
        KeyIdVariableName = "SERVICE_CATALOG_SIGNING_KEY_ID"
        KeyIdValue = "exapp-service-catalog-2026"
    }
    "service-package" = [pscustomobject]@{
        Purpose = "service-package"
        SecretName = "SERVICE_PACKAGE_SIGNING_PRIVATE_KEY_PEM"
        SecretBase64Name = "SERVICE_PACKAGE_SIGNING_PRIVATE_KEY_BASE64"
        VariableName = "SERVICE_PACKAGE_SIGNING_PUBLIC_KEY_PEM"
        VariableBase64Name = "SERVICE_PACKAGE_SIGNING_PUBLIC_KEY_BASE64"
        KeyIdVariableName = "SERVICE_PACKAGE_SIGNING_KEY_ID"
        KeyIdValue = "exapp-service-package-2026"
    }
}

function New-KeyResult {
    param([object]$Definition)

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $privateKeyPem = $ecdsa.ExportECPrivateKeyPem()
        $publicKeyPem = $ecdsa.ExportSubjectPublicKeyInfoPem()
    }
    finally {
        $ecdsa.Dispose()
    }

    [pscustomobject]@{
        Purpose = $Definition.Purpose
        GitHubSecretName = $Definition.SecretName
        GitHubSecretValue = $privateKeyPem
        GitHubSecretBase64Name = $Definition.SecretBase64Name
        GitHubSecretBase64Value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($privateKeyPem))
        GitHubVariableName = $Definition.VariableName
        GitHubVariableValue = $publicKeyPem
        GitHubVariableBase64Name = $Definition.VariableBase64Name
        GitHubVariableBase64Value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($publicKeyPem))
        KeyIdVariableName = $Definition.KeyIdVariableName
        KeyIdVariableValue = $Definition.KeyIdValue
    }
}

$selected = if ($Purpose -eq "all") {
    @($definitions.Values)
}
else {
    @($definitions[$Purpose])
}

@($selected | ForEach-Object { New-KeyResult $_ }) | ConvertTo-Json -Depth 5

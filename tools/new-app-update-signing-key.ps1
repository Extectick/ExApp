$ErrorActionPreference = "Stop"

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $privateKeyPem = $ecdsa.ExportECPrivateKeyPem()
    $publicKeyPem = $ecdsa.ExportSubjectPublicKeyInfoPem()
}
finally {
    $ecdsa.Dispose()
}

[pscustomobject]@{
    GitHubSecretName = "APP_UPDATE_SIGNING_PRIVATE_KEY_PEM"
    GitHubSecretValue = $privateKeyPem
    GitHubSecretBase64Name = "APP_UPDATE_SIGNING_PRIVATE_KEY_BASE64"
    GitHubSecretBase64Value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($privateKeyPem))
    GitHubVariableName = "APP_UPDATE_SIGNING_PUBLIC_KEY_PEM"
    GitHubVariableValue = $publicKeyPem
    GitHubVariableBase64Name = "APP_UPDATE_SIGNING_PUBLIC_KEY_BASE64"
    GitHubVariableBase64Value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($publicKeyPem))
    KeyIdVariableName = "APP_UPDATE_SIGNING_KEY_ID"
    KeyIdVariableValue = "exapp-app-update-2026"
} | ConvertTo-Json -Depth 3

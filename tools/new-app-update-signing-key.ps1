$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "new-update-signing-key.ps1") -Purpose app-update

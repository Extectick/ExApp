# ExApp

ExApp is a Windows service platform shell. The base app provides a WinUI desktop shell, service browser, package/runtime foundations, diagnostics, and update surfaces. Services are installed separately as signed `.svcpkg` packages and run as separate processes.

## Local package workflow

Build the mock package:

```powershell
.\tools\package-mock-service.ps1
```

Install and inspect it with the MVP 2 package tool:

```powershell
dotnet run --project tools/ExApp.PackageTool -- install artifacts/mock-service-0.1.0-win-x64.svcpkg
dotnet run --project tools/ExApp.PackageTool -- list
dotnet run --project tools/ExApp.PackageTool -- state mock-service
dotnet run --project tools/ExApp.PackageTool -- rollback mock-service
dotnet run --project tools/ExApp.PackageTool -- uninstall mock-service --delete-data
```

Use `--root <path>` to operate on an isolated package store instead of `%LocalAppData%\ExApp`.

## App installer workflow

Build the desktop ZIP payload and MSI installer:

```powershell
.\tools\package-exapp.ps1 -Configuration Release -Version 0.1.0
.\tools\build-exapp-installer.ps1 -Version 0.1.0
```

The MSI installs ExApp into `%LocalAppData%\Programs\ExApp`, creates a Start Menu shortcut, and keeps the same app payload that the updater uses. The per-user install path lets ExApp apply automatic updates without elevation. Published `app-v*` releases include:

- `exapp-<version>-win-x64.msi`
- `exapp-<version>-win-x64.zip`
- `exapp-delta-<base>-to-<version>-win-x64.zip` when a previous app release exists
- SHA-256 and size sidecars
- `exapp-update.json`

Application updates prefer the delta ZIP when the installed version matches the
delta `baseVersion`. The delta package contains only changed/new files plus
deletion metadata; unchanged installed files are not rewritten. If no matching
delta exists, ExApp falls back to the full ZIP.

For production code signing, configure GitHub:

```text
Secret:   SIGNING_CERTIFICATE_BASE64
Secret:   SIGNING_CERTIFICATE_PASSWORD
Variable: REQUIRE_CODE_SIGNING=true
```

`SIGNING_CERTIFICATE_BASE64` is the base64 text of the `.pfx` code-signing certificate. When `REQUIRE_CODE_SIGNING=true`, release builds fail if signing cannot be completed and verified.

Application releases use `app-v*` tags. Service releases use `services-v*` tags.

Current implementation status is tracked in [`docs/00_STATUS.md`](docs/00_STATUS.md).

# ExApp

ExApp is a Windows service platform shell. The base app provides a WinUI desktop shell, service browser, package/runtime foundations, diagnostics, and update surfaces. Services are installed separately as signed `.svcpkg` packages and run as separate processes.

## Local package workflow

Build the mock package:

```powershell
.\tools\package-mock-service.ps1
```

Install and inspect it with the MVP 2 package tool:

```powershell
dotnet run --project tools/MyApp.PackageTool -- install artifacts/mock-service-0.1.0-win-x64.svcpkg
dotnet run --project tools/MyApp.PackageTool -- list
dotnet run --project tools/MyApp.PackageTool -- state mock-service
dotnet run --project tools/MyApp.PackageTool -- rollback mock-service
dotnet run --project tools/MyApp.PackageTool -- uninstall mock-service --delete-data
```

Use `--root <path>` to operate on an isolated package store instead of `%LocalAppData%\ExApp`.

Current implementation status is tracked in [`docs/00_STATUS.md`](docs/00_STATUS.md).

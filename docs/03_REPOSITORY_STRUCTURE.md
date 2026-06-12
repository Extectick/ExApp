# 03 — Структура репозитория

## Рекомендуемый mono-repo

```text
exapp/
├── README.md
├── docs/
├── src/
│   ├── ExApp.Desktop/
│   ├── ExApp.Agent/
│   ├── ExApp.Core/
│   ├── ExApp.Ipc/
│   ├── ExApp.Packaging/
│   ├── ExApp.ServiceRuntime/
│   ├── ExApp.Updater/
│   └── ExApp.Diagnostics/
├── services/
│   ├── MockService/
│   └── VpnClient/
├── catalog/
│   ├── services.stable.json
│   ├── services.beta.json
│   └── schemas/
├── tools/
├── tests/
└── .github/workflows/
```

## Проекты

### ExApp.Desktop

- [ ] TODO — WinUI 3 приложение
- [ ] TODO — MVVM структура
- [ ] TODO — страницы UI
- [ ] TODO — IPC client
- [ ] TODO — tray
- [ ] TODO — notification wrapper

### ExApp.Agent

- [ ] TODO — Worker service
- [ ] TODO — IPC server
- [ ] TODO — lifecycle manager
- [ ] TODO — package manager host
- [ ] TODO — service process host

### ExApp.Core

- [ ] TODO — общие DTO
- [ ] TODO — enums
- [ ] TODO — versioning helpers
- [ ] TODO — result/error model
- [ ] TODO — common exceptions

### ExApp.Ipc

- [ ] TODO — named pipe contracts
- [ ] TODO — request/response envelope
- [ ] TODO — streaming logs
- [ ] TODO — cancellation
- [ ] TODO — auth/ACL для pipe

### ExApp.Packaging

- [ ] TODO — `.svcpkg` reader
- [ ] TODO — manifest validator
- [ ] TODO — checksum validator
- [ ] TODO — signature validator
- [ ] TODO — install/rollback logic

### ExApp.ServiceRuntime

- [ ] TODO — service registry
- [ ] TODO — service process launcher
- [ ] TODO — health checks
- [ ] TODO — service state machine
- [ ] TODO — service logging

## Правила именования

- Project names: `ExApp.ComponentName`
- Service package ID: kebab-case, например `vpn-client`
- Service process name: `ExApp.Service.<Name>.exe`
- DTO suffix: `Request`, `Response`, `Dto`
- Commands: dot notation, например `service.install`, `vpn.start`

## Первые задачи в репозитории

- [x] DONE — создать solution `ExApp.sln`
- [x] DONE — создать проекты из `src/`
- [x] DONE — добавить `Directory.Build.props`
- [x] DONE — включить nullable
- [x] DONE — включить analyzers
- [x] DONE — добавить базовый CI build
- [x] DONE — перенести docs

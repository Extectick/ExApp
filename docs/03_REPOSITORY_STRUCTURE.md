# 03 — Структура репозитория

## Рекомендуемый mono-repo

```text
myapp/
├── README.md
├── docs/
├── src/
│   ├── MyApp.Desktop/
│   ├── MyApp.Agent/
│   ├── MyApp.Core/
│   ├── MyApp.Ipc/
│   ├── MyApp.Packaging/
│   ├── MyApp.ServiceRuntime/
│   ├── MyApp.Updater/
│   └── MyApp.Diagnostics/
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

### MyApp.Desktop

- [ ] TODO — WinUI 3 приложение
- [ ] TODO — MVVM структура
- [ ] TODO — страницы UI
- [ ] TODO — IPC client
- [ ] TODO — tray
- [ ] TODO — notification wrapper

### MyApp.Agent

- [ ] TODO — Worker service
- [ ] TODO — IPC server
- [ ] TODO — lifecycle manager
- [ ] TODO — package manager host
- [ ] TODO — service process host

### MyApp.Core

- [ ] TODO — общие DTO
- [ ] TODO — enums
- [ ] TODO — versioning helpers
- [ ] TODO — result/error model
- [ ] TODO — common exceptions

### MyApp.Ipc

- [ ] TODO — named pipe contracts
- [ ] TODO — request/response envelope
- [ ] TODO — streaming logs
- [ ] TODO — cancellation
- [ ] TODO — auth/ACL для pipe

### MyApp.Packaging

- [ ] TODO — `.svcpkg` reader
- [ ] TODO — manifest validator
- [ ] TODO — checksum validator
- [ ] TODO — signature validator
- [ ] TODO — install/rollback logic

### MyApp.ServiceRuntime

- [ ] TODO — service registry
- [ ] TODO — service process launcher
- [ ] TODO — health checks
- [ ] TODO — service state machine
- [ ] TODO — service logging

## Правила именования

- Project names: `MyApp.ComponentName`
- Service package ID: kebab-case, например `vpn-client`
- Service process name: `MyApp.Service.<Name>.exe`
- DTO suffix: `Request`, `Response`, `Dto`
- Commands: dot notation, например `service.install`, `vpn.start`

## Первые задачи в репозитории

- [x] DONE — создать solution `MyApp.sln`
- [x] DONE — создать проекты из `src/`
- [x] DONE — добавить `Directory.Build.props`
- [x] DONE — включить nullable
- [x] DONE — включить analyzers
- [x] DONE — добавить базовый CI build
- [x] DONE — перенести docs

# 06 — Package Manager Spec

## Цель

Package Manager отвечает за установку, обновление, проверку и удаление сервисов.

## Формат пакета

Расширение:

```text
.svcpkg
```

Физически это zip-архив:

```text
vpn-client-1.0.0-win-x64.svcpkg
├── service.manifest.json
├── checksums.json
├── signature.sig
├── icon.png
├── bin/
│   ├── ExApp.Service.Vpn.exe
│   └── dependencies...
├── ui/
│   └── service-ui.json
└── assets/
```

## checksums.json

```json
{
  "algorithm": "sha256",
  "files": [
    {
      "path": "service.manifest.json",
      "sha256": "..."
    },
    {
      "path": "bin/ExApp.Service.Vpn.exe",
      "sha256": "..."
    }
  ]
}
```

## Установка

```mermaid
flowchart TD
    A[Download package] --> B[Check package hash]
    B --> C[Extract to staging]
    C --> D[Validate manifest]
    D --> E[Validate checksums]
    E --> F[Validate signature]
    F --> G[Check compatibility]
    G --> H[Show permissions]
    H --> I[Activate version]
    I --> J[Update registry]
```

## Папки

```text
%LocalAppData%/ExApp/
├── services/
│   └── vpn-client/
│       ├── current/
│       ├── versions/
│       │   ├── 1.0.0/
│       │   └── 1.0.1/
│       ├── data/
│       ├── logs/
│       └── package-state.json
├── packages/
│   ├── cache/
│   └── staging/
└── registry/
    └── installed-services.json
```

## Atomic activation

Windows symlink может требовать отдельной обработки прав, поэтому лучше не зависеть от symlink в MVP. Использовать pointer file:

```json
{
  "currentVersion": "1.0.1",
  "previousVersion": "1.0.0",
  "state": "installed"
}
```

Agent резолвит current так:

```text
services/<id>/versions/<currentVersion>/
```

## Install checklist

- [ ] TODO — скачать пакет во временный файл
- [ ] TODO — проверить expected package sha256 из catalog
- [x] DONE — распаковать в staging
- [x] DONE — проверить наличие `service.manifest.json`
- [x] DONE — проверить schema manifest
- [x] DONE — проверить `id`, `version`, `platform`, `architecture`
- [x] DONE — проверить `minAppVersion`
- [x] DONE — проверить `minAgentVersion`
- [x] DONE — проверить checksums всех файлов
- [ ] REVIEW — проверить подпись (MVP 2 проверяет наличие непустого `signature.sig`; криптографическая проверка отложена)
- [ ] TODO — показать permissions
- [x] DONE — скопировать в `versions/<version>`
- [x] DONE — обновить `package-state.json`
- [x] DONE — обновить `installed-services.json`
- [x] DONE — очистить staging

## Update checklist

- [x] DONE — проверить, что сервис установлен
- [x] DONE — проверить доступную версию
- [ ] TODO — остановить сервис, если он запущен
- [ ] TODO — скачать новую версию
- [ ] TODO — установить в staging
- [x] DONE — сохранить previousVersion
- [x] DONE — переключить currentVersion
- [ ] TODO — запустить service health check
- [x] DONE — при ошибке вернуть previousVersion
- [ ] TODO — при успехе удалить старые версии по retention policy

## Uninstall checklist

- [ ] TODO — остановить сервис
- [x] DONE — удалить binaries
- [x] DONE — удалить package state
- [x] DONE — удалить запись из registry
- [ ] TODO — спросить пользователя о data deletion
- [x] DONE — если пользователь согласен, удалить `data/`
- [ ] TODO — если пользователь согласен, удалить secrets
- [ ] TODO — сохранить uninstall log

## Required tests

- [x] DONE — valid package installs
- [x] DONE — invalid manifest rejected
- [x] DONE — wrong sha256 rejected
- [x] DONE — incompatible app version rejected
- [x] DONE — update rollback works
- [x] DONE — uninstall preserves data
- [x] DONE — uninstall deletes data when requested

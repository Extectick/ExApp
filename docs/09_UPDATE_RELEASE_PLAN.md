# 09 — Update & Release Plan

## Цель

Релизы приложения и сервисов должны идти через GitHub Releases, но пользователь должен получать обновления удобно и безопасно.

## Уровни обновлений

```text
Level 1: ExApp Desktop + Agent
Level 2: Installed Services
Level 3: Service Catalog
```

## App updates

Реализован собственный внешний `ExApp.Updater` + GitHub Releases. Desktop и Agent
поставляются как полный ZIP и как delta ZIP между предыдущим и новым app release.
Клиент выбирает delta ZIP, если установленная версия совпадает с `baseVersion`.
Delta содержит измененные/новые файлы, список удалений и patch-операции
`copy/data` для фрагментной пересборки измененных файлов. Updater применяет
файловый manifest, заменяет только нужные файлы, не трогает неизмененные, делает
backup затронутых файлов и откатывает их при неуспешном старте новой версии.
Если delta не применима из-за поврежденной локальной базы или несовпадения hash,
updater автоматически переключается на full package fallback.
Для первичной установки выпускается per-user MSI installer с тем же payload.
Установка идет в `%LocalAppData%\Programs\ExApp`, чтобы updater мог менять файлы
без прав администратора.

Поток:

```mermaid
flowchart TD
    A[git tag v1.0.0] --> B[GitHub Actions]
    B --> C[dotnet publish]
    C --> D[Sign EXE/DLL if certificate configured]
    D --> E[Build ZIP update payload]
    E --> F[Build delta ZIP from previous release]
    F --> G[Build MSI installer]
    G --> H[GitHub Release]
    H --> I[Client UpdateManager]
```

## App release checklist

- [x] DONE — добавить update check
- [x] DONE — добавить stable channel
- [x] DONE — добавить beta channel
- [x] DONE — настроить GitHub Actions
- [x] DONE — генерировать полный ZIP package
- [x] DONE — генерировать delta ZIP package
- [x] DONE — проверять SHA-256 и размер
- [x] DONE — обновлять Desktop и Agent через changed-file apply
- [x] DONE — создавать backup затронутых файлов и выполнять rollback
- [x] DONE — публиковать GitHub Release
- [x] DONE — добавить MSI installer
- [x] DONE — добавить signing pipeline hooks
- [ ] TODO — подключить production code-signing certificate в GitHub secrets
- [x] DONE — выбирать delta package при совпадении baseVersion
- [x] DONE — fallback delta -> full package при поврежденной локальной базе
- [x] DONE — CI smoke для delta update и delta fallback

## Installer and signing

Локальная сборка installer:

```powershell
.\tools\package-exapp.ps1 -Configuration Release -Version 0.1.0
.\tools\build-exapp-installer.ps1 -Version 0.1.0
```

Production release должен быть подписан доверенным code-signing сертификатом.
GitHub Actions поддерживает `.pfx` через secrets:

```text
SIGNING_CERTIFICATE_BASE64
SIGNING_CERTIFICATE_PASSWORD
```

Чтобы запретить неподписанные app releases, включить repository variable:

```text
REQUIRE_CODE_SIGNING=true
```

Preflight перед production release:

```powershell
.\tools\check-release-readiness.ps1 -Production
```

Скрипт проверяет наличие GitHub secrets/vars через `gh`, а если GitHub CLI
недоступен — локальные environment variables с теми же именами.

Сгенерировать signing keys для GitHub secrets/vars:

```powershell
# Все ключи: app update manifest, service catalog, service packages
.\tools\new-update-signing-key.ps1 -Purpose all | Set-Content -Encoding UTF8 .\release-signing-keys.json

# Только app update manifest, совместимый старый wrapper
.\tools\new-app-update-signing-key.ps1
```

В GitHub secrets нужно заносить private key значения (`GitHubSecretValue` или
`GitHubSecretBase64Value`). В GitHub variables нужно заносить public key значения
и `KeyIdVariableValue`.

Применить generated JSON через GitHub CLI:

```powershell
# Сначала посмотреть, какие secrets/vars будут установлены
.\tools\apply-release-secrets.ps1 -InputPath .\release-signing-keys.json -WhatIf

# Установить signing keys и включить production guards
.\tools\apply-release-secrets.ps1 -InputPath .\release-signing-keys.json -EnableProductionGuards

# Проверить готовность release pipeline
.\tools\check-release-readiness.ps1 -Production
```

`release-signing-keys.json` содержит private keys. Его нельзя коммитить и нельзя
хранить в репозитории.

## Service updates

Service updates не должны зависеть от обновления всего приложения.

Поток:

```mermaid
flowchart TD
    A[Catalog refresh] --> B[Find installed service]
    B --> C[Compare version]
    C --> D[Download smallest matching delta]
    D --> E[Verify]
    E --> F[Stop service]
    F --> G[Install delta to versions/new]
    G --> H[Switch currentVersion]
    H --> I[Health check]
    I --> J{OK?}
    J -->|yes| K[Done]
    J -->|no| L[Rollback]
    E -->|delta base mismatch| M[Download full package fallback]
    M --> F
```

## Service release checklist

- [x] DONE — собрать service binaries
- [x] DONE — создать `.svcpkg`
- [x] DONE — сгенерировать checksums
- [x] DONE — поддержать подпись package в pipeline
- [ ] TODO — включить production service package signing secrets
- [x] DONE — загрузить в GitHub Releases
- [x] DONE — обновить `services.stable.json`
- [x] DONE — проверить catalog metadata
- [x] DONE — поддержать подпись catalog в pipeline
- [ ] TODO — включить production catalog signing secrets или APP_UPDATE fallback key
- [x] DONE — опубликовать catalog
- [x] DONE — service delta package и file-fragment patch updates
- [x] DONE — fallback delta -> full package при `delta.*` ошибках
- [x] DONE — CI smoke для service delta update

## Channels

Сразу заложить:

- [x] DONE — `stable`
- [x] DONE — `beta` для приложения
- [ ] TODO — `dev`

## GitHub repositories

Рекомендуемый вариант:

```text
github.com/<owner>/exapp
  - основное приложение
  - исходники
  - app releases

github.com/<owner>/exapp-services
  - service packages
  - service releases

github.com/<owner>/exapp-catalog
  - services.stable.json
  - services.beta.json
```

Для MVP можно всё держать в одном mono-repo, но логически разделить папки.

## Versioning

Использовать SemVer:

```text
App:     0.1.0
Agent:   0.1.0
Service: 0.1.0
API:     1
Catalog: 1
```

## Release rules

- [ ] TODO — каждый app release имеет changelog
- [ ] TODO — каждый service release имеет changelog
- [ ] TODO — нельзя перезаписывать опубликованные версии
- [ ] TODO — нельзя менять package без изменения version
- [ ] TODO — нельзя публиковать package без sha256
- [ ] TODO — нельзя публиковать unsigned package в production
- [ ] TODO — rollback должен быть возможен минимум на одну версию назад

## Update UI

- [x] DONE — текущая версия приложения
- [x] DONE — текущая версия Agent
- [x] DONE — список установленных сервисов и версий
- [x] DONE — кнопка “Проверить обновления”
- [x] DONE — automatic update check toggle
- [x] DONE — channel selector
- [x] DONE — update history
- [ ] TODO — restart required state

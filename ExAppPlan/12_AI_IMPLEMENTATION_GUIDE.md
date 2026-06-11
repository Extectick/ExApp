# 12 — AI Implementation Guide

## Назначение

Этот файл нужен, чтобы ИИ-агент мог реализовывать проект пошагово, не теряя контекст и не перескакивая на поздние функции.

## Главная инструкция для AI

Не начинать с VPN. Сначала построить платформу.

Правильный порядок:

```text
1. Desktop shell
2. Mock service
3. Package manager
4. Agent + IPC
5. Remote catalog
6. VPN static config
7. VPN subscription
8. Updates
```

## Правила работы с задачами

Перед началом:

- [ ] TODO — открыть `00_STATUS.md`
- [ ] TODO — открыть `04_MVP_ROADMAP.md`
- [ ] TODO — выбрать ближайшую задачу из текущего MVP
- [ ] TODO — пометить её как `DOING`
- [ ] TODO — не брать задачи из будущих MVP без необходимости

После завершения:

- [ ] TODO — пометить задачу как `DONE`
- [ ] TODO — добавить краткую заметку в `00_STATUS.md`
- [ ] TODO — если принято архитектурное решение, добавить ADR в `11_RISKS_AND_DECISIONS.md`
- [ ] TODO — если найден риск, добавить его в Risk Register
- [ ] TODO — если изменён API/manifest, обновить соответствующий spec файл

## Definition of Done

Задача считается выполненной, если:

- [ ] TODO — код компилируется
- [ ] TODO — нет критических warnings
- [ ] TODO — добавлены тесты, если задача затрагивает Core/Packaging/IPC
- [ ] TODO — обновлена документация
- [ ] TODO — обновлены чекбоксы
- [ ] TODO — сценарий проверен вручную

## Запрещённые действия для AI

- [ ] TODO — не загружать сервисные DLL внутрь Desktop
- [ ] TODO — не запускать VPN core напрямую из UI
- [ ] TODO — не хранить subscription URL plain text
- [ ] TODO — не логировать secrets
- [ ] TODO — не распаковывать package сразу в current
- [ ] TODO — не делать marketplace third-party services в MVP
- [ ] TODO — не добавлять kill switch до базового VPN MVP
- [ ] TODO — не смешивать app updates и service updates в один механизм

## Первый запрос к AI для старта разработки

```text
Реализуй MVP 0 из docs/04_MVP_ROADMAP.md.
Сначала создай solution и проекты по docs/03_REPOSITORY_STRUCTURE.md.
Не реализуй VPN, Package Manager и Agent пока.
После завершения обнови docs/00_STATUS.md и отметь выполненные чекбоксы.
```

## Следующие промпты

### MVP 1

```text
Реализуй MVP 1: Local Mock Service.
Создай минимальный mock-service package и отображение сервиса в UI.
Не подключай удалённый catalog и VPN.
Обнови docs/00_STATUS.md и docs/04_MVP_ROADMAP.md.
```

### MVP 2

```text
Реализуй MVP 2: Package Manager.
Добавь формат .svcpkg, manifest parser, sha256 validation, staging install, currentVersion pointer, uninstall и rollback.
Добавь unit tests для MyApp.Packaging.
Обнови docs.
```

### MVP 3

```text
Реализуй MVP 3: Agent + IPC.
Desktop должен управлять сервисами только через Agent.
Используй Named Pipes.
Добавь команды service.list, service.install, service.uninstall, service.start, service.stop, service.status, service.logs.
Обнови docs.
```

### MVP 4

```text
Реализуй MVP 4: GitHub Service Catalog.
Добавь загрузку services.stable.json, cache, offline fallback, отображение доступных сервисов и установку mock-service из GitHub Releases.
Обнови docs.
```

### MVP 5

```text
Реализуй MVP 5: VPN Service with static config.
VPN должен быть отдельным service package, запускаться Agent'ом как отдельный процесс и показывать status/logs в UI.
Не добавляй subscription URL пока.
Обнови docs.
```

### MVP 6

```text
Реализуй MVP 6: VPN Subscription.
Добавь сохранение subscription URL через DPAPI, поддержку одного формата, список nodes, генерацию config и запуск выбранного node.
Обязательно добавь secret redaction.
Обнови docs.
```

### MVP 7

```text
Реализуй MVP 7: Updates.
Подключи Velopack для приложения и отдельные обновления сервисов через catalog.
Добавь rollback и update UI.
Обнови docs.
```

## Architecture guardrails

```text
Desktop → IPC → Agent → ServiceRuntime → Service Process
Desktop → never starts service directly
Service → never modifies app files
PackageManager → never trusts package before validation
VPN → never logs secrets
```

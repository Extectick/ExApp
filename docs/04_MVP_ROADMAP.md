# 04 — MVP Roadmap

## Общая стратегия

Не начинать с VPN. Сначала построить платформу сервисов.

```text
MVP 0 → Shell
MVP 1 → Mock Service
MVP 2 → Package Manager
MVP 3 → Agent + IPC
MVP 4 → Remote Catalog
MVP 5 → VPN static config
MVP 6 → VPN subscription
MVP 7 → Updates
```

## MVP 0 — Desktop Skeleton

Цель: базовое приложение запускается, имеет UI, страницы и tray.

- [ ] TODO — создать WinUI 3 проект `ExApp.Desktop`
- [ ] TODO — создать solution
- [ ] TODO — добавить `NavigationView`
- [ ] TODO — добавить страницу `Services`
- [ ] TODO — добавить страницу `Service Browser`
- [ ] TODO — добавить страницу `Settings`
- [ ] TODO — добавить страницу `Diagnostics`
- [ ] TODO — реализовать light/dark/system theme
- [ ] TODO — реализовать single instance
- [ ] TODO — реализовать tray icon
- [ ] TODO — реализовать поведение “закрыть в трей”
- [ ] TODO — добавить базовое логирование

Acceptance:

- [ ] TODO — приложение запускается без ошибок
- [ ] TODO — повторный запуск активирует существующее окно
- [ ] TODO — окно можно скрыть в трей
- [ ] TODO — страницы переключаются
- [ ] TODO — настройки темы сохраняются

## MVP 1 — Local Mock Service

Цель: проверить модель сервисов без VPN и сети.

- [ ] TODO — создать `MockService`
- [ ] TODO — создать `service.manifest.json`
- [ ] TODO — mock service должен уметь `start`
- [ ] TODO — mock service должен уметь `stop`
- [ ] TODO — mock service должен отдавать `status`
- [ ] TODO — mock service должен писать heartbeat logs
- [ ] TODO — Desktop показывает mock service в списке

Acceptance:

- [ ] TODO — mock service можно установить локально
- [ ] TODO — mock service можно запустить
- [ ] TODO — mock service можно остановить
- [ ] TODO — UI показывает статус
- [ ] TODO — UI показывает последние логи

## MVP 2 — Package Manager

Цель: реализовать `.svcpkg` установку.

- [ ] TODO — определить zip-based формат `.svcpkg`
- [ ] TODO — реализовать manifest parser
- [ ] TODO — реализовать schema validation
- [ ] TODO — реализовать sha256 validation
- [ ] TODO — реализовать staging install
- [ ] TODO — реализовать current pointer
- [ ] TODO — реализовать rollback
- [ ] TODO — реализовать uninstall
- [ ] TODO — реализовать uninstall with data deletion
- [ ] TODO — добавить unit tests

## MVP 3 — Agent + IPC

Цель: UI перестаёт напрямую управлять сервисами.

- [ ] TODO — создать `ExApp.Agent`
- [ ] TODO — реализовать Named Pipe server
- [ ] TODO — реализовать Named Pipe client
- [ ] TODO — определить request/response envelope
- [ ] TODO — реализовать команды `service.list/install/uninstall/start/stop/status/logs`
- [ ] TODO — добавить ACL для pipe

## MVP 4 — GitHub Service Catalog

Цель: Браузер сервисов загружает каталог извне.

- [ ] TODO — создать `services.stable.json`
- [ ] TODO — создать catalog schema
- [ ] TODO — реализовать catalog downloader
- [ ] TODO — реализовать cache
- [ ] TODO — реализовать offline fallback
- [ ] TODO — показать сервисы из каталога в UI
- [ ] TODO — скачать mock package из GitHub Releases
- [ ] TODO — проверить sha256 перед установкой
- [ ] TODO — добавить basic signature placeholder

## MVP 5 — VPN Service: Static Config

Цель: запустить VPN core без подписок.

- [ ] TODO — создать `VpnClient` service project
- [ ] TODO — упаковать sing-box в service package
- [ ] TODO — добавить статический test config
- [ ] TODO — добавить команды `vpn.start`, `vpn.stop`, `vpn.status`
- [ ] TODO — показывать logs в UI
- [ ] TODO — правильно останавливать core process
- [ ] TODO — redaction логов
- [ ] TODO — обработать отсутствие admin rights

## MVP 6 — VPN Subscription

Цель: пользователь вставляет subscription URL.

- [ ] TODO — добавить storage для subscription URL
- [ ] TODO — хранить секреты через DPAPI
- [ ] TODO — скачать subscription URL
- [ ] TODO — поддержать первый формат подписки
- [ ] TODO — распарсить nodes
- [ ] TODO — показать список серверов
- [ ] TODO — выбрать node
- [ ] TODO — сгенерировать sing-box config
- [ ] TODO — запустить selected node
- [ ] TODO — добавить refresh subscription

## MVP 7 — Updates

Цель: обновлять приложение и сервисы.

- [x] DONE — подключить внешний ExApp.Updater к Desktop app
- [ ] TODO — настроить GitHub Actions release
- [x] DONE — добавить app update check
- [x] DONE — добавить app update UI
- [x] DONE — добавить service update check
- [x] DONE — добавить service update install и rollback
- [ ] TODO — добавить service rollback
- [ ] TODO — добавить stable/beta channel

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

- [x] DONE — создать WinUI 3 проект `MyApp.Desktop`
- [x] DONE — создать solution
- [x] DONE — добавить `NavigationView`
- [x] DONE — добавить страницу `Services`
- [x] DONE — добавить страницу `Service Browser`
- [x] DONE — добавить страницу `Settings`
- [x] DONE — добавить страницу `Diagnostics`
- [x] DONE — реализовать light/dark/system theme
- [x] DONE — реализовать en/ru JSON localization
- [x] DONE — реализовать выбор языка из Windows с fallback на en
- [x] DONE — добавить настройку языка
- [x] DONE — реализовать single instance
- [x] DONE — реализовать tray icon
- [x] DONE — реализовать поведение “закрыть в трей”
- [x] DONE — добавить базовое логирование

Acceptance:

- [x] DONE — приложение запускается без ошибок
- [x] DONE — повторный запуск активирует существующее окно
- [x] DONE — окно можно скрыть в трей
- [ ] REVIEW — страницы переключаются
- [ ] REVIEW — настройки темы сохраняются

## MVP 1 — Local Mock Service

Цель: проверить модель сервисов без VPN и сети.

- [x] DONE — создать `MockService`
- [x] DONE — создать `service.manifest.json`
- [x] DONE — mock service должен уметь `start`
- [x] DONE — mock service должен уметь `stop`
- [x] DONE — mock service должен отдавать `status`
- [x] DONE — mock service должен писать heartbeat logs
- [x] DONE — Desktop показывает mock service в списке

Acceptance:

- [x] DONE — mock service можно установить локально
- [x] DONE — mock service можно запустить
- [x] DONE — mock service можно остановить
- [x] DONE — UI показывает статус
- [x] DONE — UI показывает последние логи

## MVP 2 — Package Manager

Цель: реализовать `.svcpkg` установку.

- [x] DONE — определить zip-based формат `.svcpkg`
- [x] DONE — реализовать manifest parser
- [x] DONE — реализовать schema validation
- [x] DONE — реализовать sha256 validation
- [x] DONE — реализовать staging install
- [x] DONE — реализовать current pointer
- [x] DONE — реализовать rollback
- [x] DONE — реализовать uninstall
- [x] DONE — реализовать uninstall with data deletion
- [x] DONE — добавить unit tests

## MVP 3 — Agent + IPC

Цель: UI перестаёт напрямую управлять сервисами.

- [x] DONE — создать `MyApp.Agent`
- [x] DONE — реализовать Named Pipe server
- [x] DONE — реализовать Named Pipe client
- [x] DONE — определить request/response envelope
- [x] DONE — реализовать команды `service.list/install/uninstall/start/stop/status/logs`
- [x] DONE — добавить ACL для pipe

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

- [ ] TODO — подключить Velopack к Desktop app
- [ ] TODO — настроить GitHub Actions release
- [ ] TODO — добавить app update check
- [ ] TODO — добавить app update UI
- [ ] TODO — добавить service update check
- [ ] TODO — добавить service update install
- [ ] TODO — добавить service rollback
- [ ] TODO — добавить stable/beta channel

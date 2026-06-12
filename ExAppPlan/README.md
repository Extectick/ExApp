# ExApp Service Platform — план MVP

Дата создания: **2026-06-11**

## Цель проекта

Создать Windows-приложение-платформу, в котором пользователь устанавливает базовое приложение, открывает **Браузер сервисов**, скачивает нужные сервисы и управляет ими из единого интерфейса.

Первый сервис: **VPN Client**.

## Главная архитектурная идея

Базовое приложение не должно содержать всю функциональность внутри себя. Оно должно быть оболочкой и runtime-платформой:

```text
ExApp Desktop Shell
+ ExApp Agent
+ Package Manager
+ Service Browser
+ Installed Services Runtime
+ Update System
```

VPN-клиент должен быть не встроенным экраном, а первым устанавливаемым сервисным пакетом:

```text
vpn-client-1.0.0-win-x64.svcpkg
```

## Выбранный стек

| Область | Решение |
|---|---|
| UI | WinUI 3 + .NET |
| Фоновая часть | .NET Worker / Agent process, позже Windows Service |
| Обновления приложения | ExApp.Updater + GitHub Releases |
| Сервисы | Отдельные подписанные пакеты `.svcpkg` |
| Запуск сервисов | Отдельные процессы, не DLL внутри UI |
| IPC | Named Pipes / gRPC over Named Pipes |
| VPN core | sing-box first, WireGuard позже |
| Хранение | SQLite + файловая структура + DPAPI для секретов |
| Каталог сервисов | JSON catalog в GitHub/CDN |
| UI сервисов | Декларативная schema на первом этапе |

## Статусы чекбоксов

```text
- [ ] TODO — задача не начата
- [ ] DOING — задача выполняется
- [x] DONE — задача завершена
- [ ] BLOCKED — задача заблокирована
- [ ] REVIEW — нужна проверка
```

Markdown не поддерживает “частично выполнено” нативно, поэтому статус пишется текстом после чекбокса.

## Порядок чтения файлов

1. [`00_STATUS.md`](00_STATUS.md)
2. [`01_PRODUCT_VISION.md`](01_PRODUCT_VISION.md)
3. [`02_ARCHITECTURE.md`](02_ARCHITECTURE.md)
4. [`03_REPOSITORY_STRUCTURE.md`](03_REPOSITORY_STRUCTURE.md)
5. [`04_MVP_ROADMAP.md`](04_MVP_ROADMAP.md)
6. [`05_SERVICE_PLATFORM_SPEC.md`](05_SERVICE_PLATFORM_SPEC.md)
7. [`06_PACKAGE_MANAGER_SPEC.md`](06_PACKAGE_MANAGER_SPEC.md)
8. [`07_VPN_SERVICE_SPEC.md`](07_VPN_SERVICE_SPEC.md)
9. [`08_SECURITY_MODEL.md`](08_SECURITY_MODEL.md)
10. [`09_UPDATE_RELEASE_PLAN.md`](09_UPDATE_RELEASE_PLAN.md)
11. [`10_TESTING_QA_PLAN.md`](10_TESTING_QA_PLAN.md)
12. [`11_RISKS_AND_DECISIONS.md`](11_RISKS_AND_DECISIONS.md)
13. [`12_AI_IMPLEMENTATION_GUIDE.md`](12_AI_IMPLEMENTATION_GUIDE.md)
14. [`13_BACKLOG.md`](13_BACKLOG.md)
15. [`14_ACCEPTANCE_CRITERIA.md`](14_ACCEPTANCE_CRITERIA.md)

## Критическое правило реализации

Не начинать с VPN-логики. Сначала реализовать платформу:

```text
Shell → Agent → Package Manager → Mock Service → Remote Catalog → VPN Service
```

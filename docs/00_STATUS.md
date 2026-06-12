# 00 — Текущий статус проекта

Дата: **2026-06-12**

## Глобальный статус

- [x] DONE — сформирована целевая архитектура
- [x] DONE — выбран базовый стек
- [x] DONE — определена MVP-стратегия
- [x] DONE — создан репозиторий
- [x] DONE — создано WinUI 3 приложение
- [x] DONE — создан Agent
- [x] DONE — реализован локальный mock service
- [x] DONE — реализован Package Manager
- [x] DONE — подключён GitHub service catalog
- [ ] TODO — реализован первый VPN service
- [x] DONE — подключены обновления ExApp Desktop + Agent через GitHub Releases
- [x] DONE — подключены независимые обновления сервисов через service catalog

## Сейчас выполняется

- [ ] DOING — реализовать первый VPN service со статической конфигурацией
- [ ] REVIEW — проверить публикацию service package через GitHub Releases
- [ ] REVIEW — опубликовать первый `app-v*` release и проверить обновление между двумя опубликованными версиями

## Следующий лучший шаг

Завершить **MVP 5 — VPN Service: Static Config** из [`04_MVP_ROADMAP.md`](04_MVP_ROADMAP.md).

## Правила обновления статуса

Каждый AI/разработчик, который выполняет задачу, обязан:

- [ ] TODO — перед началом работы пометить задачу как `DOING`
- [ ] TODO — после завершения пометить задачу как `DONE`
- [ ] TODO — если задача не может быть выполнена, пометить `BLOCKED` и указать причину
- [ ] TODO — если нужна проверка человека, пометить `REVIEW`
- [ ] TODO — не удалять историю решений из `11_RISKS_AND_DECISIONS.md`

## MVP milestone summary

| Milestone | Статус | Смысл |
|---|---:|---|
| MVP 0 | DONE | WinUI shell, tray, settings, empty services UI |
| MVP 1 | DONE | локальный mock service |
| MVP 2 | DONE | package manager и `.svcpkg` |
| MVP 3 | DONE | Agent + IPC |
| MVP 4 | REVIEW | GitHub service catalog |
| MVP 5 | DOING | VPN service без подписок |
| MVP 6 | TODO | VPN subscription URL |
| MVP 7 | REVIEW | обновления приложения, Agent и сервисов реализованы; требуется проверка публичного release |

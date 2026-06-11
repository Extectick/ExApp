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
- [x] DONE — добавлен базовый Service Runtime
- [ ] REVIEW — подключён GitHub service catalog
- [ ] TODO — реализован первый VPN service
- [ ] TODO — подключены обновления через Velopack

## Сейчас выполняется

- [x] DONE — подготовить репозиторий и базовые проекты
- [x] DONE — перенести этот план в `/docs`
- [ ] REVIEW — реализовать MVP 0 Desktop Skeleton
- [ ] REVIEW — реализовать MVP 1 Local Mock Service
- [x] DONE — реализовать MVP 2 Package Manager
- [x] DONE — реализовать MVP 3 Agent + IPC
- [x] DONE — стабилизировать Service Runtime

## Следующий лучший шаг

Завершить **MVP 4 — GitHub Service Catalog** из [`04_MVP_ROADMAP.md`](04_MVP_ROADMAP.md): подключить реальный GitHub Releases URL для mock package и добавить basic catalog signing placeholder.

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
| MVP 0 | REVIEW | WinUI shell, tray, settings, empty services UI |
| MVP 1 | REVIEW | локальный mock service |
| MVP 2 | DONE | package manager и `.svcpkg` |
| MVP 3 | DONE | Agent + IPC |
| MVP 4 | REVIEW | GitHub service catalog |
| MVP 5 | TODO | VPN service без подписок |
| MVP 6 | TODO | VPN subscription URL |
| MVP 7 | TODO | обновления приложения и сервисов |

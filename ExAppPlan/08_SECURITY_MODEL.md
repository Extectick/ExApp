# 08 — Security Model

## Главный риск

Приложение скачивает и запускает исполняемые сервисы. Это supply chain risk.

Поэтому security model должна быть заложена до реализации VPN.

## Security principles

- [ ] TODO — сервисы только из trusted sources
- [ ] TODO — все пакеты имеют sha256
- [ ] TODO — все пакеты подписаны
- [ ] TODO — каталог сервисов подписан
- [ ] TODO — неподписанные пакеты запрещены по умолчанию
- [ ] TODO — Desktop не работает постоянно от администратора
- [ ] TODO — UI не выполняет privileged operations
- [ ] TODO — секреты не логируются
- [ ] TODO — secrets хранятся через DPAPI
- [ ] TODO — diagnostic bundle делает redaction

## Trust model

### Publisher

```json
{
  "publisherId": "myapp",
  "publisherName": "MyApp",
  "publicKeyId": "myapp-prod-2026",
  "trusted": true
}
```

## Install-time security checks

- [ ] TODO — URL должен быть HTTPS
- [ ] TODO — package hash должен совпадать с catalog
- [ ] TODO — catalog signature должна быть валидной
- [ ] TODO — package signature должна быть валидной
- [ ] TODO — publisher должен быть trusted
- [ ] TODO — manifest id должен совпадать с catalog id
- [ ] TODO — version должна совпадать
- [ ] TODO — platform/architecture должны совпадать
- [ ] TODO — service permissions должны быть показаны пользователю

## IPC security

- [ ] TODO — Named Pipe должен иметь ACL
- [ ] TODO — не принимать команды от чужого пользователя
- [ ] TODO — request envelope должен иметь correlationId
- [ ] TODO — Agent должен логировать command audit без секретов
- [ ] TODO — dangerous commands должны проверять permission
- [ ] TODO — service process не должен получать лишние права

## Permission UI

Перед установкой показывать:

```text
VPN Client запрашивает разрешения:

✓ Доступ к сети
✓ Работа в фоне
✓ Управление DNS
✓ Создание TUN/VPN туннеля
✓ Изменение маршрутов
✓ Firewall operations

[Установить] [Отмена]
```

## Secret redaction rules

Заменять на `***`:

- [ ] TODO — query parameter `token`
- [ ] TODO — query parameter `key`
- [ ] TODO — query parameter `password`
- [ ] TODO — UUID-like secrets
- [ ] TODO — private keys
- [ ] TODO — bearer tokens
- [ ] TODO — subscription URLs

## Safe mode

Запуск:

```text
MyApp.Desktop.exe --safe-mode
```

В safe mode:

- [ ] TODO — не запускать сервисы
- [ ] TODO — не применять обновления сервисов автоматически
- [ ] TODO — показать installed services
- [ ] TODO — дать удалить проблемный сервис
- [ ] TODO — дать rollback сервиса
- [ ] TODO — экспортировать диагностику

## Security checklist for MVP

- [ ] TODO — запрет сторонних сервисов
- [ ] TODO — только собственный publisher
- [ ] TODO — sha256 validation
- [ ] TODO — manifest validation
- [ ] TODO — secret redaction
- [ ] TODO — DPAPI for subscription URL
- [ ] TODO — safe mode
- [ ] TODO — rollback
- [ ] TODO — basic IPC ACL

## Security checklist after MVP

- [ ] TODO — real package signing
- [ ] TODO — catalog signing
- [ ] TODO — key rotation
- [ ] TODO — revocation list
- [ ] TODO — transparency log
- [ ] TODO — stronger service isolation
- [ ] TODO — audit log viewer

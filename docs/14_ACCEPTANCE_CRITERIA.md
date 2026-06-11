# 14 — Acceptance Criteria

## Public MVP acceptance

Публичный MVP считается готовым, если выполнены все пункты.

### Installation

- [ ] TODO — пользователь может скачать installer
- [ ] TODO — приложение устанавливается без ручных действий
- [ ] TODO — приложение запускается после установки
- [ ] TODO — приложение можно удалить
- [ ] TODO — пользовательские данные не теряются при обновлении

### Desktop shell

- [ ] TODO — есть современный WinUI 3 UI
- [ ] TODO — есть список установленных сервисов
- [ ] TODO — есть Браузер сервисов
- [ ] TODO — есть Настройки
- [ ] TODO — есть Диагностика
- [ ] TODO — приложение умеет работать в трее
- [ ] TODO — повторный запуск не создаёт вторую копию

### Service Browser

- [ ] TODO — каталог сервисов загружается
- [ ] TODO — VPN Client отображается в каталоге
- [ ] TODO — пользователь видит описание сервиса
- [ ] TODO — пользователь видит permissions
- [ ] TODO — пользователь может установить VPN Client
- [ ] TODO — установленный сервис отображается в “Мои сервисы”

### Service runtime

- [ ] TODO — сервис запускается отдельным процессом
- [ ] TODO — сервис останавливается
- [ ] TODO — статус обновляется
- [ ] TODO — логи доступны в UI
- [ ] TODO — падение сервиса не роняет Desktop
- [ ] TODO — safe mode позволяет отключить проблемный сервис

### VPN

- [ ] TODO — пользователь может открыть VPN Client
- [ ] TODO — пользователь может добавить subscription URL
- [ ] TODO — subscription URL хранится защищённо
- [ ] TODO — поддерживается минимум один формат подписки
- [ ] TODO — пользователь видит список серверов
- [ ] TODO — пользователь может выбрать сервер
- [ ] TODO — пользователь может подключиться
- [ ] TODO — пользователь может отключиться
- [ ] TODO — UI показывает статус
- [ ] TODO — секреты не попадают в логи

### Updates

- [ ] TODO — приложение проверяет обновления
- [ ] TODO — приложение обновляется через GitHub Releases
- [ ] TODO — сервисы проверяют обновления отдельно
- [ ] TODO — VPN Client может обновиться отдельно от приложения
- [ ] TODO — неудачное обновление сервиса откатывается

### Security

- [ ] TODO — package sha256 проверяется
- [ ] TODO — manifest валидируется
- [ ] TODO — несовместимые версии отклоняются
- [ ] TODO — permissions показываются до установки
- [ ] TODO — logs redaction работает
- [ ] TODO — diagnostic export не содержит secrets
- [ ] TODO — неподписанные production packages запрещены

### QA

- [ ] TODO — unit tests для Packaging
- [ ] TODO — unit tests для Core contracts
- [ ] TODO — integration tests для Agent/IPC
- [ ] TODO — manual QA checklist пройден
- [ ] TODO — diagnostic bundle проверен
- [ ] TODO — clean install проверен
- [ ] TODO — update from previous version проверен

## Non-goals for public MVP

Эти пункты не должны блокировать MVP:

- [ ] TODO — полноценный kill switch
- [ ] TODO — split tunneling
- [ ] TODO — поддержка всех подписок
- [ ] TODO — third-party marketplace
- [ ] TODO — account system
- [ ] TODO — payments
- [ ] TODO — cloud sync
- [ ] TODO — mobile apps

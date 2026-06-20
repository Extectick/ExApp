# 14 — Acceptance Criteria

## Public MVP acceptance

Публичный MVP считается готовым, если выполнены все пункты.

### Installation

- [ ] TODO — пользователь может скачать installer
- [ ] TODO — приложение устанавливается без ручных действий
- [ ] TODO — приложение запускается после установки
- [ ] TODO — приложение можно удалить
- [x] DONE — пользовательские данные не теряются при обновлении

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

- [x] DONE — приложение проверяет обновления
- [x] DONE — приложение и Agent обновляются через GitHub Releases
- [x] DONE — приложение поддерживает delta update по файлам и фрагментам файлов
- [x] DONE — app delta update откатывается или переходит на full fallback при поврежденной локальной базе
- [x] DONE — сервисы проверяют обновления отдельно
- [x] DONE — любой сервис каталога может обновиться отдельно от приложения
- [x] DONE — сервисы поддерживают delta update по файлам и фрагментам файлов
- [x] DONE — неудачное обновление сервиса откатывается

### Security

- [x] DONE — package sha256 проверяется
- [x] DONE — manifest валидируется
- [x] DONE — несовместимые версии отклоняются
- [ ] TODO — permissions показываются до установки
- [ ] TODO — logs redaction работает
- [ ] TODO — diagnostic export не содержит secrets
- [x] DONE — неподписанные production packages запрещены release pipeline preflight

### QA

- [x] DONE — unit tests для Packaging
- [ ] TODO — unit tests для Core contracts
- [x] DONE — integration tests для Agent/IPC
- [ ] TODO — manual QA checklist пройден
- [ ] TODO — diagnostic bundle проверен
- [ ] TODO — clean install проверен
- [x] DONE — update from previous version и rollback проверены локально

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

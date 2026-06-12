# 11 — Risks & Decisions

## Architectural Decision Records

### ADR-001 — WinUI 3 for Desktop UI

Status: Accepted

- [x] DONE — использовать WinUI 3 для Windows desktop shell

Reason:

- modern Windows UI;
- native look;
- хорошая интеграция с Windows App SDK;
- подходит для Windows 11-style приложения.

### ADR-002 — Services as external packages

Status: Accepted

- [x] DONE — сервисы поставляются как `.svcpkg`, а не встроены в основной installer

Reason:

- базовое приложение остаётся лёгким;
- пользователь ставит только нужные сервисы;
- сервисы обновляются отдельно;
- можно развивать Service Browser.

### ADR-003 — Services run as separate processes

Status: Accepted

- [x] DONE — не загружать сервисы как DLL внутрь Desktop

Reason:

- падение сервиса не роняет UI;
- проще обновлять;
- проще изолировать;
- проще логировать;
- проще контролировать lifecycle.

### ADR-004 — Agent owns service lifecycle

Status: Accepted

- [x] DONE — Desktop не запускает сервисы напрямую, этим занимается Agent

Reason:

- VPN требует privileged operations;
- UI должен оставаться обычным пользовательским процессом;
- Agent может работать в фоне.

### ADR-005 — Declarative service UI for MVP

Status: Accepted

- [x] DONE — сервисы поставляют UI schema, а не WinUI plugin DLL

Reason:

- безопаснее;
- единый стиль;
- меньше конфликтов зависимостей;
- проще обновлять сервисы.

### ADR-006 — sing-box first for VPN

Status: Accepted for MVP, Review later

- [x] DONE — первым VPN core рассматривать sing-box

Reason:

- лучше подходит под subscription links;
- WireGuard можно добавить как отдельный режим позже.

### ADR-007 — External updater for app updates

Status: Accepted

- [x] DONE — использовать ExApp.Updater + GitHub Releases для обновлений приложения и Agent

## Risk register

### RISK-001 — Supply chain compromise

Impact: Critical  
Probability: Medium

- [ ] TODO — HTTPS only
- [ ] TODO — sha256 validation
- [ ] TODO — package signing
- [ ] TODO — catalog signing
- [ ] TODO — trusted publishers
- [ ] TODO — запрет unsigned packages

### RISK-002 — VPN требует admin rights

Impact: High  
Probability: High

- [ ] TODO — UI не запускать elevated
- [ ] TODO — privileged Agent
- [ ] TODO — понятный permission prompt
- [ ] TODO — обработать отказ elevation
- [ ] TODO — показать `NeedsPermission`

### RISK-003 — Сервис ломает запуск приложения

Impact: High  
Probability: Medium

- [ ] TODO — safe mode
- [ ] TODO — service crash detection
- [ ] TODO — disable auto-start после N падений
- [ ] TODO — rollback

### RISK-004 — Subscription format explosion

Impact: Medium  
Probability: High

- [ ] TODO — поддержать один формат в MVP
- [ ] TODO — сделать parser interface
- [ ] TODO — добавить unsupported format error
- [ ] TODO — расширять форматы постепенно

### RISK-005 — Secret leaks in logs

Impact: High  
Probability: Medium

- [ ] TODO — redaction library
- [ ] TODO — no full config logging
- [ ] TODO — diagnostic bundle redaction
- [ ] TODO — tests for redaction

### RISK-006 — Update breaks service compatibility

Impact: High  
Probability: Medium

- [ ] TODO — apiVersion
- [ ] TODO — minAppVersion
- [ ] TODO — minAgentVersion
- [ ] TODO — compatibility check before update
- [ ] TODO — rollback

### RISK-007 — Marketplace scope creep

Impact: High  
Probability: High

- [ ] TODO — MVP only first-party services
- [ ] TODO — no third-party publisher support in MVP
- [ ] TODO — no payments/accounts in MVP
- [ ] TODO — focus on one VPN service

## Open questions

- [ ] TODO — какой первый subscription format поддерживать?
- [ ] TODO — нужен ли Windows Service уже в MVP или достаточно Agent process?
- [ ] TODO — как именно подписывать `.svcpkg` в первой версии?
- [ ] TODO — какой hosting catalog: raw GitHub, GitHub Pages, CDN?
- [ ] TODO — какой retention policy для старых service versions?

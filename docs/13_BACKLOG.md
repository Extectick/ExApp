# 13 — Backlog

## Epic A — Desktop Shell

- [x] DONE — A001 Create WinUI 3 project
- [x] DONE — A002 Add NavigationView
- [x] DONE — A003 Add Services page
- [x] DONE — A004 Add Service Browser page
- [x] DONE — A005 Add Settings page
- [x] DONE — A006 Add Diagnostics page
- [x] DONE — A007 Add theme switching
- [x] DONE — A008 Add single instance
- [x] DONE — A009 Add tray icon
- [x] DONE — A010 Add close-to-tray behavior
- [ ] TODO — A011 Add basic app notifications
- [ ] TODO — A012 Add logs viewer component
- [x] DONE — A013 Add JSON localization foundation
- [x] DONE — A014 Add en/ru localization files
- [x] DONE — A015 Add language selector in Settings
- [x] DONE — A016 Use Windows language with en fallback

## Epic B — Core Contracts

- [x] DONE — B001 Define ServiceState enum
- [x] DONE — B002 Define ServiceStatus DTO
- [ ] TODO — B003 Define Result/Error model
- [ ] TODO — B004 Define Version compatibility helpers
- [ ] TODO — B005 Define Permission model
- [ ] TODO — B006 Define Catalog DTO
- [ ] TODO — B007 Define Manifest DTO
- [ ] TODO — B008 Define UI schema DTO
- [ ] TODO — B009 Define command envelope
- [ ] TODO — B010 Define redaction utility

## Epic C — Mock Service

- [x] DONE — C001 Create MockService executable
- [x] DONE — C002 Add mock manifest
- [x] DONE — C003 Add start command
- [x] DONE — C004 Add stop command
- [x] DONE — C005 Add status command
- [x] DONE — C006 Add heartbeat logs
- [x] DONE — C007 Package as local `.svcpkg`

## Epic D — Package Manager

- [x] DONE — D001 Define `.svcpkg` structure
- [x] DONE — D002 Implement package reader
- [x] DONE — D003 Implement manifest validation
- [x] DONE — D004 Implement checksum validation
- [x] DONE — D005 Implement staging install
- [x] DONE — D006 Implement currentVersion pointer
- [x] DONE — D007 Implement rollback
- [x] DONE — D008 Implement uninstall
- [x] DONE — D009 Implement package cache
- [x] DONE — D010 Add tests

## Epic E — Agent + IPC

- [x] DONE — E001 Create Agent project
- [x] DONE — E002 Add Named Pipe server
- [x] DONE — E003 Add Named Pipe client
- [x] DONE — E004 Add service.list
- [x] DONE — E005 Add service.install
- [x] DONE — E006 Add service.uninstall
- [x] DONE — E007 Add service.start
- [x] DONE — E008 Add service.stop
- [x] DONE — E009 Add service.status
- [x] DONE — E010 Add service.logs
- [x] DONE — E011 Add pipe ACL
- [x] DONE — E012 Add Agent auto-start strategy

## Epic F — Service Runtime

- [x] DONE — F001 Add service registry
- [x] DONE — F002 Add process launcher
- [x] DONE — F003 Add process monitor
- [x] DONE — F004 Add health checks
- [x] DONE — F005 Add crash detection
- [x] DONE — F006 Add restart policy
- [x] DONE — F007 Add safe mode
- [x] DONE — F008 Add service logs routing

## Epic G — Service Catalog

- [ ] TODO — G001 Define catalog schema
- [ ] TODO — G002 Create services.stable.json
- [ ] TODO — G003 Add catalog downloader
- [ ] TODO — G004 Add catalog cache
- [ ] TODO — G005 Add offline fallback
- [ ] TODO — G006 Show catalog in Browser
- [ ] TODO — G007 Install package from URL
- [ ] TODO — G008 Validate catalog package hash
- [ ] TODO — G009 Prepare catalog signing placeholder

## Epic H — VPN Service

- [ ] TODO — H001 Create VpnClient service executable
- [ ] TODO — H002 Add VPN manifest
- [ ] TODO — H003 Add service-ui.json
- [ ] TODO — H004 Add sing-box binary handling
- [ ] TODO — H005 Add static config start
- [ ] TODO — H006 Add core process stop
- [ ] TODO — H007 Add status mapping
- [ ] TODO — H008 Add log redaction
- [ ] TODO — H009 Add subscription storage
- [ ] TODO — H010 Add DPAPI protection
- [ ] TODO — H011 Add subscription downloader
- [ ] TODO — H012 Add one parser
- [ ] TODO — H013 Add nodes list
- [ ] TODO — H014 Add config generator
- [ ] TODO — H015 Add selected node connection

## Epic I — Updates

- [ ] TODO — I001 Add Velopack package
- [ ] TODO — I002 Initialize Velopack at app startup
- [ ] TODO — I003 Add app update check
- [ ] TODO — I004 Add update download/apply UI
- [ ] TODO — I005 Add GitHub Actions app release
- [ ] TODO — I006 Add service update detection
- [ ] TODO — I007 Add service update install
- [ ] TODO — I008 Add service update rollback
- [ ] TODO — I009 Add channels

## Epic J — Security

- [ ] TODO — J001 Add secret redaction
- [ ] TODO — J002 Add DPAPI wrapper
- [ ] TODO — J003 Add package sha256 validation
- [ ] TODO — J004 Add manifest compatibility validation
- [ ] TODO — J005 Add permission screen
- [ ] TODO — J006 Add IPC ACL
- [ ] TODO — J007 Add diagnostic bundle redaction
- [ ] TODO — J008 Add package signing
- [ ] TODO — J009 Add catalog signing
- [ ] TODO — J010 Add trusted publishers

## Epic K — Diagnostics

- [ ] TODO — K001 Add Diagnostics page
- [ ] TODO — K002 Add app info
- [ ] TODO — K003 Add agent info
- [ ] TODO — K004 Add services snapshot
- [ ] TODO — K005 Add logs viewer
- [ ] TODO — K006 Add export diagnostic bundle
- [ ] TODO — K007 Add update history
- [ ] TODO — K008 Add integrity check

# 10 — Testing & QA Plan

## Цель

Проверять не только UI, но и критические сценарии: установка, обновление, rollback, IPC, VPN process lifecycle, secret redaction.

## Unit tests

### Core

- [ ] TODO — version comparison
- [ ] TODO — result/error model
- [ ] TODO — path resolution
- [ ] TODO — secret redaction
- [ ] TODO — permission model

### Packaging

- [ ] TODO — parse valid manifest
- [ ] TODO — reject invalid manifest
- [ ] TODO — reject unsupported apiVersion
- [ ] TODO — reject incompatible minAppVersion
- [ ] TODO — validate checksums
- [ ] TODO — reject corrupted package

### Catalog

- [ ] TODO — parse valid catalog
- [ ] TODO — reject invalid catalog
- [ ] TODO — match platform/architecture
- [ ] TODO — detect update available
- [ ] TODO — handle offline cache

## Integration tests

### Agent + Package Manager

- [ ] TODO — install mock service
- [ ] TODO — uninstall mock service
- [ ] TODO — update mock service
- [ ] TODO — rollback failed update
- [ ] TODO — preserve data on uninstall
- [ ] TODO — delete data when requested

### IPC

- [ ] TODO — Desktop connects to Agent
- [ ] TODO — command request/response works
- [ ] TODO — invalid command rejected
- [ ] TODO — command timeout works
- [ ] TODO — logs streaming works
- [ ] TODO — unauthorized client rejected

### Service Runtime

- [ ] TODO — start service
- [ ] TODO — stop service
- [ ] TODO — detect crash
- [ ] TODO — restart policy works
- [ ] TODO — health check timeout works
- [ ] TODO — safe mode disables service startup

## VPN tests

### MVP static config

- [ ] TODO — service starts core process
- [ ] TODO — service stops core process
- [ ] TODO — status changes to running
- [ ] TODO — status changes to stopped
- [ ] TODO — crash is detected
- [ ] TODO — logs are redacted

### Subscription

- [ ] TODO — valid subscription URL saved via DPAPI
- [ ] TODO — invalid URL rejected
- [ ] TODO — network error handled
- [ ] TODO — supported format parsed
- [ ] TODO — unsupported format returns clear error
- [ ] TODO — generated config does not leak into logs

## Manual QA checklist

### Install app

- [ ] TODO — clean install works
- [ ] TODO — install over old version works
- [ ] TODO — uninstall works
- [ ] TODO — app launches after install
- [ ] TODO — app starts after reboot

### Desktop UI

- [ ] TODO — single instance works
- [ ] TODO — tray works
- [ ] TODO — close to tray works
- [ ] TODO — theme setting persists
- [ ] TODO — diagnostics page loads

### Service Browser

- [ ] TODO — catalog loads
- [ ] TODO — offline catalog error is clear
- [ ] TODO — service details page opens
- [ ] TODO — permissions are shown
- [ ] TODO — install button works
- [ ] TODO — uninstall button works

### Updates

- [ ] TODO — app update is detected
- [ ] TODO — app update downloads
- [ ] TODO — app restarts after update
- [ ] TODO — service update is detected
- [ ] TODO — service update can rollback

## Diagnostic bundle

Diagnostic bundle должен содержать:

- [ ] TODO — app version
- [ ] TODO — agent version
- [ ] TODO — OS info
- [ ] TODO — installed services
- [ ] TODO — service manifests
- [ ] TODO — package state
- [ ] TODO — redacted app logs
- [ ] TODO — redacted service logs
- [ ] TODO — update logs

Diagnostic bundle не должен содержать:

- [ ] TODO — subscription URL
- [ ] TODO — private keys
- [ ] TODO — passwords
- [ ] TODO — tokens
- [ ] TODO — full generated VPN config

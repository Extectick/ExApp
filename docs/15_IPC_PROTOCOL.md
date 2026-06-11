# 15 — Agent IPC Protocol

## Transport

- Named Pipe: `ExApp.Agent.v1`
- Access: current Windows user only (`PipeOptions.CurrentUserOnly`)
- Framing: one UTF-8 JSON object per line
- Lifecycle: one request and one response per connection

## Request

```json
{
  "requestId": "a unique id",
  "command": "service.status",
  "payload": {
    "serviceId": "mock-service"
  }
}
```

## Success response

```json
{
  "requestId": "the request id",
  "success": true,
  "result": {}
}
```

## Error response

```json
{
  "requestId": "the request id",
  "success": false,
  "error": {
    "code": "service.commandFailed",
    "message": "Human-readable error."
  }
}
```

## MVP 3 commands

- `agent.ping`
- `service.list`
- `service.install`
- `service.uninstall`
- `service.start`
- `service.stop`
- `service.status`
- `service.logs`
- `service.clearLogs`
- `service.rollback`

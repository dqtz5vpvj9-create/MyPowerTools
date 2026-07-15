# `tool.json` reference

Every standalone tool has one root `tool.json` validated by `schemas/tool.schema.json`.

| Field | Purpose |
| --- | --- |
| `schemaVersion`, `version` | Manifest contract and tool release versions. |
| `toolId`, `title`, `description` | Stable identity and product copy. |
| `type` | `web-surface`, `dotnet-surface`, `native-tool`, or `headless-tool`. |
| `primaryRouteId`, `routes` | Shell destinations. Each route contains a `surface`. |
| `runtime` | `none`, `loopback-http`, `stdio-jsonrpc`, or `named-pipe-grpc`. |
| `settings` | Schema, ordinary value file, and secret property names. |
| `commands` | Health, refresh, log, product, and external-open actions. |
| `permissions` | Requested host capabilities. |
| `development` | Loose discovery and refresh hints. |

Relative paths are resolved under the tool directory and cannot escape it. HTTP URLs may target localhost or a remote system. `ownerModuleId` defaults to `toolId`.

```json
{
  "schemaVersion": "1.0",
  "version": "0.1.0",
  "toolId": "example.panel",
  "title": "Example Panel",
  "description": "Existing web administration panel",
  "type": "web-surface",
  "availability": "available",
  "primaryRouteId": "main",
  "routes": [{
    "routeId": "main",
    "surfaceId": "example.panel.main",
    "title": "Overview",
    "surface": { "kind": "web", "source": "http://127.0.0.1:43110/", "openExternal": true }
  }],
  "runtime": { "transport": "loopback-http", "endpoint": "http://127.0.0.1:43110", "healthPath": "/api/status", "timeoutMs": 5000 },
  "settings": { "schema": "settings.schema.json", "values": "settings.json", "secrets": ["apiSecret"] },
  "commands": [{ "id": "example.panel.health", "title": "Check health", "method": "GET", "path": "/api/status" }]
}
```

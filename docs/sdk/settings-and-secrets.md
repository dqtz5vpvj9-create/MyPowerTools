# Settings and Secret Store

`settings.schema.json` is JSON Schema 2020-12. `settings.json` contains portable, non-secret values. Properties marked with `x-mpt-secret: true` and listed in `tool.json.settings.secrets` are stored through the host Secret Store.

```json
{
  "type": "object",
  "required": ["panelUrl"],
  "properties": {
    "panelUrl": { "type": "string", "format": "uri", "title": "Panel URL" },
    "apiSecret": { "type": "string", "title": "API secret", "x-mpt-secret": true },
    "timeoutMs": { "type": "integer", "minimum": 100, "default": 5000 }
  }
}
```

The tool receives a secret reference or bridge result at runtime. Packages and source manifests exclude resolved secret values. A developer may commit `settings.json`; local secret material stays under the platform credential store.

# Python gRPC sidecar template

Use this legacy module-package template when a runtime implements `mpt_module_v1.proto` over a named pipe or Unix domain socket. New standalone tools should start with:

```powershell
mypowertools create tool --type headless --id example.python --output C:\work\example-python
```

The Python process owns lifecycle, health, commands, logs, and events. Its user interface may be a web surface declared by `tool.json`.

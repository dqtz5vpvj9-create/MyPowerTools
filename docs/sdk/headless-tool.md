# Headless Tool

`headless-tool` publishes commands, settings, health, logs, and events without an embedded product page. It appears as a real tool entry with a host-rendered workspace.

```powershell
mypowertools create tool --type headless --id example.worker --output C:\src\example-worker
python C:\src\example-worker\runtime.py
```

The generated implementation uses one-line stdio JSON-RPC. Replace it with named-pipe gRPC or loopback HTTP by changing the runtime descriptor and implementing the protocol bundle.

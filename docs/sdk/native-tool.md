# Native Tool

`native-tool` represents a complete native application with a Suite entry, settings, health, logs, events, and commands. The implementation language is unrestricted.

```powershell
mypowertools create tool --type native --id example.rust --output C:\src\example-rust
mypowertools validate tool C:\src\example-rust
```

Set `routes[].surface.kind` to `native` and `source` to the executable or launch script. Use `runtime.transport` for background communication. Rust, C++, Go, Python, and .NET programs can implement stdio JSON-RPC, named-pipe gRPC, or HTTP.

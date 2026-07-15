# Debugging and troubleshooting

Validate the manifest and local files:

```powershell
mypowertools validate tool C:\src\example-tool
```

Confirm discovery roots:

```powershell
$env:MPT_TOOL_DIRS = 'C:\src;D:\experiments'
dotnet run --project .\src\MyPowerTools.Runner -- --once --tool-dir C:\src
```

For web tools, request the panel and health endpoint directly. Check WebView2 availability if the recovery page reports host initialization failure. Use **Open externally** to distinguish panel reachability from embedding failure.

For stdio tools, send one test vector as one line and verify one response line. Keep logging on stderr. For named-pipe gRPC, generate code from `artifacts\sdk\protocol\mypowertools-protocol-0.2.0.zip` and verify protocol version 1.0.

Click **Refresh tools** after creating, moving, editing, or deleting `tool.json`. Catalog refresh reports schema and duplicate-ID failures through Runner logs.

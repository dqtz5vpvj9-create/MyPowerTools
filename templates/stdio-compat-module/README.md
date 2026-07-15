# stdio JSON-RPC template

Use this template for a process that reads one JSON request per line from stdin and writes one JSON response per line to stdout. Diagnostic output belongs on stderr.

```powershell
mypowertools create tool --type native --id example.native --output C:\work\example-native
```

The generated `runtime.ps1` demonstrates health, logging, and command responses without a repository reference.

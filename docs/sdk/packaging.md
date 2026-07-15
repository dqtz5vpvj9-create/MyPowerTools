# Packaging and installation

Development mode scans source directories directly and accepts dirty files, arbitrary branches, and manual output folders.

```powershell
mypowertools validate tool C:\src\example-tool
mypowertools pack tool C:\src\example-tool --output C:\dist\example-tool.mptpkg
```

An `.mptpkg` is a ZIP archive containing the tool source/artifacts, `tool.json`, schemas, and `source-manifest.json`. The source manifest records the Git commit when available, dirty state, and SHA-256 for every packed file. It does not block packaging.

Place private configuration and platform-only payloads in `.mptignore`. Patterns use simple `*` wildcards and directory prefixes. Secret values always belong in the host Secret Store.

For Suite distribution, build the tool first, create its `.mptpkg`, and place it beside the Suite installer payload. Independent releases may publish the same package without rebuilding MyPowerTools.

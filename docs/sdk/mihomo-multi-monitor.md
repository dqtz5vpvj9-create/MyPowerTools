# Mihomo Multi Monitor integration contract

Source repository:

`C:\Users\lixinrui\Documents\Codex\2026-06-19\ubuntu-home-lixinrui-repo-mihomo-l2blug6i7scw\work\status-monitor`

The existing Linux service remains remote. MyPowerTools opens its management panel as a Web Surface and calls its HTTP API.

Required settings:

- `panelUrl`: existing management page, for example `http://linux-host:19090/`.
- `apiEndpoint`: API base URL, usually the same origin.
- `apiSecret`: Secret Store value.
- `connectionTimeoutMs`: probe timeout.
- `autoRefresh`: automatic panel refresh.

Required commands are `health`, `refresh`, `open-external`, and `tail-logs`. The web route runs through WebToolHost. An unreachable Linux host displays the recovery page and leaves Shell running.

Development may point directly to the current HTTP URL. Release packaging:

```powershell
mypowertools validate tool C:\path\to\status-monitor
mypowertools pack tool C:\path\to\status-monitor --output C:\dist\mihomo-multi-monitor.mptpkg
```

The package contains the Windows integration descriptor and optional web assets; it does not copy or install the Linux systemd service.

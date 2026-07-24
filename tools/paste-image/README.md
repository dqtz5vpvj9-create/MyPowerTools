# Paste Image

Paste Image replaces the AutoHotkey `Ctrl+Alt+V` launcher with a MyPowerTools module.

When the shortcut runs, the module:

1. reads the clipboard image through the active platform provider;
2. saves a temporary PNG;
3. creates the configured remote directory and uploads the PNG with the platform OpenSSH client;
4. replaces the clipboard with the remote path;
5. publishes a MyPowerTools success or failure notification.

Defaults:

- shortcut: `Ctrl+Alt+V`;
- SSH host: `chris`;
- remote directory: `/tmp`;
- timeout: 30 seconds.

Windows uses the Win32 Clipboard and the system OpenSSH client. macOS uses native NSPasteboard and `/usr/bin/ssh`. The host, directory, timeout, shortcut, and enabled state can be changed in MyPowerTools settings. OpenSSH authentication stays in the current user's SSH configuration. Batch mode prevents a hidden upload from waiting for an interactive password prompt.

The macOS global hotkey provider is still pending. Paste Image remains available from the Shell and command palette when the module is enabled.

Build from the repository root:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\tools\paste-image\build.ps1 -MyPowerToolsRepoRoot .
```

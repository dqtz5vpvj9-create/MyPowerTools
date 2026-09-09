# Paste Image

Paste Image replaces the AutoHotkey `Ctrl+Alt+V` launcher with a MyPowerTools module.

When the shortcut runs, the module:

1. reads the clipboard image through the active platform provider;
2. saves a temporary PNG;
3. creates the configured remote directory and uploads the PNG with the platform OpenSSH client;
4. replaces the clipboard with the remote path;
5. sends a configurable paste shortcut to the foreground app (default `Ctrl+Shift+V`);
6. publishes a MyPowerTools success or failure notification.

Defaults:

- shortcut: `Ctrl+Alt+V`;
- after-upload shortcut: `Ctrl+Shift+V`;
- SSH host: `chris`;
- remote directory: `/tmp`;
- timeout: 30 seconds.

Windows uses the Win32 Clipboard and the system OpenSSH client. macOS uses native NSPasteboard and `/usr/bin/ssh`. The host, directory, timeout, upload shortcut, after-upload shortcut, and enabled state can be changed in MyPowerTools settings. Leave the after-upload shortcut empty to disable automatic pasting. OpenSSH authentication stays in the current user's SSH configuration. Batch mode prevents a hidden upload from waiting for an interactive password prompt.

macOS global hotkeys use a blocking Carbon event queue. Automatic pasting additionally requires Accessibility permission for the MyPowerTools Runner helper app; a successful upload still copies the remote path when that permission is missing. Choose an after-upload shortcut appropriate for the destination app (normally `Cmd+V` on macOS). The Shell and command palette also expose the upload action.

Build from the repository root:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\tools\paste-image\build.ps1 -MyPowerToolsRepoRoot .
```

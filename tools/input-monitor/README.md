# Input Monitor

Input Monitor is a first-party MyPowerTools port of the macOS status-bar InputMonitor app.

It collects local keyboard, mouse, and foreground-window activity, stores daily statistics in SQLite, and reminds you to rest after continuous work. Character mapping is skipped in privacy mode. Mouse movement is sampled from a 30 Hz cursor poll with the original 30 px / 50 ms dual threshold.

Windows capture uses low-level keyboard and mouse hooks plus `GetCursorPos`. The rest overlay is a topmost layered window; Esc skips the current rest and raises the next threshold to 120.

Build from the repository root:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\tools\input-monitor\build.ps1 -MyPowerToolsRepoRoot .
```

Overlay into the Dev install:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId input-monitor
```

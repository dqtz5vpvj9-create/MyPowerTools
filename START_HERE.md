# Start MyPowerTools

For normal use, open PowerShell as administrator in this folder, run `install-windows.ps1` once, then open `MyPowerTools` from the Windows Start menu. The app installs to `%ProgramFiles%\MyPowerTools`; user data remains in `%LOCALAPPDATA%\MyPowerTools`.

```powershell
pwsh.exe -NoLogo -NoProfile -File .\install-windows.ps1 -EnableAutostart -StartRunner -DesktopShortcut
```

After installation, the Start menu shows a single entry:

```text
MyPowerTools
```

Read-only portable use without installation:

```cmd
MyPowerTools.exe
```

`Start-MyPowerTools.cmd` is also available for environments that prefer a script entry point.

ADB portproxy writes are disabled in portable and developer layouts because those directories do not provide the ACL-protected Broker trust root.

The app starts the Runner in the background and opens the Shell. The tray icon keeps MyPowerTools running, with menu actions for opening MyPowerTools and quitting the Runner.

Advanced tools stay inside the package:

- `Cli\MyPowerTools.Cli.exe` is for command-line automation.
- `Runner\MyPowerTools.Runner.exe` is the background control plane.
- `Shell\MyPowerTools.Shell.Avalonia.exe` is the desktop UI used by the main app shortcut.

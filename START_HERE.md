# Start MyPowerTools

For normal use, install once from a regular PowerShell window, then open `MyPowerTools` from the Windows Start menu. The app installs to `%LOCALAPPDATA%\Programs\MyPowerTools`; user data remains in `%LOCALAPPDATA%\MyPowerTools`.

From a source checkout, the installer builds the current portable package first:

```powershell
pwsh scripts/install-windows.ps1
```

This single command performs a clean publish, validates the package, installs it for the current user, configures startup, starts the runtime, and opens MyPowerTools.

From an extracted portable package:

```powershell
pwsh ./install-windows.ps1
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

ADB portproxy writes are enabled in the installed user layout. The dedicated Broker requests Windows UAC automatically when an approved port change needs administrator rights. Portable and developer layouts keep privileged writes disabled.

The app starts the Runner in the background and opens the Shell. The tray icon is the user-visible presence of MyPowerTools: open the Shell from it, and Exit MyPowerTools from it. Runner and ServiceManager are windowless Windows processes; they do not show a console.

Advanced tools stay inside the package:

- `Cli\MyPowerTools.Cli.exe` is for command-line automation.
- `Runner\MyPowerTools.Runner.exe` is the background control plane.
- `Shell\MyPowerTools.Shell.Avalonia.exe` is the desktop UI used by the main app shortcut.

# MyPowerTools Native Win32 Bootstrapper (`mpt-bootstrap.exe`)

A `< 50 KB`, zero-`.NET`, zero-CRT pure Win32 C GUI bootstrapper and one-click `.NET 8.0+` user-local runtime installer for standalone and shell-hosted MyPowerTools executables on Windows x64.

## Key Capabilities

1. **Ultra-Small & Zero External Dependencies (`~25 KB`)**:
   - Cross-compiled via `zig cc -target x86_64-windows-gnu` with `-nostdlib` (`WinMainCRTStartup`).
   - Imports **only** built-in Windows OS DLLs (`KERNEL32.dll`, `USER32.dll`, `GDI32.dll`, `COMCTL32.dll`, `SHELL32.dll`, `ADVAPI32.dll`, `WINHTTP.dll`).
   - Embeds Common Controls v6 modern visual styles, `asInvoker` UAC manifest, `PerMonitorV2` DPI awareness, and `MyPowerTools.ico`.

2. **Flexible Sidecar Configuration (`<ExeDir>\<ExeStem>.mptboot.json`) + Zero-Config Fallback**:
   - Reads optional UTF-8 `<ExeStem>.mptboot.json` supporting `title`, `entryDll`, `probeDirs` (with `%ENV%` and `__EXE_DIR__` expansion), `defaultArgs` (`__EXE_DIR__` / `.` expansion), `minMajor` (default `8`), `requireAspNet` (default `false`), and `waitForChild` (default `false`).
   - When `.mptboot.json` is omitted, searches in convention order:
     1. `<ExeDir>\<ExeStem>.dll`
     2. `<ExeDir>\app\<ExeStem>.dll`
     3. `<ExeDir>\app\MptStandaloneHost.dll` (injecting `--tool-root "<ExeDir>"`)
     4. `<ExeDir>\..\shared-ui\MptStandaloneHost.dll` (injecting `--tool-root "<ExeDir>"`)
     5. `%LOCALAPPDATA%\MyPowerTools\shared-ui\MptStandaloneHost.dll` (injecting `--tool-root "<ExeDir>"`)

3. **RollForward-Ready `.NET >= 8.0` Discovery & Zero-Flash Launch**:
   - Discovers valid `Microsoft.NETCore.App` (`coreclr.dll`) `>= minMajor` (8, 9, 10, 11+) across private `Runtime\dotnet`, `%DOTNET_ROOT%`, `%LOCALAPPDATA%\Microsoft\dotnet`, `%ProgramFiles%\dotnet`, Windows Registry (`InstalledVersions\x64`), and `%PATH%`.
   - Launches `"<dotnetRoot>\dotnet.exe" exec "<resolvedEntryDll>"` with `CREATE_NO_WINDOW`, `DOTNET_ROLL_FORWARD=Major`, `DOTNET_ROLL_FORWARD_PRE_RELEASE=1`, `DOTNET_ROOT`, and `MPT_LAUNCHER_EXE`.

4. **One-Click User-Local `.NET 8.0+` Auto-Installer GUI**:
   - When `.NET >= minMajor` is missing, presents a crisp Per-Monitor DPI v2 Chinese Win32 dialog (`Microsoft YaHei UI`).
   - Queries Microsoft official `releases.json` over `WinHTTP`, downloads `dotnet-runtime-<ver>-win-x64.zip` in 64 KB chunks with live MB/% progress, extracts to `%LOCALAPPDATA%\Microsoft\dotnet` without UAC (`tar.exe` with `Expand-Archive` fallback, plus official `.exe` installer fallback), and automatically launches the tool.

## Diagnostic & Test Flags (Stripped Before Child Launch)

- `--mpt-probe-json <outPath>`: Writes runtime & entry resolution JSON report and exits `0` (found) or `3` (missing).
- `--mpt-test-no-dotnet` (or `MPT_BOOTSTRAP_FORCE_MISSING_DOTNET=1`): Simulates missing `.NET` runtime.
- `--mpt-wait`: Waits for the child process and forwards its exit code.
- `--mpt-test-ui-dump <jsonFile>` / `--mpt-test-autoclose-ms <ms>` / `--mpt-auto-install`: Automated GUI & mock-install test hooks (with `MPT_BOOTSTRAP_RUNTIME_ZIP_URL` and `MPT_BOOTSTRAP_INSTALL_DIR` environment overrides).

## Building & Running Unit Tests

```bash
bash native/mpt-bootstrap/build.sh
```

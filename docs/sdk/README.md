# MyPowerTools Tool SDK

MyPowerTools accepts standalone tools from any directory. A tool owns its source, runtime, settings, and release cycle. The Suite reads `tool.json`, exposes the declared surface, and communicates with the runtime through a stable package or protocol contract.

## Five-minute quick start

Build the SDK and CLI from a clean checkout:

```powershell
cd C:\src\MyPowerTools
dotnet restore .\MyPowerTools.slnx
.\scripts\build-sdk.ps1
dotnet build .\src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -c Release
$mpt = '.\src\MyPowerTools.Cli\bin\Release\net10.0\MyPowerTools.Cli.exe'
```

Create a web tool outside this repository:

```powershell
& $mpt create tool --type web --id example.weather --output C:\src\example-weather
& $mpt validate tool C:\src\example-weather
python C:\src\example-weather\serve.py
```

Add its parent directory to `%LOCALAPPDATA%\MyPowerTools\settings\tool-directories.json`:

```json
{ "directories": ["C:\\src"] }
```

Start MyPowerTools, open **All tools**, and click **Refresh tools**. Open the new entry to load its real page. Remove `C:\src` from the configuration, click refresh, and the entry disappears.

Inspect settings and logs from the tool page and **System**. Create the distributable package with:

```powershell
& $mpt pack tool C:\src\example-weather --output C:\dist\example.weather.mptpkg
```

Development discovery accepts dirty repositories, arbitrary branches, manual build outputs, `MPT_TOOL_DIRS`, and repeated Runner `--tool-dir` arguments.

## Documents

- [tool.json reference](tool-json.md)
- [Lifecycle and fault model](lifecycle-and-faults.md)
- [Settings and Secret Store](settings-and-secrets.md)
- [Commands, events, and logs](commands-events-logs.md)
- [Dotnet Surface](dotnet-surface.md)
- [Web Surface and Web Bridge](web-surface.md)
- [Native Tool](native-tool.md)
- [Headless Tool](headless-tool.md)
- [Packaging and installation](packaging.md)
- [Version compatibility](version-compatibility.md)
- [Debugging](debugging.md)
- [Mihomo Multi Monitor contract](mihomo-multi-monitor.md)

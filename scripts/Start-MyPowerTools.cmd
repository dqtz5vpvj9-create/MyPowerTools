@echo off
setlocal
set "MPT_ROOT=%~dp0"
if exist "%MPT_ROOT%Runtime\dotnet\host\fxr" (
  set "DOTNET_ROOT=%MPT_ROOT%Runtime\dotnet"
) else (
  set "DOTNET_ROOT="
)
if exist "%MPT_ROOT%MyPowerTools.exe" (
  start "" "%MPT_ROOT%MyPowerTools.exe" %*
) else (
  start "" "%MPT_ROOT%Shell\MyPowerTools.Shell.Avalonia.exe" --modules "%MPT_ROOT%modules" %*
)

@echo off
setlocal
set "MPT_ROOT=%~dp0"
set "DOTNET_ROOT=%MPT_ROOT%Runtime\dotnet"
if exist "%MPT_ROOT%MyPowerTools.exe" (
  start "" "%MPT_ROOT%MyPowerTools.exe" %*
) else (
  start "" "%MPT_ROOT%Shell\MyPowerTools.Shell.Avalonia.exe" --modules "%MPT_ROOT%modules" %*
)

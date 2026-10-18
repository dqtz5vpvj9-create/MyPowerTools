@echo off
setlocal
set "DDNS_SCRIPT=%~dp0ddns.ps1"
rem Published units contain the staged script; source checkouts use the canonical copy.
if not exist "%DDNS_SCRIPT%" set "DDNS_SCRIPT=%~dp0..\..\..\ddns.ps1"
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%DDNS_SCRIPT%" -Command watch

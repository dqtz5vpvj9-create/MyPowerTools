@echo off
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0ddns.ps1" -Command watch

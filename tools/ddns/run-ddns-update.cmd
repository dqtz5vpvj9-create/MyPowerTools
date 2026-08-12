@echo off
pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0ddns.ps1" -Command update

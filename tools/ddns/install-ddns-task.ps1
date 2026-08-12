[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'ddns-config.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "ddns-config.json not found: $ConfigPath"
}

$config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
$intervalMinutes = [Math]::Max(1, [int]$config.checkIntervalMinutes)
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$script = Join-Path $PSScriptRoot 'ddns.ps1'

$action = New-ScheduledTaskAction -Execute $pwsh -Argument (
    "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`" -Command update")
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $intervalMinutes)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName 'MyPowerTools DDNS' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Write-Host "Registered 'MyPowerTools DDNS' every $intervalMinutes minute(s): $script"

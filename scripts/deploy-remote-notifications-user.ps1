[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$ArtifactRoot = (Join-Path $PSScriptRoot '..\artifacts\tools\remote-notifications\0.2.0\service-units\remote-notifications.service')
)

$ErrorActionPreference = 'Stop'

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)
$artifactRootFull = [IO.Path]::GetFullPath($ArtifactRoot)
$artifactBin = Join-Path $artifactRootFull 'bin'
$sourceExecutable = Join-Path $artifactBin 'RemoteNotifications.Service.exe'
$cli = Join-Path $installRootFull 'Cli\MyPowerTools.Cli.exe'
$manifestPath = Join-Path $dataRootFull 'ServiceManager\units\remote-notifications.service.json'

foreach ($required in @($sourceExecutable, $cli, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required deployment input is missing: $required"
    }
}

$revision = (Get-FileHash -LiteralPath $sourceExecutable -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
$deployRoot = Join-Path $dataRootFull "ServiceManager\versions\0.2.0-$revision\remote-notifications.service"
$allowedDeployRoot = ([IO.Path]::GetFullPath((Join-Path $dataRootFull 'ServiceManager\versions'))).TrimEnd('\') + '\'
if (-not ([IO.Path]::GetFullPath($deployRoot)).StartsWith($allowedDeployRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe Service Unit deployment target: $deployRoot"
}

& $cli @('service', 'stop', 'remote-notifications.service')
$stopExitCode = $LASTEXITCODE
if ($stopExitCode -notin @(0, 1)) {
    throw "Stopping Remote Notifications failed with exit code $stopExitCode."
}

New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $artifactBin -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $deployRoot -Recurse -Force
}

$unitManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$unitManifest.exec = Join-Path $deployRoot 'RemoteNotifications.Service.exe'
$unitManifest.workingDirectory = $deployRoot
$environment = @{}
foreach ($property in $unitManifest.environment.PSObject.Properties) {
    $environment[$property.Name] = [string]$property.Value
}
$environment['MPT_INSTALL_ROOT'] = $installRootFull
$environment['MPT_DATA_ROOT'] = $dataRootFull
$unitManifest.environment = $environment
$unitManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

& $cli @('service', 'reload')
if ($LASTEXITCODE -ne 0) {
    throw 'Reloading ServiceManager failed.'
}
& $cli @('service', 'start', 'remote-notifications.service')
if ($LASTEXITCODE -ne 0) {
    throw 'Starting Remote Notifications failed.'
}

$protocolRoot = 'HKCU:\Software\Classes\mypowertools'
New-Item -Path "$protocolRoot\shell\open\command" -Force | Out-Null
New-Item -Path "$protocolRoot\DefaultIcon" -Force | Out-Null
Set-Item -LiteralPath $protocolRoot -Value 'URL:MyPowerTools Remote Notification'
New-ItemProperty -LiteralPath $protocolRoot -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
Set-Item -LiteralPath "$protocolRoot\DefaultIcon" -Value "$(Join-Path $deployRoot 'RemoteNotifications.Service.exe'),0"
Set-Item -LiteralPath "$protocolRoot\shell\open\command" -Value "`"$(Join-Path $deployRoot 'RemoteNotifications.Service.exe')`" --remote-notification-activation `"%1`""

Write-Output $deployRoot

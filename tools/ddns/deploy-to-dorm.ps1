[CmdletBinding()]
param(
    [string]$DormHost = 'dorm',
    [string]$RemoteUser = 'lixinrui',
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'ddns-config.dorm.json'),
    [switch]$SkipTask
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Dorm config not found: $ConfigPath (copy ddns-config.example.json and fill in credentials)."
}

$remoteDir = "C:\Users\$RemoteUser\AppData\Local\MyPowerTools\ddns"
$remoteRelativeDir = 'AppData/Local/MyPowerTools/ddns'
$files = @(
    'ddns.ps1',
    'install-ddns-task.ps1',
    'run-ddns-update.cmd',
    'run-install-task.cmd'
)

Write-Host "Creating remote directory $remoteDir ..."
$createDirCommand = "powershell -NoProfile -Command `"New-Item -ItemType Directory -Force -Path $remoteDir | Out-Null; Write-Output DIR_OK`""
ssh $DormHost $createDirCommand
if ($LASTEXITCODE -ne 0) {
    throw "Unable to create remote directory on $DormHost."
}

foreach ($file in $files) {
    $localPath = Join-Path $PSScriptRoot $file
    Write-Host "Copying $file ..."
    scp -O $localPath "${DormHost}:$remoteRelativeDir/$file"
    if ($LASTEXITCODE -ne 0) {
        throw "scp failed for $file."
    }
}

Write-Host "Copying dorm config ..."
scp -O $ConfigPath "${DormHost}:$remoteRelativeDir/ddns-config.json"
if ($LASTEXITCODE -ne 0) {
    throw "scp failed for ddns-config.json."
}

Write-Host "Running first DDNS update on $DormHost ..."
ssh $DormHost "$remoteDir\run-ddns-update.cmd"
if ($LASTEXITCODE -ne 0) {
    throw "Remote DDNS update failed with exit code $LASTEXITCODE."
}

if (-not $SkipTask) {
    Write-Host "Registering scheduled task on $DormHost ..."
    ssh $DormHost "$remoteDir\run-install-task.cmd"
    if ($LASTEXITCODE -ne 0) {
        throw "Remote task registration failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Deployment complete."

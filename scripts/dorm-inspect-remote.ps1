[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$result = [ordered]@{
    installDir = Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'
    dataRoot = Join-Path $env:LOCALAPPDATA 'MyPowerTools'
    consoleUser = [string](Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
    processes = @(
        Get-Process -Name 'MyPowerTools.Runner', 'MyPowerTools.Shell.Avalonia', 'MyPowerTools.ServiceManager', 'MyPowerTools.Cli' -ErrorAction SilentlyContinue |
            Select-Object ProcessName, Id, SessionId, @{
                n = 'Path'
                e = { try { $_.MainModule.FileName } catch { '' } }
            }
    )
    otaCheckTask = @(
        Get-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue |
            Select-Object TaskName, State, @{
                n = 'Result'
                e = { (Get-ScheduledTaskInfo -TaskName $_.TaskName -ErrorAction SilentlyContinue).LastTaskResult }
            }
    )
    runEntries = [ordered]@{}
    allRunValues = [ordered]@{}
    mptTasks = @(
        Get-ScheduledTask -ErrorAction SilentlyContinue |
            Where-Object { $_.TaskName -like '*MyPowerTools*' -or $_.TaskName -like '*SmartBird*' -or $_.TaskName -like '*Energy*' } |
            ForEach-Object {
                $info = Get-ScheduledTaskInfo -TaskName $_.TaskName -ErrorAction SilentlyContinue
                [ordered]@{
                    name = $_.TaskName
                    state = $_.State.ToString()
                    result = if ($null -ne $info) { $info.LastTaskResult } else { $null }
                    lastRun = if ($null -ne $info) { $info.LastRunTime.ToString('O') } else { '' }
                }
            }
    )
    installManifest = $null
    otaStateFiles = @()
    installedRelease = $null
    healthCheck = $null
}
foreach ($name in @('MyPowerTools', 'MyPowerTools.ServiceManager')) {
    $value = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -ne $value) {
        $result.runEntries.$name = [string]$value
    }
}
$runKey = Get-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
if ($null -ne $runKey) {
    foreach ($property in $runKey.Property) {
        $result.allRunValues.$property = [string]$runKey.GetValue($property)
    }
}
$installManifestPath = Join-Path $result.installDir 'install.manifest.json'
if (Test-Path -LiteralPath $installManifestPath -PathType Leaf) {
    $result.installManifest = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
}
$otaStateDir = Join-Path $result.dataRoot 'ota-state'
if (Test-Path -LiteralPath $otaStateDir -PathType Container) {
    $result.otaStateFiles = @(
        Get-ChildItem -LiteralPath $otaStateDir -File -ErrorAction SilentlyContinue |
            Select-Object Name, Length
    )
    $installedReleasePath = Join-Path $otaStateDir 'installed-release.json'
    if (Test-Path -LiteralPath $installedReleasePath -PathType Leaf) {
        $result.installedRelease = Get-Content -LiteralPath $installedReleasePath -Raw | ConvertFrom-Json
    }
    $healthPath = Join-Path $otaStateDir 'health-check.json'
    if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
        $result.healthCheck = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
    }
}

$result | ConvertTo-Json -Depth 8

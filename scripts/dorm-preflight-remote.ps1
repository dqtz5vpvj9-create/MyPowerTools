[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$result = [ordered]@{
    computer = $env:COMPUTERNAME
    user = $env:USERNAME
    temp = $env:TEMP
    pwsh = (Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    installDir = Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'
    dataRoot = Join-Path $env:LOCALAPPDATA 'MyPowerTools'
}

$manifestPath = Join-Path $result.installDir 'install.manifest.json'
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $result.currentVersion = [string]$manifest.version
    $result.installed = $true
} else {
    $result.currentVersion = ''
    $result.installed = (Test-Path -LiteralPath $result.installDir -PathType Container)
}

$otaStatePath = Join-Path $result.dataRoot 'ota-state\installed-release.json'
$result.otaState = Test-Path -LiteralPath $otaStatePath -PathType Leaf
$result.runtimes = [ordered]@{
    smartBird = Test-Path -LiteralPath (Join-Path $result.installDir 'Runtimes\SmartBird') -PathType Container
    doubao = Test-Path -LiteralPath (Join-Path $result.installDir 'Runtimes\Doubao') -PathType Container
    python = Test-Path -LiteralPath (Join-Path $result.installDir 'Runtimes\Python312') -PathType Container
    androidTools = Test-Path -LiteralPath (Join-Path $result.installDir 'Tools\AndroidPlatformTools') -PathType Container
}
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$result.runEntries = [ordered]@{}
foreach ($name in @('MyPowerTools', 'MyPowerTools.ServiceManager')) {
    $value = (Get-ItemProperty -LiteralPath $runKeyPath -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -ne $value) {
        $result.runEntries.$name = [string]$value
    }
}
$result.mptTasks = @(
    Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object {
            $_.TaskName -like '*MyPowerTools*' -or
            $_.TaskName -like '*SmartBird*' -or
            $_.TaskName -like '*Energy*'
        } |
        ForEach-Object {
            [ordered]@{
                name = $_.TaskName
                state = $_.State.ToString()
            }
        }
)
$result.smartBirdData = Test-Path -LiteralPath (Join-Path $result.dataRoot 'SmartBird') -PathType Container
$result.doubaoData = Test-Path -LiteralPath (Join-Path $result.dataRoot 'Doubao') -PathType Container

$result | ConvertTo-Json -Depth 4

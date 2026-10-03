param([Parameter(Mandatory)][string]$Scenario, [Parameter(Mandatory)][string]$DataRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
$configPath = Join-Path $DataRoot 'config with spaces.json'
$config = @{
    mainDomain = 'example.test'; subDomain = 'qa'; secretId = 'fake'; secretToken = 'fake'
    dataRoot = $DataRoot; ipSource = 'internet'; ttlSeconds = 600
    checkIntervalMinutes = 2; clearSameNameRecords = $false
}
$global:ddnsTestCalls = [Collections.Generic.List[object]]::new()
$global:ddnsTestRecords = @()
$global:ddnsTestFailApi = $false
if ($Scenario -eq 'distribution') {
    $isolatedTool = Join-Path $DataRoot 'tool'
    New-Item -ItemType Directory -Path $isolatedTool -Force | Out-Null
    foreach ($file in @('ddns.ps1', 'ddns-config.example.json', 'build.ps1', 'module.json')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "tools/ddns/$file") -Destination (Join-Path $isolatedTool $file)
    }
    & (Join-Path $isolatedTool 'build.ps1') -MyPowerToolsRepoRoot $repoRoot -Configuration Debug 6>$null
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $isolatedTool 'ddns.ps1')).Hash
    $packageHash = (Get-FileHash -LiteralPath (Join-Path $isolatedTool 'artifacts/package/bin/ddns.ps1')).Hash
    $serviceHash = (Get-FileHash -LiteralPath (Join-Path $isolatedTool 'service-units/ddns.service/bin/ddns.ps1')).Hash
    @{ sourceHash=$sourceHash; packageHash=$packageHash; serviceHash=$serviceHash } | ConvertTo-Json -Compress
    exit 0
}
function global:Invoke-RestMethod {
    param($Uri, $Method, $Body, $TimeoutSec)
    if ($Uri -notlike 'https://dnsapi.cn/*') { throw "Unexpected external IP request: $Uri" }
    $action = ([uri]$Uri).AbsolutePath.TrimStart('/')
    $global:ddnsTestCalls.Add(@{ action = $action; body = $Body.Clone() })
    if ($global:ddnsTestFailApi) { return @{ status = @{ code = '5'; message = 'test denied' } } }
    return @{ status = @{ code = '1' }; records = $global:ddnsTestRecords; record = @{ id = 'created' } }
}
switch ($Scenario) {
    'unchanged' { $global:ddnsTestRecords = @(@{id='1';value='203.0.113.4'}, @{id='2';value='203.0.113.5'}); $config.clearSameNameRecords = $true }
    'update' { $global:ddnsTestRecords = @(@{id='1';value='203.0.113.5'}) }
    'force-cleanup' { $global:ddnsTestRecords = @(@{id='1';value='203.0.113.5'}, @{id='2';value='203.0.113.4'}); $config.clearSameNameRecords = $true }
    'api-error' { $global:ddnsTestFailApi = $true }
    'list' { $global:ddnsTestRecords = @(@{id='1';value='203.0.113.4'}, @{id='2';value='203.0.113.5'}) }
    'task' {
        function global:New-ScheduledTaskAction { param($Execute, $Argument) return @{ Execute=$Execute; Argument=$Argument } }
        function global:New-ScheduledTaskTrigger { param([switch]$Once, $At, $RepetitionInterval) return @{ Interval=$RepetitionInterval.TotalMinutes } }
        function global:New-ScheduledTaskSettingsSet { param([switch]$StartWhenAvailable, $ExecutionTimeLimit, $MultipleInstances) return @{} }
        function global:Register-ScheduledTask { param($TaskName, $Action, $Trigger, $Settings, [switch]$Force) $global:ddnsTestTask = @{Name=$TaskName;Action=$Action;Trigger=$Trigger} }
    }
}
$config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
if ($Scenario -eq 'saved-status') {
    @{ schemaVersion=1; wanIp='203.0.113.8'; updated=$true; message='saved test update' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $DataRoot 'ddns-state.json') -Encoding utf8
}
if ($Scenario -eq 'watch-error') {
    $global:ddnsTestFailApi = $true
    function global:Start-Sleep { param($Seconds) throw 'TEST-WATCH-STOP' }
}
$result = $null
$errorMessage = ''
try {
    if ($Scenario -eq 'task') {
        & (Join-Path $repoRoot 'tools/ddns/install-ddns-task.ps1') -ConfigPath $configPath 6>$null
        $result = $global:ddnsTestTask
    } else {
        $command = if ($Scenario -eq 'list') { 'list' } elseif ($Scenario -in @('fresh-status', 'saved-status')) { 'status' } elseif ($Scenario -eq 'watch-error') { 'watch' } else { 'update' }
        $ip = if ($Scenario -eq 'invalid-ip') { 'not-an-ip' } else { '203.0.113.4' }
        $json = & (Join-Path $repoRoot 'tools/ddns/ddns.ps1') -Command $command -ConfigPath $configPath -OverrideIp $ip -Force:($Scenario -eq 'force-cleanup')
        $result = ($json -join "`n") | ConvertFrom-Json
    }
} catch { $errorMessage = $_.Exception.Message }
$statePath = Join-Path $DataRoot 'ddns-state.json'
$saved = if (Test-Path -LiteralPath $statePath) { Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json } else { $null }
$logPath = Join-Path $DataRoot 'ddns.log'
$log = if (Test-Path -LiteralPath $logPath) { Get-Content -Raw -LiteralPath $logPath } else { '' }
@{ result=$result; calls=@($global:ddnsTestCalls.ToArray()); error=$errorMessage; saved=$saved; configPath=$configPath; log=$log } | ConvertTo-Json -Depth 12 -Compress


[CmdletBinding()]
param(
    [ValidateSet('update', 'watch', 'status', 'list')]
    [string]$Command = 'status',
    [string]$ConfigPath = '',
    [string]$OverrideIp = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Resolve-ConfigPath {
    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        return [IO.Path]::GetFullPath($ConfigPath)
    }
    $candidates = @(
        (Join-Path $PSScriptRoot 'ddns-config.json'),
        (Join-Path $env:LOCALAPPDATA 'MyPowerTools\ddns\ddns-config.json')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw "ddns-config.json was not found. Looked in: $($candidates -join ', ')"
}

function Read-Config {
    param([Parameter(Mandatory = $true)][string]$Path)

    $config = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$config.mainDomain) -or
        [string]::IsNullOrWhiteSpace([string]$config.subDomain) -or
        [string]::IsNullOrWhiteSpace([string]$config.secretId) -or
        [string]::IsNullOrWhiteSpace([string]$config.secretToken)) {
        throw "ddns-config.json is incomplete: mainDomain/subDomain/secretId/secretToken are required."
    }
    return $config
}

function Resolve-DataRoot {
    param([Parameter(Mandatory = $true)][pscustomobject]$Config)

    if (-not [string]::IsNullOrWhiteSpace([string]$Config.dataRoot)) {
        return [IO.Path]::GetFullPath([string]$Config.dataRoot)
    }
    return (Join-Path $env:LOCALAPPDATA 'MyPowerTools\ddns')
}

function Get-WanIp {
    param([Parameter(Mandatory = $true)][pscustomobject]$Config)

    if (-not [string]::IsNullOrWhiteSpace($OverrideIp)) {
        return $OverrideIp.Trim()
    }

    $source = [string]$Config.ipSource
    if ($source -in @('internet', 'wan')) {
        foreach ($endpoint in @('https://api.ipify.org', 'https://ifconfig.me/ip')) {
            try {
                return ([string](Invoke-RestMethod -Uri $endpoint -TimeoutSec 15)).Trim()
            }
            catch {
                # try the next endpoint
            }
        }
        throw "Unable to determine public IP from external sources."
    }

    # adapter: match the physical adapter by description, e.g. "Realtek USB 2.5GbE Family Controller"
    $adapterDescription = [string]$Config.adapterDescription
    if ([string]::IsNullOrWhiteSpace($adapterDescription)) {
        throw "ipSource 'adapter' requires adapterDescription in ddns-config.json."
    }

    # Get-NetAdapter can return a null Description in SSH/non-interactive
    # sessions; CIM exposes the same hardware description reliably.
    $adapters = @(Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.NetConnectionStatus -eq 2 -and $_.Name -like "*$adapterDescription*" })
    if ($adapters.Count -eq 0) {
        $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue |
            Where-Object { $_.Status -eq 'Up' -and $_.Description -like "*$adapterDescription*" })
    }
    foreach ($adapter in $adapters) {
        $interfaceIndex = if ($null -ne $adapter.Index) { $adapter.Index } else { $adapter.ifIndex }
        $address = Get-NetIPAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' -and $_.PrefixOrigin -ne 'WellKnown' } |
            Select-Object -First 1
        if ($null -ne $address) {
            return $address.IPAddress
        }
    }

    throw "No IPv4 address found on adapter matching '*$adapterDescription*'. Run ipconfig /all to check adapter names."
}

function Invoke-DnspodApi {
    param(
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][hashtable]$Params,
        [Parameter(Mandatory = $true)][pscustomobject]$Config
    )

    $body = @{
        login_token = "$([string]$Config.secretId),$([string]$Config.secretToken)"
        format = 'json'
    }
    foreach ($key in $Params.Keys) {
        $body[$key] = $Params[$key]
    }

    $response = Invoke-RestMethod -Uri "https://dnsapi.cn/$Action" -Method Post -Body $body -TimeoutSec 20
    if ([string]$response.status.code -ne '1') {
        throw "DNSPod $Action failed: $($response.status.message) (code $($response.status.code))"
    }
    return $response
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Line
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Add-Content -LiteralPath $Path -Value $Line -Encoding UTF8
}

function Invoke-Update {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Config,
        [Parameter(Mandatory = $true)][string]$DataRoot
    )

    $statePath = Join-Path $DataRoot 'ddns-state.json'
    $logPath = Join-Path $DataRoot 'ddns.log'
    $domain = [string]$Config.mainDomain
    $subDomain = [string]$Config.subDomain
    $recordName = if ([string]::IsNullOrWhiteSpace($subDomain)) { $domain } else { "$subDomain.$domain" }

    $wanIp = Get-WanIp -Config $Config
    $records = @((Invoke-DnspodApi -Action 'Record.List' -Params @{
        domain = $domain
        sub_domain = $subDomain
        record_type = 'A'
    } -Config $Config).records)

    $domainIp = if ($records.Count -gt 0) { [string]$records[0].value } else { '' }
    $now = Get-Date

    # 清除所有同名记录：只保留一条 A 记录，其余删除（覆盖语义）。
    if ([bool]$Config.clearSameNameRecords -and $records.Count -gt 1) {
        foreach ($extra in $records | Select-Object -Skip 1) {
            Invoke-DnspodApi -Action 'Record.Remove' -Params @{
                domain = $domain
                record_id = [string]$extra.id
            } -Config $Config | Out-Null
        }
        $records = @($records[0])
    }

    $updated = $false
    $message = ''
    $recordId = ''
    if ($records.Count -eq 0) {
        $result = Invoke-DnspodApi -Action 'Record.Create' -Params @{
            domain = $domain
            sub_domain = $subDomain
            record_type = 'A'
            record_line = '默认'
            value = $wanIp
            ttl = [string]$Config.ttlSeconds
        } -Config $Config
        $recordId = [string]$result.record.id
        $updated = $true
        $message = "created $recordName -> $wanIp"
    }
    else {
        $record = $records[0]
        $recordId = [string]$record.id
        if ([string]$record.value -eq $wanIp -and -not $Force) {
            $message = "IP dont need UPDATE... ($recordName already $wanIp)"
        }
        else {
            Invoke-DnspodApi -Action 'Record.Modify' -Params @{
                domain = $domain
                record_id = $recordId
                sub_domain = $subDomain
                record_type = 'A'
                record_line = '默认'
                value = $wanIp
                ttl = [string]$Config.ttlSeconds
            } -Config $Config | Out-Null
            $updated = $true
            $message = "updated $recordName -> $wanIp"
        }
    }

    $state = [ordered]@{
        schemaVersion = 1
        provider = 'tencent-dnspod'
        checkedAt = $now.ToString('yyyy-MM-dd HH:mm:ss')
        checkedAtUtc = $now.ToUniversalTime().ToString('O')
        wanIp = $wanIp
        domainIp = $domainIp
        record = $recordName
        recordId = $recordId
        updated = $updated
        message = $message
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Log -Path $logPath -Line "$($now.ToString('yyyy-MM-dd HH:mm:ss')) WAN-IP: $wanIp"
    Write-Log -Path $logPath -Line "$($now.ToString('yyyy-MM-dd HH:mm:ss')) DOMAIN-IP: $domainIp"
    Write-Log -Path $logPath -Line "$($now.ToString('yyyy-MM-dd HH:mm:ss')) $message"

    return [pscustomobject]$state
}

$configPath = Resolve-ConfigPath
$config = Read-Config -Path $configPath
$dataRoot = Resolve-DataRoot -Config $config
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

switch ($Command) {
    'update' {
        $state = Invoke-Update -Config $config -DataRoot $dataRoot
        $state | ConvertTo-Json
    }
    'watch' {
        $intervalSeconds = [Math]::Max(1, [int]$config.checkIntervalMinutes) * 60
        while ($true) {
            try {
                Invoke-Update -Config $config -DataRoot $dataRoot | Out-Null
            }
            catch {
                Write-Log -Path (Join-Path $dataRoot 'ddns.log') -Line "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ERROR: $($_.Exception.Message)"
            }
            Start-Sleep -Seconds $intervalSeconds
        }
    }
    'status' {
        $statePath = Join-Path $dataRoot 'ddns-state.json'
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            Get-Content -Raw -LiteralPath $statePath
        }
        else {
            '{"schemaVersion":1,"checkedAt":null,"wanIp":null,"domainIp":null,"updated":false,"message":"No update has run yet."}'
        }
    }
    'list' {
        (Invoke-DnspodApi -Action 'Record.List' -Params @{
            domain = [string]$config.mainDomain
            sub_domain = [string]$config.subDomain
            record_type = 'A'
        } -Config $config).records | ConvertTo-Json -Depth 5
    }
}

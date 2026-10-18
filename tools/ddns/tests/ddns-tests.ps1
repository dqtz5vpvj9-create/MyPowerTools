[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$toolRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $toolRoot 'ddns.ps1'
$installerPath = Join-Path $toolRoot 'install-ddns-task.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
$functionAst = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-WanIp'
}, $true)
if ($null -eq $functionAst) { throw 'Get-WanIp was not found.' }
# Load only the function under test. Never run DDNS dispatch/configuration or network calls.
$wanIpFunction = [scriptblock]::Create($functionAst.Extent.Text)
# Extract only the actual publisher's DDNS staging block. Do not run publishing,
# builds, installations or any external command from the surrounding script.
$repoRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '../..'))
$publisherPath = if ($env:MPT_TEST_PUBLISH_WINDOWS) { $env:MPT_TEST_PUBLISH_WINDOWS } else {
    Join-Path $repoRoot 'scripts/publish-windows.ps1'
}
$publisherAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $publisherPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
$ddnsSourceStatement = @($publisherAst.EndBlock.Statements | Where-Object {
    $_.Extent.Text.TrimStart().StartsWith('$ddnsUnitSource =')
})
$ddnsCopyStatement = @($publisherAst.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text.TrimStart().StartsWith('if (Test-Path -LiteralPath $ddnsUnitSource')
})
if ($ddnsSourceStatement.Count -ne 1 -or $ddnsCopyStatement.Count -ne 1) {
    throw 'Expected exactly one standalone DDNS publisher staging block.'
}
$stageDdnsUnit = [scriptblock]::Create(
    $ddnsSourceStatement[0].Extent.Text + "`n" + $ddnsCopyStatement[0].Extent.Text)
$script:passed = 0
$script:failed = 0
function Invoke-RestMethod { throw 'Network access is forbidden in DDNS tests.' }

function Assert-Equal($Expected, $Actual) {
    if ($Expected -cne $Actual) { throw "Expected '$Expected'; actual '$Actual'." }
}

function Invoke-Test([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $script:passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

Invoke-Test 'CIM adapter uses InterfaceIndex rather than device Index' {
    . $wanIpFunction
    $OverrideIp = ''
    function Get-CimInstance {
        [pscustomobject]@{ NetConnectionStatus = 2; Name = 'Test NIC'; Index = 4; InterfaceIndex = 17 }
    }
    function Get-NetAdapter { throw 'Unexpected Get-NetAdapter call.' }
    function Get-NetIPAddress {
        param($InterfaceIndex, $AddressFamily, $ErrorAction)
        Assert-Equal 17 $InterfaceIndex
        Assert-Equal 'IPv4' $AddressFamily
        [pscustomobject]@{ IPAddress = '192.0.2.17'; PrefixOrigin = 'Dhcp' }
    }
    $result = Get-WanIp -Config ([pscustomobject]@{ ipSource = 'adapter'; adapterDescription = 'Test NIC' })
    Assert-Equal '192.0.2.17' $result
}

Invoke-Test 'NetAdapter fallback retains ifIndex support and ignores link-local addresses' {
    . $wanIpFunction
    $OverrideIp = ''
    function Get-CimInstance { }
    function Get-NetAdapter { [pscustomobject]@{ Status = 'Up'; Description = 'Test NIC'; ifIndex = 29 } }
    function Get-NetIPAddress {
        param($InterfaceIndex, $AddressFamily, $ErrorAction)
        Assert-Equal 29 $InterfaceIndex
        [pscustomobject]@{ IPAddress = '169.254.1.2'; PrefixOrigin = 'WellKnown' }
        [pscustomobject]@{ IPAddress = '192.0.2.29'; PrefixOrigin = 'Dhcp' }
    }
    $result = Get-WanIp -Config ([pscustomobject]@{ ipSource = 'adapter'; adapterDescription = 'Test NIC' })
    Assert-Equal '192.0.2.29' $result
}

Invoke-Test 'Override IP does not query adapters or network services' {
    . $wanIpFunction
    $OverrideIp = ' 192.0.2.50 '
    function Get-CimInstance { throw 'Unexpected CIM call.' }
    function Get-NetAdapter { throw 'Unexpected adapter call.' }
    function Invoke-RestMethod { throw 'Unexpected network call.' }
    Assert-Equal '192.0.2.50' (Get-WanIp -Config ([pscustomobject]@{ ipSource = 'internet' }))
}

Invoke-Test 'Scheduled task preserves a custom config path with spaces' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('mpt-ddns-tests-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $configPath = Join-Path $tempRoot 'custom config.json'
    '{"checkIntervalMinutes":7}' | Set-Content -LiteralPath $configPath
    try {
        $registration = @{ Count = 0 }
        function Get-Command { param($Name, $ErrorAction) [pscustomobject]@{ Source = 'test-pwsh' } }
        function New-ScheduledTaskAction { param($Execute, $Argument) [pscustomobject]@{ Execute = $Execute; Argument = $Argument } }
        function New-ScheduledTaskTrigger { param([switch]$Once, $At, $RepetitionInterval) [pscustomobject]@{ RepetitionInterval = $RepetitionInterval } }
        function New-ScheduledTaskSettingsSet { param([switch]$StartWhenAvailable, $ExecutionTimeLimit, $MultipleInstances) [pscustomobject]@{} }
        function Register-ScheduledTask {
            param($TaskName, $Action, $Trigger, $Settings, [switch]$Force)
            $registration.Count++
            Assert-Equal 'MyPowerTools DDNS' $TaskName
            Assert-Equal 'test-pwsh' $Action.Execute
            Assert-Equal ([TimeSpan]::FromMinutes(7)) $Trigger.RepetitionInterval
            $expectedPath = [IO.Path]::GetFullPath($configPath)
            if (-not $Action.Argument.Contains("-ConfigPath `"$expectedPath`"")) {
                throw "Scheduled task omitted the fully qualified, quoted ConfigPath: $($Action.Argument)"
            }
        }
        Push-Location $tempRoot
        try { & $installerPath -ConfigPath './custom config.json' }
        finally { Pop-Location }
        Assert-Equal 1 $registration.Count
    }
    finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Invoke-Test 'Published service stages canonical DDNS bytes and interface mapping' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('mpt-ddns-staging-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    try {
        $RepoRoot = $repoRoot
        $publishedServiceUnitsRoot = $tempRoot
        & $stageDdnsUnit
        $stagedScript = Join-Path $tempRoot 'ddns.service/bin/ddns.ps1'
        Assert-Equal (Get-FileHash -LiteralPath $scriptPath).Hash (Get-FileHash -LiteralPath $stagedScript).Hash
        $manifest = Get-Content -Raw -LiteralPath (Join-Path $tempRoot 'ddns.service/unit-manifest.json') | ConvertFrom-Json
        Assert-Equal 'ddns-runner.cmd' $manifest.exec
        if (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'ddns.service/bin/ddns-runner.cmd'))) {
            throw 'Staged service runner is missing.'
        }
        $stagedAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $stagedScript, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
        $stagedFunction = $stagedAst.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-WanIp'
        }, $true)
        . ([scriptblock]::Create($stagedFunction.Extent.Text))
        $OverrideIp = ''
        function Get-CimInstance {
            [pscustomobject]@{ NetConnectionStatus = 2; Name = 'Test NIC'; Index = 4; InterfaceIndex = 17 }
        }
        function Get-NetIPAddress {
            param($InterfaceIndex, $AddressFamily, $ErrorAction)
            Assert-Equal 17 $InterfaceIndex
            [pscustomobject]@{ IPAddress = '192.0.2.17'; PrefixOrigin = 'Dhcp' }
        }
        Assert-Equal '192.0.2.17' (Get-WanIp -Config ([pscustomobject]@{ ipSource = 'adapter'; adapterDescription = 'Test NIC' }))
    }
    finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Invoke-Test 'DDNS publisher rejects duplicate destination without overwriting it' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('mpt-ddns-duplicate-' + [guid]::NewGuid().ToString('N'))
    $unitRoot = Join-Path $tempRoot 'ddns.service'
    New-Item -ItemType Directory -Path $unitRoot -Force | Out-Null
    $marker = Join-Path $unitRoot 'keep.txt'
    Set-Content -LiteralPath $marker -Value 'preserve me' -NoNewline
    try {
        $RepoRoot = $repoRoot
        $publishedServiceUnitsRoot = $tempRoot
        $rejected = $false
        try { & $stageDdnsUnit } catch { $rejected = $_.Exception.Message.Contains('Duplicate Service Unit') }
        Assert-Equal $true $rejected
        Assert-Equal 'preserve me' (Get-Content -Raw -LiteralPath $marker)
    }
    finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Invoke-Test 'Source runner retains the canonical checkout fallback' {
    $runnerPath = Join-Path $toolRoot 'service-units/ddns.service/bin/ddns-runner.cmd'
    $runner = Get-Content -Raw -LiteralPath $runnerPath
    if (-not $runner.Contains('set "DDNS_SCRIPT=%~dp0ddns.ps1"') -or
        -not $runner.Contains('if not exist "%DDNS_SCRIPT%" set "DDNS_SCRIPT=%~dp0..\..\..\ddns.ps1"') -or
        -not $runner.Contains('-File "%DDNS_SCRIPT%" -Command watch')) {
        throw 'Runner must prefer its staged sibling and retain the source-checkout fallback.'
    }
    Assert-Equal ([IO.Path]::GetFullPath($scriptPath)) (
        [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $runnerPath) '../../../ddns.ps1')))
}

Write-Host "DDNS tests: $script:passed passed; $script:failed failed. No network or scheduled-task operations were performed."
if ($script:failed -gt 0) { exit 1 }

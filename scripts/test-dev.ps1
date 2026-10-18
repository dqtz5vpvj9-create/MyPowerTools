#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Full', 'Tools', 'Shell', 'Settings', 'Packaging')]
    [string]$Area = 'Full',
    [string]$ToolId,
    [string]$Feature,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [Alias('List')][switch]$Plan,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$rootTests = 'src/MyPowerTools.Tests/MyPowerTools.Tests.csproj'
$targets = [Collections.Generic.List[object]]::new()
$needsSdk = $false
$consoleListMarker = 'MPT_TEST_CASES_V1'

function Add-TestTarget([string]$Path, [string[]]$Classes = @(), [switch]$Console) {
    $filter = ($Classes | ForEach-Object { "FullyQualifiedName~MyPowerTools.Tests.$_." }) -join '|'
    $targets.Add([pscustomobject]@{ Path = $Path; Filter = $filter; Console = [bool]$Console; Names = @() })
}

function Invoke-Dotnet([string[]]$Arguments) {
    if ($needsSdk -and $Arguments[0] -in @('build', 'test', 'msbuild')) {
        # Match SDK preparation's owned cache for every consuming evaluation/build/test.
        # Ambient NuGet cache overrides must not select an older package with the same version.
        $Arguments += "-p:RestorePackagesPath=$(Join-Path $repo 'artifacts/sdk/global-packages')"
    }
    Write-Host ('> dotnet ' + (($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '))
    $output = @(& $dotnet @Arguments 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) { Write-Host $line }
    if ($exitCode -ne 0) { throw "dotnet exited with code $exitCode. No failed result was suppressed." }
    return $output
}

if ($Area -in @('Full', 'Packaging')) {
    if ($ToolId -or $Feature) { throw '-ToolId and -Feature require an explicitly supported focused area.' }
    if ($Area -eq 'Packaging') { Write-Host 'Packaging is shared infrastructure; using Full coverage.' }
    # Keep solution coverage intact. Standalone UI/unit projects and console regressions
    # are not discovered by dotnet test MyPowerTools.slnx and must remain explicit.
    Add-TestTarget 'MyPowerTools.slnx'
    Add-TestTarget 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj'
    Add-TestTarget 'tests/RemoteNotifications.Mac.Ui.Tests/RemoteNotifications.Mac.Ui.Tests.csproj'
    Add-TestTarget 'tools/ime-manager/sdk-tool/tests/ImeManager.Tests/ImeManager.Tests.csproj'
    Add-TestTarget 'tools/screenshot/tests/Screenshot.Lifecycle.Regression/Screenshot.Lifecycle.Regression.csproj' -Console
    Add-TestTarget 'tools/adb-forwarder/tests/AdbForwarder.Runtime.Regression/AdbForwarder.Runtime.Regression.csproj' -Console
    Add-TestTarget 'tools/xbrd/tests/Xbrd.Runtime.Regression/Xbrd.Runtime.Regression.csproj' -Console
    $needsSdk = $true
}
elseif ($Area -eq 'Settings') {
    if ($ToolId -ne 'remote-notifications' -or ($Feature -and $Feature -ne 'Configuration')) {
        throw 'Settings supports only -ToolId remote-notifications [-Feature Configuration]. Use Full for shared settings/schema changes.'
    }
    Add-TestTarget 'tools/remote-notifications/current-integration/tests/RemoteNotifications.Configuration.Tests/RemoteNotifications.Configuration.Tests.csproj'
}
elseif ($Area -eq 'Tools') {
    switch ("$ToolId/$Feature") {
        'input-monitor/Core' { Add-TestTarget 'tools/input-monitor/tests/InputMonitor.Core.Tests/InputMonitor.Core.Tests.csproj' }
        'adb-forwarder/Runtime' { Add-TestTarget 'tools/adb-forwarder/tests/AdbForwarder.Runtime.Regression/AdbForwarder.Runtime.Regression.csproj' -Console }
        'xbrd/Runtime' { $needsSdk = $true; Add-TestTarget 'tools/xbrd/tests/Xbrd.Runtime.Regression/Xbrd.Runtime.Regression.csproj' -Console }
        'screenshot/Lifecycle' { Add-TestTarget 'tools/screenshot/tests/Screenshot.Lifecycle.Regression/Screenshot.Lifecycle.Regression.csproj' -Console }
        'remote-notifications/Configuration' { Add-TestTarget 'tools/remote-notifications/current-integration/tests/RemoteNotifications.Configuration.Tests/RemoteNotifications.Configuration.Tests.csproj' }
        default { throw 'Unsupported tool/feature pair. Supported: adb-forwarder/Runtime, xbrd/Runtime, input-monitor/Core, screenshot/Lifecycle, remote-notifications/Configuration. Use Full for broader changes.' }
    }
}
elseif ($Area -eq 'Shell') {
    if ($ToolId) { throw '-ToolId is not valid for Shell.' }
    switch ($Feature) {
        'Failure' { Add-TestTarget $rootTests @('ShellFailurePresenterTests', 'HostConnectionRecoveryTests') }
        'Navigation' { Add-TestTarget 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj' @('PersonalUxFavoritesTests', 'PersonalUxSearchTests', 'ShellBackNavigationUxTests') }
        'Shortcuts' {
            # The shared host owns the shortcut cases and their full companion UX coverage.
            Add-TestTarget 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj'
        }
        default { throw 'Shell requires -Feature Failure, Navigation, or Shortcuts. Use Full for shared Shell/ViewModel changes.' }
    }
    $needsSdk = $true
}

Push-Location $repo
try {
    Write-Host "Selection: Area=$Area ToolId=$ToolId Feature=$Feature Configuration=$Configuration"
    Write-Host 'Shared SDK, protocol, schema, security, broker, build/dependency changes, or uncertain ownership require -Area Full.'
    if ($Plan -or $NoBuild) {
        Write-Host 'Using existing compiled artifacts. Counts describe those artifacts, not unbuilt source changes.'
    }
    else {
        if ($needsSdk) {
            & (Join-Path $PSScriptRoot 'prepare-focused-test-sdk.ps1') -Configuration $Configuration
        }
        # The solution builds its console projects already. Avoid building them twice.
        $builtSolution = $false
        foreach ($target in $targets) {
            if ($builtSolution -and $target.Console) { continue }
            Invoke-Dotnet @('build', $target.Path, '--configuration', $Configuration, '-m:1', '-p:UseSharedCompilation=false', '-p:BuildWindowsWebToolHost=false') | Out-Null
            if ($target.Path -eq 'MyPowerTools.slnx') { $builtSolution = $true }
        }
    }

    # Discover every target before executing any test, so an unavailable project or
    # empty filter cannot silently turn a partial run into a successful selection.
    $total = 0
    foreach ($target in $targets) {
        if ($target.Console) {
            $properties = @(Invoke-Dotnet @('msbuild', $target.Path, "-property:Configuration=$Configuration", '-getProperty:TargetPath'))
            $paths = @($properties | Where-Object { $_.Trim().EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) })
            if ($paths.Count -ne 1 -or -not (Test-Path -LiteralPath $paths[0].Trim() -PathType Leaf)) {
                throw "Built console artifact missing for $($target.Path). Build the selected project before -Plan/-NoBuild."
            }
            # Old runners ignored unknown arguments and would execute tests on --list.
            # Require the new listing protocol in the binary before launching it.
            $binary = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($paths[0].Trim()))
            $markerBytes = [Text.Encoding]::Unicode.GetBytes($consoleListMarker)
            if (-not $binary.Contains([Text.Encoding]::UTF8.GetString($markerBytes))) {
                throw "Console artifact lacks safe --list support: $($target.Path). Rebuild before discovery."
            }
            $output = @(Invoke-Dotnet @('run', '--project', $target.Path, '--configuration', $Configuration, '--no-build', '--no-restore', '--', '--list'))
            if ($output.Count -lt 2 -or $output[0] -ne $consoleListMarker) {
                throw "Invalid console discovery protocol for $($target.Path)."
            }
            $target.Names = @($output | Select-Object -Skip 1 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        }
        else {
            $arguments = @('test', $target.Path, '--configuration', $Configuration, '--no-build', '--no-restore', '--list-tests', '-p:BuildWindowsWebToolHost=false')
            if ($target.Filter) { $arguments += @('--filter', $target.Filter) }
            $output = @(Invoke-Dotnet $arguments)
            $target.Names = @($output | Where-Object { $_ -match '^\s{4}\S' } | ForEach-Object { $_.Trim() })
        }
        if ($target.Names.Count -eq 0) { throw "Zero tests discovered for $($target.Path). Refusing an empty selection." }
        Write-Host "Selected $($target.Names.Count) cases: $($target.Path)"
        foreach ($name in $target.Names) { Write-Host "  $name" }
        $total += $target.Names.Count
    }
    Write-Host "TOTAL SELECTED CASES: $total"
    if ($Plan) { Write-Host 'Plan only: no test cases executed.'; return }

    if (-not $ResultsDirectory) {
        $ResultsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mypowertools-test-dev-' + [Guid]::NewGuid().ToString('N'))
    }
    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
    $index = 0
    foreach ($target in $targets) {
        $index++
        if ($target.Console) {
            Invoke-Dotnet @('run', '--project', $target.Path, '--configuration', $Configuration, '--no-build', '--no-restore') | Out-Null
        }
        else {
            # Let VSTest generate unique filenames: a fixed filename would be
            # overwritten by each project in the solution-wide invocation.
            $targetResults = Join-Path $ResultsDirectory "target-$index"
            $arguments = @('test', $target.Path, '--configuration', $Configuration, '--no-build', '--no-restore', '-p:BuildWindowsWebToolHost=false', '--logger', 'trx', '--results-directory', $targetResults)
            if ($target.Filter) { $arguments += @('--filter', $target.Filter) }
            Invoke-Dotnet $arguments | Out-Null
        }
    }
    Write-Host "Selected suites completed; $total cases were discovered. Inspect results for platform skips: $ResultsDirectory"
}
finally { Pop-Location }

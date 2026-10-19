# Disposable Windows CI only. These are ordinary offline integration settings,
# not substitutes for any module, runtime, service executable, or readiness probe.
$script:E2ESafetyPackages = @{}

function Initialize-E2ESafety {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$DataRoot,
        [Parameter(Mandatory)][string]$ReportRoot
    )
    $ErrorActionPreference = 'Stop'
    $package = [IO.Path]::GetFullPath($PackageRoot)
    $data = [IO.Path]::GetFullPath($DataRoot)
    $report = [IO.Path]::GetFullPath($ReportRoot)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Safety setup requires disposable Windows CI.' }
    if (-not $env:LOCALAPPDATA) { throw 'Isolated LOCALAPPDATA is required.' }
    $local = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
    if ($data -ne (Join-Path $local 'MyPowerTools')) { throw 'DataRoot must match the official Dev entry point LOCALAPPDATA/MyPowerTools.' }
    if ($data.StartsWith($package.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'DataRoot cannot be inside the release package.' }
    if ((Test-Path -LiteralPath $data) -and @(Get-ChildItem -LiteralPath $data -Force).Count) { throw 'Each arm requires an empty, isolated DataRoot.' }

    # Some components use Windows KnownFolder rather than the redirected variable.
    $knownLocal = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ($knownLocal -and [IO.Path]::GetFullPath($knownLocal) -ne $local) {
        foreach ($relative in @('MyPowerTools', 'AdbForwarder')) {
            if (Test-Path -LiteralPath (Join-Path $knownLocal $relative)) { throw "Legacy KnownFolder state exists: $relative. Refusing to import or alter it." }
        }
    }
    foreach ($name in @('XBRD_SETTINGS_FILE', 'MPT_TOOL_DATA_ROOT', 'MPT_ADB_CONFIG_PATH')) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process')) { throw "Unexpected inherited configuration override: $name" }
    }
    if (@(Get-NetTCPConnection -LocalPort 9 -State Listen -ErrorAction SilentlyContinue).Count) { throw 'The offline loopback port is occupied.' }

    # Never remove or replace existing credential/device configuration.
    foreach ($path in @((Join-Path $local 'MyPowerTools\ddns\ddns-config.json'), (Join-Path $local 'AdbForwarder\devices.ini'))) {
        if (Test-Path -LiteralPath $path) { throw 'Unexpected existing DDNS or ADB configuration.' }
    }
    if (@(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -in @('ddns-config.json', 'devices.ini', 'win-space-shift.json') }).Count) {
        throw 'Release package unexpectedly contains device, DNS, or InputRemap configuration.'
    }

    $expectedUnits = @('adb-forwarder.service', 'ddns.service', 'doubao-agent.controller.service', 'remote-notifications.service', 'screenease.service', 'xbrd.codex-quota.service', 'xbrd.mem.service')
    $unitFiles = @(Get-ChildItem -LiteralPath (Join-Path $package 'service-units') -Filter unit-manifest.json -Recurse -File)
    $actualUnits = @($unitFiles | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).id })
    if (@(Compare-Object $expectedUnits $actualUnits).Count -or $actualUnits.Count -ne $expectedUnits.Count) { throw 'Unreviewed service-unit inventory; fail closed before startup.' }

    $settingsPath = Join-Path $package 'modules\xbrd\settings.json'
    $modulePath = Join-Path $package 'modules\xbrd\module.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $module = Get-Content -LiteralPath $modulePath -Raw | ConvertFrom-Json
    foreach ($name in @('enableMemSource', 'enableCodexSource')) {
        if ($settings.PSObject.Properties.Name -notcontains $name -or $settings.$name -isnot [bool]) { throw "Unrecognized XBRD settings schema: $name" }
    }
    if ($module.id -ne 'xbrd' -or $module.schemaVersion -ne '1.0' -or @($module.entrypoints).Count -ne 1) { throw 'Unrecognized XBRD module schema.' }
    $http = @($module.entrypoints)[0]
    if ($http.kind -ne 'http' -or $http.PSObject.Properties.Name -notcontains 'baseUrl' -or $http.health.path -ne '/health') { throw 'Unrecognized XBRD HTTP health schema.' }
    $unchangedRootJson = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $package -Filter '*.json' -File)) {
        $unchangedRootJson[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    if ($script:E2ESafetyPackages.ContainsKey($package)) {
        $packageProof = $script:E2ESafetyPackages[$package]
        foreach ($change in $packageProof.changes) {
            if ((Get-FileHash -LiteralPath (Join-Path $package $change.path) -Algorithm SHA256).Hash -ne $change.offlineSha256) { throw 'Previously prepared release settings changed unexpectedly.' }
        }
    } else {
        $changes = @(
            [ordered]@{ path = 'modules/xbrd/settings.json'; originalSha256 = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash; offlineSha256 = '' },
            [ordered]@{ path = 'modules/xbrd/module.json'; originalSha256 = (Get-FileHash -LiteralPath $modulePath -Algorithm SHA256).Hash; offlineSha256 = '' }
        )
        $settings.enableMemSource = $false
        $settings.enableCodexSource = $false
        $http.baseUrl = 'http://127.0.0.1:9'
        $settings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $settingsPath -Encoding utf8
        $module | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $modulePath -Encoding utf8
        foreach ($change in $changes) { $change.offlineSha256 = (Get-FileHash -LiteralPath (Join-Path $package $change.path) -Algorithm SHA256).Hash }
        $packageProof = [ordered]@{ changes = $changes; originalRootJsonSha256 = $unchangedRootJson }
        $script:E2ESafetyPackages[$package] = $packageProof
    }
    foreach ($name in $packageProof.originalRootJsonSha256.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $package $name) -Algorithm SHA256).Hash -ne $packageProof.originalRootJsonSha256[$name]) { throw 'Root release metadata changed; original hashes must remain intact.' }
    }

    # Any additional external HTTP module entry point is an unreviewed startup risk.
    $moduleFiles = @(Get-ChildItem -LiteralPath (Join-Path $package 'modules') -Filter module.json -Recurse -File)
    foreach ($file in $moduleFiles) {
        $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($manifest.PSObject.Properties.Name -notcontains 'entrypoints') { continue }
        foreach ($entry in @($manifest.entrypoints)) {
            if ($entry.kind -eq 'http') {
                $uri = $null
                if (-not [Uri]::TryCreate([string]$entry.baseUrl, [UriKind]::Absolute, [ref]$uri) -or -not $uri.IsLoopback) { throw 'Unreviewed non-loopback HTTP module entry point remains.' }
            }
        }
    }

    New-Item -ItemType Directory -Force $data, $report | Out-Null
    $notificationDirectory = Join-Path $data 'state\tools\remote-notifications'
    $imeDirectory = Join-Path $data 'state\tools\ime-manager'
    New-Item -ItemType Directory -Force $notificationDirectory, $imeDirectory | Out-Null
    $missingKey = Join-Path $notificationDirectory 'never-created-signing-key'
    if (Test-Path -LiteralPath $missingKey) { throw 'Notification key must remain absent.' }
    $notificationPath = Join-Path $notificationDirectory 'settings.json'
    [ordered]@{
        protocol = 'http'; host = '127.0.0.1'; port = 9; channel = 'ci-unused'
        pollIntervalSeconds = 5; privateKeyPath = $missingKey; keepWindowsBanners = $false
    } | ConvertTo-Json | Set-Content -LiteralPath $notificationPath -Encoding utf8
    $imePath = Join-Path $imeDirectory 'win-space-shift.json'
    [ordered]@{ enabled = $false } | ConvertTo-Json | Set-Content -LiteralPath $imePath -Encoding utf8

    $proof = [ordered]@{
        condition = 'Real full package with explicitly offline external integrations; no mock server or substituted modules.'
        package = $package; dataRoot = $data; packageChanges = $packageProof.changes
        rootReleaseMetadataUnchanged = $true; serviceUnits = $actualUnits; moduleManifestCount = $moduleFiles.Count
        xbrdMemoryPublisherEnabled = $false; xbrdCodexPublisherEnabled = $false; xbrdHealthBaseUrl = 'http://127.0.0.1:9'
        notificationSettings = $notificationPath; notificationSettingsSha256 = (Get-FileHash -LiteralPath $notificationPath -Algorithm SHA256).Hash
        notificationKeyAbsent = $true; ddnsConfigurationAbsent = $true; adbConfigurationAbsent = $true
        inputRemapEnabled = $false; inputRemapSettingsSha256 = (Get-FileHash -LiteralPath $imePath -Algorithm SHA256).Hash
        protectedInputRemapTask = '\MyPowerTools WinSpace Shift'
        modelStartup = 'Controller only; fresh data has no owned model processes; no tool commands are invoked.'
        limitations = @('XBRD health is offline.', 'Notification polling fails before network signing because no key exists.', 'DDNS remains CONFIG-MISSING.', 'No device or paid model workflow is exercised.')
    }
    $proof | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $report 'safety-config.json') -Encoding utf8
    return [pscustomobject]$proof
}

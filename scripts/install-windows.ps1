#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$NoStartMenuShortcut,
    [switch]$DesktopShortcut,
    [switch]$EnableAutostart,
    [switch]$StartRunner,
    [switch]$NoDesktopShortcut,
    [switch]$NoAutostart,
    [switch]$NoStartRunner,
    [switch]$NoOpenApp,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-environment.ps1')

if ($DesktopShortcut -and $NoDesktopShortcut) {
    throw 'DesktopShortcut and NoDesktopShortcut cannot be used together.'
}
if ($EnableAutostart -and $NoAutostart) {
    throw 'EnableAutostart and NoAutostart cannot be used together.'
}
if ($StartRunner -and $NoStartRunner) {
    throw 'StartRunner and NoStartRunner cannot be used together.'
}

$CreateDesktopShortcut = -not $NoDesktopShortcut.IsPresent
$EnableAutostartEffective = -not $NoAutostart.IsPresent
$StartRunnerEffective = -not $NoStartRunner.IsPresent
$OpenAppEffective = -not $NoOpenApp.IsPresent

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-IsInsidePath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = Resolve-FullPath $Parent
    $childFull = Resolve-FullPath $Child
    if (-not $parentFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $parentFull = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    }

    return $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-RequiredPackageContent {
    param([Parameter(Mandatory = $true)][string]$Root)

    $required = @(
        'Runner\MyPowerTools.Runner.exe',
        'Shell\MyPowerTools.Shell.Avalonia.exe',
        'Cli\MyPowerTools.Cli.exe',
        'Broker\MyPowerTools.ElevatedBroker.exe',
        'InputRemap\MyPowerTools.InputRemapHost.exe',
        'ServiceManager\MyPowerTools.ServiceManager.exe',
        'service-units',
        'MyPowerTools.exe',
        'modules',
        'schemas',
        'ui',
        'START_HERE.md',
        'Start-MyPowerTools.cmd',
        'configure-user-services.ps1',
        'start-user-runtime.ps1',
        'runtime-environment.ps1',
        'new-ota-file-manifest.ps1',
        'new-ota-delta-package.ps1',
        'invoke-ota-update.ps1',
        'ota-update.ps1',
        'package-ota-update.ps1',
        'ed25519.cs',
        'assets\MyPowerTools.ico',
        'build-provenance.json'
    )

    foreach ($relative in $required) {
        $path = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Portable package is missing $relative at $Root"
        }
    }

    $provenancePath = Join-Path $Root 'build-provenance.json'
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    $shellContract = $provenance.windowsShell
    if ($provenance.schemaVersion -lt 2 -or $null -eq $shellContract) {
        throw "Portable package has no verifiable Windows Shell build contract at $provenancePath"
    }
    $packageDistribution = if ([string]$provenance.distributionMode -eq 'web') { 'web' } else { 'full' }
    $expectedReadyToRun = $packageDistribution -eq 'full'
    if ($shellContract.runtimeIdentifier -ne 'win-x64' -or
        $shellContract.selfContained -ne $false -or
        $shellContract.publishReadyToRun -ne $expectedReadyToRun -or
        $shellContract.publishReadyToRunComposite -ne $expectedReadyToRun) {
        throw "Portable package Windows Shell build contract is incompatible at $provenancePath"
    }

    $shellHashes = [ordered]@{
        executableSha256 = 'Shell\MyPowerTools.Shell.Avalonia.exe'
        assemblySha256 = 'Shell\MyPowerTools.Shell.Avalonia.dll'
        runtimeConfigSha256 = 'Shell\MyPowerTools.Shell.Avalonia.runtimeconfig.json'
    }
    foreach ($hashProperty in $shellHashes.Keys) {
        $relative = $shellHashes[$hashProperty]
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $Root $relative) -Algorithm SHA256).Hash
        $expectedHash = [string]$shellContract.files.$hashProperty
        if ([string]::IsNullOrWhiteSpace($expectedHash) -or
            -not $actualHash.Equals($expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Portable package Shell integrity check failed for $relative. Rebuild with scripts\publish-windows.ps1 -PortableOnly."
        }
    }
}

function Invoke-SourcePortableBuild {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $publishScript = Join-Path $RepositoryRoot 'scripts\publish-windows.ps1'
    if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
        throw "Source checkout is missing its Windows publish script: $publishScript"
    }

    $pwsh = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $publishArguments = @(
        '-NoLogo'
        '-NoProfile'
        '-NonInteractive'
        '-File'
        $publishScript
        '-PortableOnly'
        '-AllowUnsigned'
        '-Channel'
        'local'
    )
    Write-Host "Building the portable package from $RepositoryRoot"
    & $pwsh.Source @publishArguments
    $publishExitCode = $LASTEXITCODE
    if ($publishExitCode -ne 0) {
        throw "Portable package build failed with exit code $publishExitCode."
    }
}

function New-Shortcut {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string]$Arguments = '',
        [string]$Description = '',
        [string]$IconLocation = ''
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Arguments = $Arguments
    $shortcut.Description = $Description
    if (-not [string]::IsNullOrWhiteSpace($IconLocation) -and (Test-Path -LiteralPath $IconLocation)) {
        $shortcut.IconLocation = $IconLocation
    }
    $shortcut.Save()
}

function Clear-StartMenuShortcuts {
    param([Parameter(Mandatory = $true)][string]$StartMenuDir)

    if (Test-Path -LiteralPath $StartMenuDir) {
        Remove-Item -LiteralPath $StartMenuDir -Recurse -Force
    }
}

function Stop-InstalledScheduledTasks {
    param([switch]$DryRun)

    $taskNames = @(
        'MyPowerTools DDNS',
        'MyPowerTools OTA Check',
        'SmartBirdThermostat',
        'EnergyServer'
    )
    foreach ($name in $taskNames) {
        if ($null -eq (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)) {
            continue
        }
        if ($DryRun) {
            Write-Host "Would stop scheduled task $name"
            continue
        }
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    }
}

function Stop-InstalledProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$DryRun
    )

    Stop-InstalledScheduledTasks -DryRun:$DryRun

    $alwaysStopProcessNames = @(
        'adb'
    )
    foreach ($name in $alwaysStopProcessNames) {
        foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
            if ($DryRun) {
                Write-Host "Would stop $name ($($process.Id))"
                continue
            }

            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
    }

    $processNames = @(
        'MyPowerTools',
        'MyPowerTools.Runner',
        'MyPowerTools.Shell.Avalonia',
        'MyPowerTools.Cli',
        'MyPowerTools.ElevatedBroker',
        'MyPowerTools.ServiceManager'
    )

    foreach ($name in $processNames) {
        foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
            $path = $null
            try {
                $path = $process.MainModule.FileName
            } catch {
                continue
            }

            if ($path -and (Test-IsInsidePath -Parent $Root -Child $path)) {
                if ($DryRun) {
                    Write-Host "Would stop $name ($($process.Id))"
                    continue
                }

                if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                    $null = $process.CloseMainWindow()
                    if ($process.WaitForExit(3000)) {
                        continue
                    }
                }

                Stop-Process -Id $process.Id -Force
            }
        }
    }

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $path = $null
        try {
            $path = $process.MainModule.FileName
        } catch {
            continue
        }

        if (-not $path -or -not (Test-IsInsidePath -Parent $Root -Child $path)) {
            continue
        }

        if ($DryRun) {
            Write-Host "Would stop installed child process $($process.ProcessName) ($($process.Id))"
            continue
        }

        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
    }

    # Host processes such as pwsh.exe / python.exe live outside the install root
    # but keep scripts or a working directory under it (DDNS watch, SmartBird).
    $rootPrefix = (Resolve-FullPath $Root).TrimEnd('\')
    $nestedMarker = $rootPrefix + '\'
    $selfId = $PID
    foreach ($proc in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) {
        if ($proc.ProcessId -eq $selfId) {
            continue
        }

        $usesRoot = $false
        $exe = [string]$proc.ExecutablePath
        if (-not [string]::IsNullOrWhiteSpace($exe)) {
            try {
                $usesRoot = Test-IsInsidePath -Parent $Root -Child $exe
            } catch {
                $usesRoot = $false
            }
        }

        $cmd = [string]$proc.CommandLine
        if (-not $usesRoot -and -not [string]::IsNullOrWhiteSpace($cmd)) {
            $usesRoot = $cmd.IndexOf($nestedMarker, [StringComparison]::OrdinalIgnoreCase) -ge 0
        }

        if (-not $usesRoot) {
            continue
        }

        if ($DryRun) {
            Write-Host "Would stop host process $($proc.Name) ($($proc.ProcessId))"
            continue
        }

        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $proc.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
}

function Start-RunnerHidden {
    param(
        [Parameter(Mandatory = $true)][string]$RunnerExe,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$ModulesRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot
    )

    if ((Get-Process -Id $PID).SessionId -eq 0) {
        throw 'MyPowerTools Runner launch is blocked in Windows Session 0.'
    }

    $processStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $RunnerExe
    $processStartInfo.WorkingDirectory = $WorkingDirectory
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.CreateNoWindow = $true

    foreach ($argument in @('--modules', $ModulesRoot, '--data-root', $DataRoot)) {
        $processStartInfo.ArgumentList.Add($argument)
    }

    [System.Diagnostics.Process]::Start($processStartInfo) | Out-Null
}

function Invoke-UserServiceConfiguration {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall')][string]$Mode,
        [Parameter(Mandatory = $true)][string]$ConfigurationScript,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [switch]$RegisterOnly
    )

    $pwsh = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $arguments = @(
        '-NoLogo'
        '-NoProfile'
        '-NonInteractive'
        '-File'
        $ConfigurationScript
        '-Mode'
        $Mode
        '-InstallRoot'
        $InstallRoot
        '-DataRoot'
        $DataRoot
    )
    if ($RegisterOnly) {
        $arguments += '-RegisterOnly'
    }
    & $pwsh.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "User service configuration failed in $Mode mode with exit code $LASTEXITCODE."
    }
}

function Invoke-InteractiveRuntimeBootstrap {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeScript,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [switch]$StartRunner
    )

    $interactiveUser = [string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($interactiveUser) -or
        -not $interactiveUser.Equals($currentUser, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "MyPowerTools was installed without launching its runtime because $currentUser has no active interactive desktop. Autostart will launch it at the next sign-in."
        return $false
    }

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskName = "MyPowerToolsInteractiveBootstrap-$PID-$([Guid]::NewGuid().ToString('N'))"
    $actionArguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File `"$RuntimeScript`" -InstallRoot `"$InstallRoot`" -DataRoot `"$DataRoot`""
    if ($StartRunner) {
        $actionArguments += ' -StartRunner'
    }

    $action = New-ScheduledTaskAction `
        -Execute $windowsPowerShell `
        -Argument $actionArguments `
        -WorkingDirectory $InstallRoot
    $principal = New-ScheduledTaskPrincipal `
        -UserId $interactiveUser `
        -LogonType Interactive `
        -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    $registeredAt = Get-Date
    $taskRegistered = $false

    try {
        Register-ScheduledTask `
            -TaskName $taskName `
            -Action $action `
            -Principal $principal `
            -Settings $settings `
            -Description 'One-time MyPowerTools interactive-session bootstrap' `
            -Force | Out-Null
        $taskRegistered = $true
        Start-ScheduledTask -TaskName $taskName

        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
            $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
            if ($task.State -ne 'Running' -and $info.LastRunTime -ge $registeredAt.AddSeconds(-1)) {
                if ($info.LastTaskResult -eq 0) {
                    return $true
                }
                Write-Warning "Interactive MyPowerTools bootstrap failed with task result $($info.LastTaskResult). Autostart remains registered for the next sign-in."
                return $false
            }
            Start-Sleep -Milliseconds 500
        }

        Write-Warning 'Interactive MyPowerTools bootstrap timed out. Autostart remains registered for the next sign-in.'
        return $false
    }
    catch {
        Write-Warning "Interactive MyPowerTools bootstrap failed: $($_.Exception.Message). Autostart remains registered for the next sign-in."
        return $false
    }
    finally {
        if ($taskRegistered) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}

$packageRootWasExplicit = $PSBoundParameters.ContainsKey('PackageRoot')
$repositoryRoot = Resolve-FullPath (Join-Path $PSScriptRoot '..')
$runningFromSource = -not $packageRootWasExplicit -and
    (Test-Path -LiteralPath (Join-Path $repositoryRoot 'MyPowerTools.slnx') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $repositoryRoot 'scripts\publish-windows.ps1') -PathType Leaf)
if ($runningFromSource) {
    $sourcePackageRoot = Join-Path $repositoryRoot 'artifacts\release\win-x64'
    if (-not $DryRun.IsPresent) {
        Invoke-SourcePortableBuild -RepositoryRoot $repositoryRoot
    }
    elseif (-not (Test-Path -LiteralPath $sourcePackageRoot -PathType Container)) {
        throw "DryRun cannot resolve a source-built package because $sourcePackageRoot is missing. Run scripts\publish-windows.ps1 -PortableOnly first."
    }
    $PackageRoot = $sourcePackageRoot
}

$PackageRootFull = Resolve-FullPath $PackageRoot
$InstallDirFull = Resolve-FullPath $InstallDir
$DataRootFull = Resolve-FullPath $DataRoot
$CanonicalInstallDir = Resolve-FullPath (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools')
$CurrentSessionId = (Get-Process -Id $PID).SessionId

$releaseVersion = ''
$releaseChannel = 'stable'
$releaseRepository = 'https://github.com/dqtz5vpvj9-create/MyPowerTools'
$distributionMode = 'full'
$provenancePath = Join-Path $PackageRootFull 'build-provenance.json'
if (Test-Path -LiteralPath $provenancePath -PathType Leaf) {
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace([string]$provenance.version)) {
        $releaseVersion = [string]$provenance.version
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$provenance.channel)) {
        $releaseChannel = [string]$provenance.channel
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$provenance.repository)) {
        $releaseRepository = [string]$provenance.repository
    }
    if ([string]$provenance.distributionMode -in @('full', 'web')) {
        $distributionMode = [string]$provenance.distributionMode
    }
}
if ([string]::IsNullOrWhiteSpace($releaseVersion)) {
    if ($runningFromSource) {
        $versionScript = Join-Path $repositoryRoot 'scripts\get-product-version.ps1'
        $versionOutput = @(& $versionScript -RepoRoot $repositoryRoot |
            ForEach-Object { [string]$_ })
        $versionObject = ($versionOutput -join [Environment]::NewLine) | ConvertFrom-Json
        $releaseVersion = [string]$versionObject.version
        $releaseChannel = [string]$versionObject.channel
        if (-not [string]::IsNullOrWhiteSpace([string]$versionObject.repository)) {
            $releaseRepository = [string]$versionObject.repository
        }
    } else {
        throw "Portable package build-provenance.json does not declare a version."
    }
}
if ($releaseVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Invalid release version '$releaseVersion' in build-provenance.json."
}

if (-not $DryRun.IsPresent -and
    -not $InstallDirFull.Equals($CanonicalInstallDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "MyPowerTools must be installed for the current user at $CanonicalInstallDir. InstallDir=$InstallDirFull"
}

Assert-RequiredPackageContent -Root $PackageRootFull

if ($InstallDirFull.Equals($PackageRootFull, [System.StringComparison]::OrdinalIgnoreCase) -or
    (Test-IsInsidePath -Parent $PackageRootFull -Child $InstallDirFull)) {
    throw "InstallDir must be outside PackageRoot. PackageRoot=$PackageRootFull InstallDir=$InstallDirFull"
}

$runnerExe = Join-Path $InstallDirFull 'Runner\MyPowerTools.Runner.exe'
$shellExe = Join-Path $InstallDirFull 'Shell\MyPowerTools.Shell.Avalonia.exe'
$cliExe = Join-Path $InstallDirFull 'Cli\MyPowerTools.Cli.exe'
$brokerExe = Join-Path $InstallDirFull 'Broker\MyPowerTools.ElevatedBroker.exe'
$inputRemapExe = Join-Path $InstallDirFull 'InputRemap\MyPowerTools.InputRemapHost.exe'
$appExe = Join-Path $InstallDirFull 'MyPowerTools.exe'
$runnerArguments = "--modules `"$InstallDirFull\modules`" --data-root `"$DataRootFull`""
$appArguments = "--data-root `"$DataRootFull`""
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MyPowerTools'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'MyPowerTools.lnk'
$iconPath = Join-Path $InstallDirFull 'assets\MyPowerTools.ico'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$installManifestPath = Join-Path $InstallDirFull 'install.manifest.json'
$legacyDotNetRootMigration = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $InstallDirFull

$plan = [ordered]@{
    packageRoot = $PackageRootFull
    installDir = $InstallDirFull
    canonicalInstallDir = $CanonicalInstallDir
    dataRoot = $DataRootFull
    createStartMenuShortcut = -not $NoStartMenuShortcut.IsPresent
    startMenuShortcutName = 'MyPowerTools.lnk'
    createDesktopShortcut = $CreateDesktopShortcut
    enableAutostart = $EnableAutostartEffective
    startRunner = $StartRunnerEffective
    openApp = $OpenAppEffective
    installerSessionId = $CurrentSessionId
    sessionZeroLaunchBlocked = $true
    advancedEntryPoints = @(
        'Cli\MyPowerTools.Cli.exe',
        'Broker\MyPowerTools.ElevatedBroker.exe',
        'InputRemap\MyPowerTools.InputRemapHost.exe',
        'ServiceManager\MyPowerTools.ServiceManager.exe',
        'Runner\MyPowerTools.Runner.exe',
        'Shell\MyPowerTools.Shell.Avalonia.exe'
    )
    releaseVersion = $releaseVersion
    releaseChannel = $releaseChannel
    distributionMode = $distributionMode
    legacyDotNetRootMigration = $legacyDotNetRootMigration
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    return
}

$legacyDotNetRootMigration = Clear-MyPowerToolsLegacyUserDotNetRoot -InstallRoot $InstallDirFull

$installParent = Split-Path -Parent $InstallDirFull
New-Item -ItemType Directory -Path $installParent -Force | Out-Null
New-Item -ItemType Directory -Path $DataRootFull -Force | Out-Null

$stagingDir = Join-Path $installParent ("MyPowerTools.__staging__." + [Guid]::NewGuid().ToString('N'))
$backupDir = Join-Path $installParent ("MyPowerTools.__backup__." + (Get-Date -Format 'yyyyMMddHHmmss'))

try {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Get-ChildItem -LiteralPath $PackageRootFull -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingDir -Recurse -Force
    }

    $serviceConfigurationScript = Join-Path $PackageRootFull 'configure-user-services.ps1'
    if (Test-Path -LiteralPath $InstallDirFull -PathType Container) {
        Invoke-UserServiceConfiguration `
            -Mode Uninstall `
            -ConfigurationScript $serviceConfigurationScript `
            -InstallRoot $InstallDirFull `
            -DataRoot $DataRootFull
    }

    $installerCwd = [IO.Directory]::GetCurrentDirectory()
    if (Test-IsInsidePath -Parent $InstallDirFull -Child $installerCwd) {
        Set-Location -LiteralPath $DataRootFull
    }

    $moveError = $null
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        Stop-InstalledProcess -Root $InstallDirFull

        if (Test-IsInsidePath -Parent $InstallDirFull -Child ([IO.Directory]::GetCurrentDirectory())) {
            Set-Location -LiteralPath $DataRootFull
        }

        if (-not (Test-Path -LiteralPath $InstallDirFull)) {
            $moveError = $null
            break
        }

        try {
            [System.IO.Directory]::Move($InstallDirFull, $backupDir)
            $moveError = $null
            break
        } catch {
            $moveError = $_
            Start-Sleep -Seconds 1
        }
    }

    if ($null -ne $moveError) {
        throw "Unable to replace '$InstallDirFull' because it is still in use (a process still has that directory as its working directory or has files open). $($moveError.Exception.Message)"
    }

    [System.IO.Directory]::Move($stagingDir, $InstallDirFull)

    $runtimeResolution = Set-MyPowerToolsProcessDotNetRoot -InstallRoot $InstallDirFull

    $manifest = [ordered]@{
        product = 'MyPowerTools'
        version = $releaseVersion
        channel = $releaseChannel
        installedAt = (Get-Date).ToString('O')
        packageRoot = $PackageRootFull
        installDir = $InstallDirFull
        dataRoot = $DataRootFull
        distributionMode = $distributionMode
        runtimeSource = [string]$runtimeResolution.source
        legacyDotNetRootMigration = $legacyDotNetRootMigration
        runner = $runnerExe
        shell = $shellExe
        cli = $cliExe
        broker = $brokerExe
        inputRemap = $inputRemapExe
        app = $appExe
        autostart = $EnableAutostartEffective
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $installManifestPath -Encoding UTF8

    $otaStateDir = Join-Path $DataRootFull 'ota-state'
    New-Item -ItemType Directory -Path $otaStateDir -Force | Out-Null
    $shippedPublicKey = Join-Path $PackageRootFull 'ota-signing-public-key.txt'
    if (Test-Path -LiteralPath $shippedPublicKey -PathType Leaf) {
        Copy-Item -LiteralPath $shippedPublicKey -Destination (
            Join-Path $otaStateDir 'ota-signing-public-key.txt') -Force
    }
    $shippedManifestName = if ($distributionMode -eq 'web') {
        'MyPowerTools-core-win-x64.manifest.json'
    } else {
        'MyPowerTools-win-x64.manifest.json'
    }
    $shippedManifest = Join-Path $PackageRootFull $shippedManifestName
    $installedFilesManifestPath = Join-Path $otaStateDir 'installed-files.manifest.json'
    if (Test-Path -LiteralPath $shippedManifest -PathType Leaf) {
        Copy-Item -LiteralPath $shippedManifest -Destination $installedFilesManifestPath -Force
    } else {
        $manifestScript = Join-Path $PackageRootFull 'new-ota-file-manifest.ps1'
        if (-not (Test-Path -LiteralPath $manifestScript -PathType Leaf)) {
            throw "Portable package is missing new-ota-file-manifest.ps1 for OTA state initialization."
        }
        [void](& $manifestScript `
            -Root $InstallDirFull `
            -OutputPath $installedFilesManifestPath `
            -Version $releaseVersion)
    }
    $installedRelease = [ordered]@{
        schemaVersion = 1
        product = 'MyPowerTools'
        version = $releaseVersion
        channel = $releaseChannel
        installedAt = (Get-Date).ToString('O')
        installDir = $InstallDirFull
        dataRoot = $DataRootFull
        repository = $releaseRepository
        manifestPath = 'installed-files.manifest.json'
        manifestSha256 = (Get-FileHash -LiteralPath $installedFilesManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        packageKind = $(if ($distributionMode -eq 'web') { 'core' } else { 'full' })
        distributionMode = $distributionMode
    }
    $installedRelease | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $otaStateDir 'installed-release.json') -Encoding UTF8

    Clear-StartMenuShortcuts -StartMenuDir $startMenuDir
    if (-not $NoStartMenuShortcut.IsPresent) {
        New-Shortcut -Path (Join-Path $startMenuDir 'MyPowerTools.lnk') -TargetPath $appExe -WorkingDirectory $InstallDirFull -Arguments $appArguments -Description 'Open MyPowerTools' -IconLocation $iconPath
    }

    if ($CreateDesktopShortcut) {
        New-Shortcut -Path $desktopShortcutPath -TargetPath $appExe -WorkingDirectory $InstallDirFull -Arguments $appArguments -Description 'Open MyPowerTools' -IconLocation $iconPath
    }

    if ($EnableAutostartEffective) {
        if (-not (Test-Path -LiteralPath $runKeyPath)) {
            New-Item -Path $runKeyPath -Force | Out-Null
        }
        Set-ItemProperty -Path $runKeyPath -Name 'MyPowerTools' -Value "`"$runnerExe`" $runnerArguments"
    } else {
        Remove-ItemProperty -Path $runKeyPath -Name 'MyPowerTools' -ErrorAction SilentlyContinue
    }

    $installedConfigurationScript = Join-Path $InstallDirFull 'configure-user-services.ps1'
    $installedRuntimeScript = Join-Path $InstallDirFull 'start-user-runtime.ps1'
    if ($CurrentSessionId -eq 0) {
        Invoke-UserServiceConfiguration `
            -Mode Install `
            -ConfigurationScript $installedConfigurationScript `
            -InstallRoot $InstallDirFull `
            -DataRoot $DataRootFull `
            -RegisterOnly
        [void](Invoke-InteractiveRuntimeBootstrap `
            -RuntimeScript $installedRuntimeScript `
            -InstallRoot $InstallDirFull `
            -DataRoot $DataRootFull `
            -StartRunner:$StartRunnerEffective)
    }
    else {
        & $installedRuntimeScript `
            -InstallRoot $InstallDirFull `
            -DataRoot $DataRootFull `
            -StartRunner:$StartRunnerEffective | Out-Null

        if ($OpenAppEffective) {
            $appStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $appStartInfo.FileName = $appExe
            $appStartInfo.WorkingDirectory = $InstallDirFull
            $appStartInfo.UseShellExecute = $false
            $appStartInfo.ArgumentList.Add('--data-root')
            $appStartInfo.ArgumentList.Add($DataRootFull)
            [System.Diagnostics.Process]::Start($appStartInfo) | Out-Null
        }
    }

    if (Test-Path -LiteralPath $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
    }
} catch {
    if ((Test-Path -LiteralPath $backupDir) -and -not (Test-Path -LiteralPath $InstallDirFull)) {
        [System.IO.Directory]::Move($backupDir, $InstallDirFull)
    }

    throw
} finally {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }
}

Write-Host "Installed MyPowerTools to $InstallDirFull"

<#
.SYNOPSIS
Applies a fast development overlay to the complete Windows installation.

.DESCRIPTION
The fast path publishes only the selected managed components, stages them beside the canonical
installation, then replaces the corresponding installed directories transactionally. Shell,
Runner, and ServiceManager continue to execute from the complete installed layout, so modules,
schemas, runtimes, service units, the launcher, and ElevatedBroker remain available.

Core publishes Shell/WebToolHost, Runner, and ServiceManager. Shell publishes Shell/WebToolHost
only. Tools runs the selected canonical tool build scripts and overlays package directories
produced under each tool's artifacts/package directory. SDK tools without an installed package
are rebuilt in place.

install-windows.ps1 remains the release and clean-install path.

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\update-windows-dev.ps1

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\update-windows-dev.ps1 -Scope Shell

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\update-windows-dev.ps1 -Scope Tools -ToolId paste-image
#>
[CmdletBinding()]
param(
    [ValidateSet('Core', 'Shell', 'Tools')]
    [string]$Scope = 'Core',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string[]]$ToolId = @(),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 60,
    [switch]$NoRestore,
    [switch]$NoOpenShell,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$scriptStart = [DateTimeOffset]::UtcNow

function Write-Phase {
    param([Parameter(Mandatory = $true)][string]$Message)

    $elapsedSeconds = [int]([DateTimeOffset]::UtcNow - $scriptStart).TotalSeconds
    Write-Host ("==> [{0}] {1} (+{2}s)" -f (Get-Date -Format 'HH:mm:ss'), $Message, $elapsedSeconds) -ForegroundColor Cyan
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$canonicalInstallRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'))
$installationParent = [IO.Path]::GetFullPath((Split-Path -Parent $canonicalInstallRoot))
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)
$installedModulesRoot = Join-Path $canonicalInstallRoot 'modules'
$installedShellExecutable = Join-Path $canonicalInstallRoot 'Shell\MyPowerTools.Shell.Avalonia.exe'
$installedRunnerExecutable = Join-Path $canonicalInstallRoot 'Runner\MyPowerTools.Runner.exe'
$installedServiceManagerExecutable = Join-Path $canonicalInstallRoot 'ServiceManager\MyPowerTools.ServiceManager.exe'
$installedWebToolHostExecutable = Join-Path $canonicalInstallRoot 'Shell\WebToolHost\MyPowerTools.WebToolHost.exe'
$installedRuntimeScript = Join-Path $canonicalInstallRoot 'start-user-runtime.ps1'
$installedAppExecutable = Join-Path $canonicalInstallRoot 'MyPowerTools.exe'
$installedOverlayManifest = Join-Path $canonicalInstallRoot 'dev-update.manifest.json'
$shellProject = Join-Path $repositoryRoot 'src\MyPowerTools.Shell.Avalonia\MyPowerTools.Shell.Avalonia.csproj'
$runnerProject = Join-Path $repositoryRoot 'src\MyPowerTools.Runner\MyPowerTools.Runner.csproj'
$serviceManagerProject = Join-Path $repositoryRoot 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj'
$devArtifactsParent = Join-Path $repositoryRoot 'artifacts\dev-update'
$managedProcessRoots = @($repositoryRoot, $canonicalInstallRoot)
$runId = [Guid]::NewGuid().ToString('N')
$artifactRoot = Join-Path $devArtifactsParent $runId
$publishRoot = Join-Path $artifactRoot 'publish'
$toolPackageRoot = Join-Path $artifactRoot 'tool-packages'
$transactionRoot = Join-Path $installationParent ".MyPowerTools-dev-update-$runId"
$payloadRoot = Join-Path $transactionRoot 'payload'
$backupRoot = Join-Path $transactionRoot 'backup'
$overlayManifestBackup = Join-Path $backupRoot 'metadata\dev-update.manifest.json'

if ($Scope -eq 'Tools' -and $ToolId.Count -eq 0) {
    throw 'Scope Tools requires at least one -ToolId.'
}
if ($Scope -ne 'Tools' -and $ToolId.Count -gt 0) {
    throw 'ToolId can only be used with Scope Tools.'
}

function Test-IsInsidePath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    return $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsSamePath {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    return [IO.Path]::GetFullPath($Left).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar).Equals(
            [IO.Path]::GetFullPath($Right).TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar),
            [StringComparison]::OrdinalIgnoreCase)
}

function Remove-VerifiedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedParent
    )

    $pathFull = [IO.Path]::GetFullPath($Path)
    $parentFull = [IO.Path]::GetFullPath($AllowedParent)
    if ((Test-IsSamePath -Left $pathFull -Right $parentFull) -or
        -not (Test-IsInsidePath -Parent $parentFull -Child $pathFull)) {
        throw "Refusing recursive deletion outside the verified parent. Path=$pathFull Parent=$parentFull"
    }
    if (Test-Path -LiteralPath $pathFull) {
        try {
            Remove-Item -LiteralPath $pathFull -Recurse -Force
        }
        catch {
            $deleteError = $_
            foreach ($attempt in 1..10) {
                if (-not [IO.Directory]::Exists($pathFull)) {
                    return
                }
                Start-Sleep -Seconds 1
                try {
                    [IO.Directory]::Delete($pathFull, $true)
                    return
                }
                catch {
                    $deleteError = $_
                }
            }
            throw $deleteError
        }
    }
}

function Resolve-InstalledRelativePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Split(
            @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries) -contains '..') {
        throw "Installed overlay path must be a safe relative path: $RelativePath"
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $canonicalInstallRoot $RelativePath))
    if (-not (Test-IsInsidePath -Parent $canonicalInstallRoot -Child $resolved)) {
        throw "Installed overlay path escaped the canonical installation: $RelativePath"
    }
    return $resolved
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$Activity
    )

    $phaseWatch = [Diagnostics.Stopwatch]::StartNew()
    Write-Phase $Activity
    & $FilePath @ArgumentList
    $nativeExitCode = $LASTEXITCODE
    $phaseWatch.Stop()
    if ($nativeExitCode -ne 0) {
        throw "$Activity failed with exit code $nativeExitCode."
    }
    Write-Host ("    {0} completed in {1}s" -f $Activity, [int]$phaseWatch.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

function Start-ProductProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string[]]$ArgumentList = @(),
        [switch]$CreateNoWindow,
        [switch]$Detached
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $Detached.IsPresent
    if ($Detached) {
        if ($CreateNoWindow) {
            $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        }
    }
    else {
        $startInfo.CreateNoWindow = $CreateNoWindow.IsPresent
    }
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    $startedProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $startedProcess) {
        throw "Process failed to start: $FilePath"
    }
    return $startedProcess
}

function Get-ProductProcessRecords {
    param([Parameter(Mandatory = $true)][string[]]$Name)

    $records = [Collections.Generic.List[object]]::new()
    foreach ($processName in $Name) {
        foreach ($process in Get-Process -Name $processName -ErrorAction SilentlyContinue) {
            $executablePath = $null
            try {
                $executablePath = $process.MainModule.FileName
            }
            catch {
            }
            $managed = $false
            if (-not [string]::IsNullOrWhiteSpace($executablePath)) {
                $managed = @($managedProcessRoots | Where-Object {
                    Test-IsInsidePath -Parent $_ -Child $executablePath
                }).Count -gt 0
            }
            $records.Add([pscustomobject]@{
                Process = $process
                Id = $process.Id
                Name = $process.ProcessName
                Path = $executablePath
                Managed = $managed
            })
        }
    }
    return $records.ToArray()
}

function Assert-NoUnmanagedConflict {
    param([Parameter(Mandatory = $true)][string[]]$Name)

    $conflicts = @(Get-ProductProcessRecords -Name $Name | Where-Object { -not $_.Managed })
    if ($conflicts.Count -eq 0) {
        return
    }

    $descriptions = @($conflicts | ForEach-Object {
        $displayPath = if ([string]::IsNullOrWhiteSpace($_.Path)) { '<path unavailable>' } else { $_.Path }
        "$($_.Name) pid=$($_.Id) path=$displayPath"
    })
    throw "A process outside the repository and canonical install roots owns a shared runtime name: $($descriptions -join '; ')"
}

function Wait-ForProcessExit {
    param(
        [Parameter(Mandatory = $true)][object[]]$Record,
        [ValidateRange(1, 30)][int]$Seconds = 5
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = @($Record | Where-Object {
            $null -ne (Get-Process -Id $_.Id -ErrorAction SilentlyContinue)
        })
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
}

function Stop-ManagedProcesses {
    param([Parameter(Mandatory = $true)][string[]]$Name)

    $records = @(Get-ProductProcessRecords -Name $Name | Where-Object Managed)
    foreach ($record in $records) {
        Stop-Process -Id $record.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $record.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
}

function Request-ShellShutdown {
    $shellRecords = @(Get-ProductProcessRecords -Name @('MyPowerTools.Shell.Avalonia') |
        Where-Object Managed)
    if ($shellRecords.Count -eq 0) {
        Stop-ManagedProcesses -Name @('MyPowerTools.WebToolHost')
        return
    }

    $clientPath = @(
        $shellRecords | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.Path) -and
            (Test-Path -LiteralPath $_.Path -PathType Leaf)
        } | ForEach-Object Path
        $installedShellExecutable
    ) | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        (Test-Path -LiteralPath $_ -PathType Leaf)
    } | Select-Object -First 1

    if (-not [string]::IsNullOrWhiteSpace($clientPath)) {
        try {
            $clientParameters = @{
                FilePath = $clientPath
                WorkingDirectory = Split-Path -Parent $clientPath
                ArgumentList = @('--shutdown-shell')
                CreateNoWindow = $true
            }
            $client = Start-ProductProcess @clientParameters
            [void]$client.WaitForExit(5000)
            $client.Dispose()
        }
        catch {
            Write-Warning "Graceful Shell shutdown failed: $($_.Exception.Message)"
        }
    }
    Wait-ForProcessExit -Record $shellRecords -Seconds 5
    Stop-ManagedProcesses -Name @('MyPowerTools.WebToolHost', 'MyPowerTools.Shell.Avalonia')
}

function Request-RunnerShutdown {
    $runnerRecords = @(Get-ProductProcessRecords -Name @('MyPowerTools.Runner') |
        Where-Object Managed)
    if ($runnerRecords.Count -eq 0) {
        return
    }

    if (Test-Path -LiteralPath $installedShellExecutable -PathType Leaf) {
        try {
            $probeParameters = @{
                FilePath = $installedShellExecutable
                WorkingDirectory = $canonicalInstallRoot
                ArgumentList = @(
                    '--smoke',
                    '--timeout-ms', '5000',
                    '--quit-runner',
                    '--modules', $installedModulesRoot,
                    '--data-root', $dataRootFull)
                CreateNoWindow = $true
            }
            $probe = Start-ProductProcess @probeParameters
            [void]$probe.WaitForExit(10000)
            $probe.Dispose()
        }
        catch {
            Write-Warning "Graceful Runner shutdown failed: $($_.Exception.Message)"
        }
    }
    Wait-ForProcessExit -Record $runnerRecords -Seconds 5
    Stop-ManagedProcesses -Name @('MyPowerTools.Runner')

    # The replacement instance must be able to acquire the single-instance
    # guard, so verify every managed runner is gone before returning. A
    # survivor here makes the new runner exit immediately ("already running")
    # and the post-swap readiness wait time out.
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $survivors = @(Get-ProductProcessRecords -Name @('MyPowerTools.Runner') |
            Where-Object Managed)
        if ($survivors.Count -eq 0) {
            return
        }
        foreach ($survivor in $survivors) {
            Stop-Process -Id $survivor.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $survivor.Id -Timeout 3 -ErrorAction SilentlyContinue
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $remaining = @(Get-ProductProcessRecords -Name @('MyPowerTools.Runner') |
        Where-Object Managed)
    if ($remaining.Count -gt 0) {
        throw "MyPowerTools.Runner refused to stop (pid=$($remaining[0].Id))."
    }
}

function Request-ServiceManagerShutdown {
    $managerRecords = @(Get-ProductProcessRecords -Name @('MyPowerTools.ServiceManager') |
        Where-Object Managed)
    if ($managerRecords.Count -eq 0) {
        return
    }

    $cli = Join-Path $canonicalInstallRoot 'Cli\MyPowerTools.Cli.exe'
    if (Test-Path -LiteralPath $cli -PathType Leaf) {
        try {
            $shutdownParameters = @{
                FilePath = $cli
                WorkingDirectory = $canonicalInstallRoot
                ArgumentList = @('service', 'shutdown')
                CreateNoWindow = $true
            }
            $shutdown = Start-ProductProcess @shutdownParameters
            [void]$shutdown.WaitForExit(15000)
            $shutdown.Dispose()
        }
        catch {
            Write-Warning "Graceful ServiceManager shutdown failed: $($_.Exception.Message)"
        }
    }

    Wait-ForProcessExit -Record $managerRecords -Seconds 5
    Stop-ManagedProcesses -Name @('MyPowerTools.ServiceManager')
    $remaining = @(Get-ProductProcessRecords -Name @('MyPowerTools.ServiceManager') |
        Where-Object Managed)
    if ($remaining.Count -gt 0) {
        throw "MyPowerTools.ServiceManager refused to stop (pid=$($remaining[0].Id))."
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Directory source is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-ToolPackageRuntimeExecutables {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $packageManifest = Join-Path $PackageRoot 'package.json'
    if (-not (Test-Path -LiteralPath $packageManifest -PathType Leaf)) {
        return @()
    }
    try {
        $manifest = Get-Content -LiteralPath $packageManifest -Raw | ConvertFrom-Json
    }
    catch {
        return @()
    }
    if ($null -eq $manifest.shared) {
        return @()
    }

    $executableNames = [Collections.Generic.List[string]]::new()
    foreach ($runtime in @($manifest.shared.runtimes)) {
        foreach ($entrypoint in @($runtime.entrypoints)) {
            $command = [string]$entrypoint.command
            if (-not [string]::IsNullOrWhiteSpace($command)) {
                $executableNames.Add([IO.Path]::GetFileName($command))
            }
        }
    }
    return @($executableNames | Sort-Object -Unique)
}

function Get-ProcessesInDirectory {
    param([Parameter(Mandatory = $true)][string]$Directory)

    return @(Get-CimInstance Win32_Process | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
        (Test-IsInsidePath -Parent $Directory -Child $_.ExecutablePath)
    })
}

function Stop-ToolPackageRuntimes {
    param([Parameter(Mandatory = $true)][object[]]$Components)

    $targets = @($Components | ForEach-Object {
        $packageId = [string]$_.PackageId
        if ([string]::IsNullOrWhiteSpace($packageId)) {
            return
        }
        [pscustomobject]@{
            PackageId = $packageId
            ModuleRoot = [IO.Path]::GetFullPath((Join-Path $installedModulesRoot $packageId))
        }
    })
    if ($targets.Count -eq 0) {
        return
    }

    Write-Phase 'Stopping tool runtimes that execute from the replaced module directories'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $running = [Collections.Generic.List[object]]::new()
        foreach ($target in $targets) {
            foreach ($process in (Get-ProcessesInDirectory -Directory $target.ModuleRoot)) {
                $running.Add($process)
            }
        }
        if ($running.Count -eq 0) {
            return
        }

        foreach ($process in $running) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.ProcessId -Timeout 3 -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    foreach ($target in $targets) {
        $survivors = @(Get-ProcessesInDirectory -Directory $target.ModuleRoot)
        if ($survivors.Count -gt 0) {
            throw "Tool runtime still holds the module directory being replaced: $($target.PackageId) ($($survivors[0].Name), pid=$($survivors[0].ProcessId))"
        }
    }
}

function Publish-ManagedComponent {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $publishArguments = @(
        'publish',
        $Project,
        '--configuration', $Configuration,
        '--output', $Output,
        '--nologo',
        '--no-self-contained',
        '-p:PublishReadyToRun=false',
        '-p:PublishAot=false')
    if ($NoRestore) {
        $publishArguments += '--no-restore'
    }
    $publishParameters = @{
        FilePath = $dotnetCommand.Source
        ArgumentList = $publishArguments
        Activity = "Publishing $Label development overlay ($Configuration)"
    }
    Invoke-Native @publishParameters
}

function Get-ToolPackageDescriptor {
    param([Parameter(Mandatory = $true)][string]$RequestedToolId)

    $packageRoot = Join-Path $repositoryRoot "tools\$RequestedToolId\artifacts\package"
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        return $null
    }

    $candidateRoots = @($packageRoot)
    $candidateRoots += @(Get-ChildItem -LiteralPath $packageRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object FullName)
    foreach ($candidateRoot in $candidateRoots) {
        $moduleManifest = Join-Path $candidateRoot 'module.json'
        $packageManifest = Join-Path $candidateRoot 'package.json'
        $packageId = $null
        if (Test-Path -LiteralPath $moduleManifest -PathType Leaf) {
            $manifest = Get-Content -LiteralPath $moduleManifest -Raw | ConvertFrom-Json
            $packageId = [string]$manifest.packageId
        }
        elseif (Test-Path -LiteralPath $packageManifest -PathType Leaf) {
            $manifest = Get-Content -LiteralPath $packageManifest -Raw | ConvertFrom-Json
            $packageId = [string]$manifest.id
        }
        if ([string]::IsNullOrWhiteSpace($packageId)) {
            continue
        }
        if ($packageId -notmatch '^[A-Za-z0-9_.-]+$') {
            throw "Tool package id contains unsupported characters: $packageId"
        }
        return [pscustomobject]@{
            ToolId = $RequestedToolId
            PackageId = $packageId
            Source = [IO.Path]::GetFullPath($candidateRoot)
        }
    }
    return $null
}

function Find-ToolSurfaceProject {
    param([Parameter(Mandatory = $true)][string]$RequestedToolId)

    $toolSourceRoot = Join-Path $repositoryRoot "tools\$RequestedToolId"
    $surfaceProjects = @(
        Get-ChildItem -LiteralPath $toolSourceRoot -Recurse -File -Filter '*.Surface.csproj' -ErrorAction SilentlyContinue)
    if ($surfaceProjects.Count -eq 0) {
        return $null
    }
    if ($surfaceProjects.Count -ne 1) {
        throw "Expected one Surface project for tool '$RequestedToolId', found $($surfaceProjects.Count)."
    }
    return $surfaceProjects[0].FullName
}

function Add-ToolSurfaceToPackage {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedToolId,
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    $surfaceProject = Find-ToolSurfaceProject -RequestedToolId $RequestedToolId
    if ($null -eq $surfaceProject) {
        return
    }

    $surfaceBuildArguments = @(
        'build',
        $surfaceProject,
        '--configuration', $Configuration,
        '--nologo',
        "-p:MyPowerToolsRepoRoot=$repositoryRoot")
    if ($NoRestore) {
        $surfaceBuildArguments += '--no-restore'
    }
    $surfaceBuildParameters = @{
        FilePath = $dotnetCommand.Source
        ArgumentList = $surfaceBuildArguments
        Activity = "Building tool Surface $RequestedToolId"
    }
    Invoke-Native @surfaceBuildParameters

    $targetFramework = 'net10.0'
    $surfaceOutput = Join-Path (Split-Path -Parent $surfaceProject) "bin\$Configuration\$targetFramework"
    $surfaceAssemblyName = [IO.Path]::GetFileNameWithoutExtension($surfaceProject) + '.dll'
    $surfaceAssemblyPath = Join-Path $surfaceOutput $surfaceAssemblyName
    if (-not (Test-Path -LiteralPath $surfaceAssemblyPath -PathType Leaf)) {
        throw "Tool Surface assembly is missing: $surfaceAssemblyPath"
    }

    $matchingToolManifests = @(
        Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -Filter 'tool.json' |
            Where-Object {
                $toolManifest = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                [string]$toolManifest.toolId -eq $RequestedToolId
            })
    if ($matchingToolManifests.Count -ne 1) {
        throw "Expected one tool.json for Surface tool '$RequestedToolId', found $($matchingToolManifests.Count)."
    }
    $surfaceTarget = Join-Path $matchingToolManifests[0].Directory.FullName 'surface'
    New-Item -ItemType Directory -Path $surfaceTarget -Force | Out-Null
    foreach ($extension in @('*.dll', '*.pdb', '*.deps.json')) {
        Get-ChildItem -LiteralPath $surfaceOutput -File -Filter $extension |
            Copy-Item -Destination $surfaceTarget -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $surfaceTarget $surfaceAssemblyName) -PathType Leaf)) {
        throw "Tool Surface was not staged under the module package: $surfaceTarget"
    }
}

function Find-ToolServiceUnits {
    param([Parameter(Mandatory = $true)][string]$RequestedToolId)

    $toolSourceRoot = Join-Path $repositoryRoot "tools\$RequestedToolId"
    if (-not (Test-Path -LiteralPath $toolSourceRoot -PathType Container)) {
        return @()
    }

    $units = [Collections.Generic.List[object]]::new()
    foreach ($manifestFile in @(
        Get-ChildItem -LiteralPath $toolSourceRoot -Recurse -File -Filter 'unit-manifest.json' -ErrorAction SilentlyContinue)) {
        $project = Get-ChildItem -LiteralPath $manifestFile.Directory.FullName -File -Filter '*.csproj' |
            Select-Object -First 1
        if ($null -eq $project) {
            continue
        }
        $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
        $unitId = [string]$manifest.id
        if ($unitId -notmatch '^[A-Za-z0-9_.-]+$') {
            throw "Service unit id contains unsupported characters: $unitId"
        }
        $units.Add([pscustomobject]@{
            UnitId = $unitId
            Project = $project.FullName
            Manifest = $manifestFile.FullName
        })
    }
    return @($units)
}

function Publish-ToolServiceUnit {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedToolId,
        [Parameter(Mandatory = $true)]$Unit
    )

    $unitRoot = Join-Path $publishRoot "service-units\$($Unit.UnitId)"
    $unitBin = Join-Path $unitRoot 'bin'
    if (Test-Path -LiteralPath $unitRoot -PathType Container) {
        Remove-Item -LiteralPath $unitRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $unitBin -Force | Out-Null
    $publishArguments = @(
        'publish',
        $Unit.Project,
        '--configuration', $Configuration,
        '--runtime', 'win-x64',
        '--self-contained', 'false',
        '--output', $unitBin,
        '--nologo',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:MyPowerToolsRepoRoot=$repositoryRoot")
    if ($NoRestore) {
        $publishArguments += '--no-restore'
    }
    $publishParameters = @{
        FilePath = $dotnetCommand.Source
        ArgumentList = $publishArguments
        Activity = "Publishing service unit $($Unit.UnitId) ($RequestedToolId)"
    }
    [void](Invoke-Native @publishParameters)
    Copy-Item -LiteralPath $Unit.Manifest -Destination (Join-Path $unitRoot 'unit-manifest.json') -Force
    $genericExe = Get-ChildItem -LiteralPath $unitBin -File -Filter '*.exe' |
        Select-Object -First 1
    if ($null -eq $genericExe) {
        throw "Service unit publish output is missing an executable: $unitBin"
    }
    return [string][IO.Path]::GetFullPath($unitRoot)
}

function Get-InstalledLayoutInventory {
    $required = [ordered]@{
        Launcher = $installedAppExecutable
        Broker = (Join-Path $canonicalInstallRoot 'Broker')
        Modules = $installedModulesRoot
        Runtimes = (Join-Path $canonicalInstallRoot 'Runtimes')
        Schemas = (Join-Path $canonicalInstallRoot 'schemas')
        ServiceUnits = (Join-Path $canonicalInstallRoot 'service-units')
        ServiceManager = (Join-Path $canonicalInstallRoot 'ServiceManager')
    }
    foreach ($entry in $required.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Value)) {
            throw "Complete installed layout component is missing: $($entry.Key)=$($entry.Value)"
        }
    }
    return [pscustomobject]@{
        ModuleManifestCount = @(
            Get-ChildItem -LiteralPath $installedModulesRoot -Recurse -File -Filter 'module.json'
        ).Count
        ToolManifestCount = @(
            Get-ChildItem -LiteralPath $installedModulesRoot -Recurse -File -Filter 'tool.json'
        ).Count
        RuntimeFileCount = @(
            Get-ChildItem -LiteralPath (Join-Path $canonicalInstallRoot 'Runtimes') -Recurse -File
        ).Count
        ServiceUnitFileCount = @(
            Get-ChildItem -LiteralPath (Join-Path $canonicalInstallRoot 'service-units') -Recurse -File
        ).Count
    }
}

function Start-InstalledRuntime {
    param([switch]$OpenShell)

    $runtimeStartParameters = @{
        FilePath = $pwshCommand.Source
        WorkingDirectory = $canonicalInstallRoot
        ArgumentList = @(
            '-NoLogo',
            '-NoProfile',
            '-NonInteractive',
            '-File', $installedRuntimeScript,
            '-InstallRoot', $canonicalInstallRoot,
            '-DataRoot', $dataRootFull,
            '-StartRunner')
        CreateNoWindow = $true
        Detached = $true
    }
    Write-Phase 'Starting the complete installed runtime'
    $runtimeProcess = Start-ProductProcess @runtimeStartParameters
    [void]$runtimeProcess.WaitForExit(120000)
    if (-not $runtimeProcess.HasExited) {
        $runtimeProcess.Kill($true)
        throw 'Installed runtime startup timed out after 120 seconds.'
    }
    $runtimeExitCode = $runtimeProcess.ExitCode
    $runtimeProcess.Dispose()
    if ($runtimeExitCode -ne 0) {
        throw "Installed runtime startup failed with exit code $runtimeExitCode."
    }

    if ($OpenShell) {
        $appParameters = @{
            FilePath = $installedAppExecutable
            WorkingDirectory = $canonicalInstallRoot
            ArgumentList = @('--data-root', $dataRootFull)
            Detached = $true
        }
        [void](Start-ProductProcess @appParameters)
    }
}

function Wait-ForInstalledProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [ValidateRange(1, 120)][int]$Seconds = 10
    )

    $expectedFull = [IO.Path]::GetFullPath($ExpectedPath)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $record = @(Get-ProductProcessRecords -Name @($Name) | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.Path) -and
            (Test-IsSamePath -Left $_.Path -Right $expectedFull)
        } | Select-Object -First 1)
        if ($record.Count -gt 0) {
            return $record[0]
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Installed process did not become ready. Name=$Name ExpectedPath=$expectedFull"
}

function Invoke-InstalledHostControlSmoke {
    $shellAssembly = Join-Path $canonicalInstallRoot 'Shell\MyPowerTools.Shell.Avalonia.dll'
    if (-not (Test-Path -LiteralPath $shellAssembly -PathType Leaf)) {
        throw "Installed Shell assembly is missing: $shellAssembly"
    }
    $smokeMilliseconds = ($TimeoutSeconds * 1000).ToString(
        [Globalization.CultureInfo]::InvariantCulture)
    Write-Phase 'Verifying installed Shell-to-Runner HostControl'
    $smokeArguments = @(
        $shellAssembly,
        '--smoke',
        '--timeout-ms', $smokeMilliseconds,
        '--modules', $installedModulesRoot,
        '--data-root', $dataRootFull)

    # The named-pipe smoke is occasionally flaky right after a runtime restart
    # (a gRPC call can hang until its client-side timeout). Retry a fresh smoke
    # process a couple of times before failing the update.
    $lastFailure = 'HostControl smoke did not report the installed tool catalog inventory.'
    foreach ($attempt in 1..3) {
        if ($attempt -gt 1) {
            Write-Host "    Retrying HostControl smoke (attempt $attempt of 3)" -ForegroundColor DarkGray
            Start-Sleep -Seconds 2
        }
        $smokeOutput = @(& $dotnetCommand.Source @smokeArguments 2>&1)
        $smokeExitCode = $LASTEXITCODE
        foreach ($line in $smokeOutput) {
            Write-Host ([string]$line)
        }
        if ($smokeExitCode -ne 0) {
            $lastFailure = "Installed Shell-to-Runner HostControl verification failed with exit code $smokeExitCode."
            continue
        }

        $catalogLine = @($smokeOutput | Where-Object {
            [string]$_ -match 'modules=(?<modules>\d+)\s+dashboardCards=(?<cards>\d+)\s+commands=(?<commands>\d+)'
        } | Select-Object -Last 1)
        if ($catalogLine.Count -eq 0) {
            continue
        }
        $catalogText = [string]$catalogLine[0]
        if ($catalogText -notmatch 'modules=(?<modules>\d+)\s+dashboardCards=(?<cards>\d+)\s+commands=(?<commands>\d+)') {
            $lastFailure = "HostControl smoke catalog output has an unexpected format: $catalogText"
            continue
        }
        return [pscustomobject]@{
            ModuleCount = [int]$Matches.modules
            DashboardCardCount = [int]$Matches.cards
            CommandCount = [int]$Matches.commands
        }
    }
    throw $lastFailure
}

function Restore-OverlayTransaction {
    param([Parameter(Mandatory = $true)][object[]]$AppliedComponents)

    $rollbackSucceeded = $false
    try {
        Request-ShellShutdown
        Request-RunnerShutdown
        Stop-ToolPackageRuntimes -Components $AppliedComponents
        if ($AppliedComponents.Count -gt 0) {
            foreach ($component in @($AppliedComponents)[($AppliedComponents.Count - 1)..0]) {
                if ($component.Applied -and (Test-Path -LiteralPath $component.Target)) {
                    Remove-VerifiedDirectory -Path $component.Target -AllowedParent $canonicalInstallRoot
                }
                if ($component.HadOriginal -and (Test-Path -LiteralPath $component.Backup)) {
                    New-Item -ItemType Directory -Path (Split-Path -Parent $component.Target) -Force | Out-Null
                    Move-Item -LiteralPath $component.Backup -Destination $component.Target
                }
            }
        }
        if (Test-Path -LiteralPath $overlayManifestBackup -PathType Leaf) {
            Copy-Item -LiteralPath $overlayManifestBackup -Destination $installedOverlayManifest -Force
        }
        elseif (Test-Path -LiteralPath $installedOverlayManifest -PathType Leaf) {
            Remove-Item -LiteralPath $installedOverlayManifest -Force
        }
        $rollbackSucceeded = $true
    }
    finally {
        if ($rollbackSucceeded) {
            Start-InstalledRuntime -OpenShell:(-not $NoOpenShell)
        }
    }
    return $rollbackSucceeded
}

$toolBuildScripts = [Collections.Generic.List[string]]::new()
foreach ($requestedToolId in $ToolId) {
    if ($requestedToolId -notmatch '^[A-Za-z0-9_.-]+$') {
        throw "ToolId contains unsupported characters: $requestedToolId"
    }
    $toolBuildScript = Join-Path $repositoryRoot "tools\$requestedToolId\build.ps1"
    if (-not (Test-Path -LiteralPath $toolBuildScript -PathType Leaf)) {
        throw "Tool build script does not exist: $toolBuildScript"
    }
    $toolBuildScripts.Add($toolBuildScript)
}

$plannedRelativePaths = switch ($Scope) {
    'Core' { @('Shell', 'Runner', 'ServiceManager') }
    'Shell' { @('Shell') }
    default { @($ToolId | ForEach-Object { "modules\<package from $_>" }) }
}
$activeProcesses = @(Get-ProductProcessRecords -Name @(
    'MyPowerTools.Shell.Avalonia',
    'MyPowerTools.WebToolHost',
    'MyPowerTools.Runner',
    'MyPowerTools.ServiceManager') | ForEach-Object {
        [ordered]@{
            name = $_.Name
            id = $_.Id
            path = $_.Path
            managed = $_.Managed
        }
    })
$plan = [ordered]@{
    scope = $Scope
    configuration = $Configuration
    repositoryRoot = $repositoryRoot
    installRoot = $canonicalInstallRoot
    modulesRoot = $installedModulesRoot
    dataRoot = $dataRootFull
    overlayPaths = $plannedRelativePaths
    toolBuildScripts = $toolBuildScripts.ToArray()
    openShell = -not $NoOpenShell.IsPresent
    activeProcesses = $activeProcesses
    preservedInstalledLayout = @(
        'MyPowerTools.exe',
        'Broker',
        'Cli',
        'modules not selected by Scope Tools',
        'Runtimes',
        'schemas',
        'service-units not selected by Scope Tools',
        'Tools')
    excludedReleaseWork = @(
        'all-tool rebuild',
        'ReadyToRun',
        'NativeAOT',
        'runtime staging',
        'ZIP generation',
        'shortcut and uninstall registration')
}
if ($DryRun) {
    $plan | ConvertTo-Json -Depth 6
    return
}

foreach ($requiredPath in @(
    $canonicalInstallRoot,
    $installedModulesRoot,
    $installedRuntimeScript,
    $installedAppExecutable,
    $installedShellExecutable,
    $installedRunnerExecutable,
    $installedServiceManagerExecutable,
    $shellProject,
    $runnerProject,
    $serviceManagerProject)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required development update path is missing: $requiredPath"
    }
}

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
$pwshCommand = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
    Select-Object -First 1

Assert-NoUnmanagedConflict -Name @(
    'MyPowerTools.Shell.Avalonia',
    'MyPowerTools.WebToolHost',
    'MyPowerTools.Runner',
    'MyPowerTools.ServiceManager')

$inventoryBefore = Get-InstalledLayoutInventory
$stagedComponents = [Collections.Generic.List[object]]::new()
$appliedComponents = [Collections.Generic.List[object]]::new()
$transactionStarted = $false
$updateSucceeded = $false
$rollbackSucceeded = $false

try {
    New-Item -ItemType Directory -Path $publishRoot, $toolPackageRoot -Force | Out-Null

    if ($Scope -in @('Core', 'Shell')) {
        $shellPublish = Join-Path $publishRoot 'Shell'
        $shellPublishParameters = @{
            Project = $shellProject
            Output = $shellPublish
            Label = 'Shell and WebToolHost'
        }
        Publish-ManagedComponent @shellPublishParameters
        foreach ($requiredOutput in @(
            (Join-Path $shellPublish 'MyPowerTools.Shell.Avalonia.exe'),
            (Join-Path $shellPublish 'WebToolHost\MyPowerTools.WebToolHost.exe'))) {
            if (-not (Test-Path -LiteralPath $requiredOutput -PathType Leaf)) {
                throw "Shell development publish output is missing: $requiredOutput"
            }
        }
        $isolationProbeParameters = @{
            FilePath = Join-Path $shellPublish 'WebToolHost\MyPowerTools.WebToolHost.exe'
            ArgumentList = @('--isolation-probe')
            Activity = 'Verifying staged WebToolHost isolation'
        }
        Invoke-Native @isolationProbeParameters
        $stagedComponents.Add([pscustomobject]@{
            Kind = 'managed'
            RelativePath = 'Shell'
            Source = $shellPublish
            PackageId = ''
            RuntimeExecutables = @()
        })
    }

    if ($Scope -eq 'Core') {
        $runnerPublish = Join-Path $publishRoot 'Runner'
        $runnerPublishParameters = @{
            Project = $runnerProject
            Output = $runnerPublish
            Label = 'Runner'
        }
        Publish-ManagedComponent @runnerPublishParameters
        $publishedRunner = Join-Path $runnerPublish 'MyPowerTools.Runner.exe'
        if (-not (Test-Path -LiteralPath $publishedRunner -PathType Leaf)) {
            throw "Runner development publish output is missing: $publishedRunner"
        }
        $stagedComponents.Add([pscustomobject]@{
            Kind = 'managed'
            RelativePath = 'Runner'
            Source = $runnerPublish
            PackageId = ''
            RuntimeExecutables = @()
        })

        $serviceManagerPublish = Join-Path $publishRoot 'ServiceManager'
        $serviceManagerPublishParameters = @{
            Project = $serviceManagerProject
            Output = $serviceManagerPublish
            Label = 'ServiceManager'
        }
        Publish-ManagedComponent @serviceManagerPublishParameters
        $publishedServiceManager = Join-Path $serviceManagerPublish 'MyPowerTools.ServiceManager.exe'
        if (-not (Test-Path -LiteralPath $publishedServiceManager -PathType Leaf)) {
            throw "ServiceManager development publish output is missing: $publishedServiceManager"
        }
        $stagedComponents.Add([pscustomobject]@{
            Kind = 'managed'
            RelativePath = 'ServiceManager'
            Source = $serviceManagerPublish
            PackageId = ''
            RuntimeExecutables = @()
        })
    }

    foreach ($toolBuildScript in $toolBuildScripts) {
        $requestedToolId = [IO.Path]::GetFileName((Split-Path -Parent $toolBuildScript))
        $toolBuildParameters = @{
            FilePath = $pwshCommand.Source
            ArgumentList = @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-File', $toolBuildScript,
                '-MyPowerToolsRepoRoot', $repositoryRoot,
                '-Configuration', $Configuration)
            Activity = "Building tool $requestedToolId"
        }
        Invoke-Native @toolBuildParameters
        $descriptor = Get-ToolPackageDescriptor -RequestedToolId $requestedToolId
        if ($null -eq $descriptor) {
            Write-Host "==> Tool $requestedToolId produced an SDK/loose-tool build; no installed package overlay is required." -ForegroundColor DarkCyan
            continue
        }
        Add-ToolSurfaceToPackage -RequestedToolId $requestedToolId -PackageRoot $descriptor.Source
        $stagedComponents.Add([pscustomobject]@{
            Kind = 'tool-package'
            RelativePath = "modules\$($descriptor.PackageId)"
            Source = $descriptor.Source
            PackageId = $descriptor.PackageId
            RuntimeExecutables = @(Get-ToolPackageRuntimeExecutables -PackageRoot $descriptor.Source)
        })
        foreach ($unit in @(Find-ToolServiceUnits -RequestedToolId $requestedToolId)) {
            $unitSource = Publish-ToolServiceUnit -RequestedToolId $requestedToolId -Unit $unit
            $relativePath = "service-units\$($unit.UnitId)"
            $installedUnit = Resolve-InstalledRelativePath -RelativePath $relativePath
            if (-not (Test-Path -LiteralPath $installedUnit -PathType Container)) {
                throw "Installed service unit is missing for overlay: $installedUnit"
            }
            $stagedComponents.Add([pscustomobject]@{
                Kind = 'service-unit'
                RelativePath = $relativePath
                Source = $unitSource
                PackageId = ''
                RuntimeExecutables = @()
            })
        }
    }

    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-VerifiedDirectory -Path $transactionRoot -AllowedParent $installationParent
    }
    New-Item -ItemType Directory -Path $payloadRoot, $backupRoot -Force | Out-Null
    if (Test-Path -LiteralPath $installedOverlayManifest -PathType Leaf) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $overlayManifestBackup) -Force | Out-Null
        Copy-Item -LiteralPath $installedOverlayManifest -Destination $overlayManifestBackup -Force
    }
    foreach ($component in $stagedComponents) {
        $payloadDestination = [IO.Path]::GetFullPath(
            (Join-Path $payloadRoot $component.RelativePath))
        if (-not (Test-IsInsidePath -Parent $payloadRoot -Child $payloadDestination)) {
            throw "Payload path escaped the transaction root: $($component.RelativePath)"
        }
        Copy-DirectoryContents -Source $component.Source -Destination $payloadDestination
    }

    Write-Phase 'Stopping Shell, Runner, and ServiceManager for the component swap'
    Request-ShellShutdown
    Request-RunnerShutdown
    Request-ServiceManagerShutdown
    Stop-ToolPackageRuntimes -Components $stagedComponents
    foreach ($component in $stagedComponents) {
        if ($component.Kind -ne 'service-unit') {
            continue
        }
        $unitDirectory = Resolve-InstalledRelativePath -RelativePath $component.RelativePath
        foreach ($process in @(Get-ProcessesInDirectory -Directory $unitDirectory)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.ProcessId -Timeout 3 -ErrorAction SilentlyContinue
        }
    }
    $transactionStarted = $true

    foreach ($component in $stagedComponents) {
        $target = Resolve-InstalledRelativePath -RelativePath $component.RelativePath
        $backup = [IO.Path]::GetFullPath((Join-Path $backupRoot $component.RelativePath))
        $payload = [IO.Path]::GetFullPath((Join-Path $payloadRoot $component.RelativePath))
        $record = [pscustomobject]@{
            RelativePath = $component.RelativePath
            Target = $target
            Backup = $backup
            HadOriginal = (Test-Path -LiteralPath $target)
            Applied = $false
            PackageId = [string]$component.PackageId
            RuntimeExecutables = @($component.RuntimeExecutables)
        }
        $appliedComponents.Add($record)

        New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
        if ($record.HadOriginal) {
            Move-Item -LiteralPath $target -Destination $backup
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Move-Item -LiteralPath $payload -Destination $target
        $record.Applied = $true
    }

    $activeComponentMap = [ordered]@{}
    if (Test-Path -LiteralPath $installedOverlayManifest -PathType Leaf) {
        $previousOverlay = Get-Content -LiteralPath $installedOverlayManifest -Raw |
            ConvertFrom-Json
        foreach ($previousComponent in @($previousOverlay.components)) {
            $previousRelativePath = [string]$previousComponent.relativePath
            if ([string]::IsNullOrWhiteSpace($previousRelativePath)) {
                continue
            }
            $previousConfiguration = if (
                $previousComponent.PSObject.Properties.Name -contains 'configuration') {
                [string]$previousComponent.configuration
            }
            else {
                [string]$previousOverlay.configuration
            }
            $activeComponentMap[$previousRelativePath] = [ordered]@{
                kind = [string]$previousComponent.kind
                relativePath = $previousRelativePath
                configuration = $previousConfiguration
            }
        }
    }
    foreach ($stagedComponent in $stagedComponents) {
        $activeComponentMap[$stagedComponent.RelativePath] = [ordered]@{
            kind = $stagedComponent.Kind
            relativePath = $stagedComponent.RelativePath
            configuration = $Configuration
        }
    }
    $overlayManifest = [ordered]@{
        schemaVersion = 1
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        repositoryRoot = $repositoryRoot
        lastScope = $Scope
        configuration = $Configuration
        frameworkDependentManagedComponents = $true
        components = @($activeComponentMap.Values)
    }
    $overlayManifest |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $installedOverlayManifest -Encoding UTF8

    Start-InstalledRuntime -OpenShell:(-not $NoOpenShell)

    $runnerProcessParameters = @{
        Name = 'MyPowerTools.Runner'
        ExpectedPath = $installedRunnerExecutable
        Seconds = $TimeoutSeconds
    }
    $runnerRecord = Wait-ForInstalledProcess @runnerProcessParameters
    $serviceManagerProcessParameters = @{
        Name = 'MyPowerTools.ServiceManager'
        ExpectedPath = Join-Path $canonicalInstallRoot 'ServiceManager\MyPowerTools.ServiceManager.exe'
        Seconds = $TimeoutSeconds
    }
    $serviceManagerRecord = Wait-ForInstalledProcess @serviceManagerProcessParameters

    $catalogInventory = Invoke-InstalledHostControlSmoke

    $shellRecord = $null
    if (-not $NoOpenShell) {
        $shellProcessParameters = @{
            Name = 'MyPowerTools.Shell.Avalonia'
            ExpectedPath = $installedShellExecutable
            Seconds = $TimeoutSeconds
        }
        $shellRecord = Wait-ForInstalledProcess @shellProcessParameters
    }

    $inventoryAfter = Get-InstalledLayoutInventory
    $overlaidServiceUnits = @($stagedComponents | Where-Object Kind -eq 'service-unit')
    if ($inventoryAfter.RuntimeFileCount -ne $inventoryBefore.RuntimeFileCount) {
        throw 'The fast update changed preserved runtime inventory.'
    }
    if ($overlaidServiceUnits.Count -eq 0 -and
        $inventoryAfter.ServiceUnitFileCount -ne $inventoryBefore.ServiceUnitFileCount) {
        throw 'The fast update changed preserved runtime or service-unit inventory.'
    }
    if ($Scope -in @('Core', 'Shell') -and
        ($inventoryAfter.ModuleManifestCount -ne $inventoryBefore.ModuleManifestCount -or
         $inventoryAfter.ToolManifestCount -ne $inventoryBefore.ToolManifestCount)) {
        throw 'The core fast update changed the installed module catalog inventory.'
    }
    if ($catalogInventory.ModuleCount -lt $inventoryAfter.ModuleManifestCount -or
        $catalogInventory.DashboardCardCount -lt $inventoryAfter.ToolManifestCount) {
        throw 'HostControl loaded fewer modules or dashboard cards than the installed package inventory.'
    }

    $updateSucceeded = $true
    Write-Host ("==> Update completed in {0}s" -f [int]([DateTimeOffset]::UtcNow - $scriptStart).TotalSeconds) -ForegroundColor Green
    [pscustomobject]@{
        State = 'ready'
        Scope = $Scope
        Configuration = $Configuration
        InstallRoot = $canonicalInstallRoot
        ShellPath = $installedShellExecutable
        ShellProcessId = if ($null -eq $shellRecord) { $null } else { $shellRecord.Id }
        RunnerPath = $installedRunnerExecutable
        RunnerProcessId = $runnerRecord.Id
        ServiceManagerPath = $serviceManagerRecord.Path
        ModulesRoot = $installedModulesRoot
        ModuleManifestCount = $inventoryAfter.ModuleManifestCount
        ToolManifestCount = $inventoryAfter.ToolManifestCount
        LoadedModuleCount = $catalogInventory.ModuleCount
        DashboardCardCount = $catalogInventory.DashboardCardCount
        CommandCount = $catalogInventory.CommandCount
        RuntimeFileCount = $inventoryAfter.RuntimeFileCount
        ServiceUnitFileCount = $inventoryAfter.ServiceUnitFileCount
        OverlayComponents = @($stagedComponents | ForEach-Object RelativePath)
        ToolIds = $ToolId
    }
}
catch {
    $updateError = $_
    if ($transactionStarted) {
        try {
            $rollbackSucceeded = Restore-OverlayTransaction -AppliedComponents $appliedComponents.ToArray()
        }
        catch {
            Write-Warning "Development overlay rollback raised an additional error: $($_.Exception.Message)"
        }
    }
    throw $updateError
}
finally {
    if ($updateSucceeded -or $rollbackSucceeded -or -not $transactionStarted) {
        try {
            Remove-VerifiedDirectory -Path $transactionRoot -AllowedParent $installationParent
        }
        catch {
            Write-Warning "Transaction directory cleanup failed: $($_.Exception.Message)"
        }
    }
    try {
        Remove-VerifiedDirectory -Path $artifactRoot -AllowedParent $devArtifactsParent
    }
    catch {
        Write-Warning "Development artifact cleanup failed: $($_.Exception.Message)"
    }
}

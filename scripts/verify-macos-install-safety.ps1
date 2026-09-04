<#
.SYNOPSIS
    Exercises installer rollback functions and, on macOS, signed fixture installations.
.DESCRIPTION
    No .NET SDK, submodules, Pester, signing credentials or user configuration are required.
    Native fixtures compile a small C sleeper with Xcode Command Line Tools, not the product.
    All installations pass -SkipLaunchAgents and use a temporary Applications/Data root.
#>
[CmdletBinding()]
param([string]$ReportPath = '')

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'install-macos-base.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
# Load the actual production functions without running the installer entry point.
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) {
        . ([scriptblock]::Create($statement.Extent.Text))
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('mpt-install-safety-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$registeredBundles = [Collections.Generic.List[string]]::new()

function Assert-Equal($Actual, $Expected, [string]$Reason) {
    if ($Actual -cne $Expected) { throw "${Reason}: expected '$Expected', got '$Actual'." }
}
function Assert-True([bool]$Condition, [string]$Reason) {
    if (-not $Condition) { throw $Reason }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    if ($null -eq $caught) { throw "Expected failure matching '$Pattern'." }
    if ($caught.Exception.Message -notmatch $Pattern) { throw "Unexpected failure: $($caught.Exception.Message)" }
}
function Invoke-Case([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        $results.Add([pscustomobject]@{ name = $Name; passed = $true; detail = '' })
        Write-Host "PASS $Name"
    }
    catch {
        $results.Add([pscustomobject]@{ name = $Name; passed = $false; detail = $_.ToString() })
        Write-Host "FAIL ${Name}: $_"
    }
}
function New-SwapFixture([switch]$InitialInstall) {
    $root = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + ' Applications [test]')
    $stage = Join-Path $root 'staging/MyPowerTools.app'
    $target = Join-Path $root 'MyPowerTools.app'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $stage 'version'), 'new')
    if (-not $InitialInstall) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $target 'version'), 'old')
    }
    return @{ StagedApp = $stage; TargetApp = $target; BackupApp = (Join-Path $root 'backup.app') }
}
function Read-Version([string]$Bundle) { return [IO.File]::ReadAllText((Join-Path $Bundle 'version')) }

try {
    Invoke-Case 'process selection uses executable prefix and directory boundary' {
        $bundle = Join-Path $testRoot 'Applications [test]/MyPowerTools.app'
        $ids = @(Select-AppBundleProcessIds -AppBundlePath $bundle -ExcludedProcessId 99 -ProcessLines @(
            " 10 $bundle/Contents/MacOS/MyPowerTools",
            " 11 $bundle/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner",
            " 12 ${bundle}.backup/Contents/MacOS/Other",
            " 13 /usr/bin/editor --file $bundle/Contents/Info.plist",
            " 14 /usr/bin/pwsh -File $bundle/Contents/Resources/scripts/install-macos.ps1",
            " 99 $bundle/Contents/MacOS/MyPowerTools",
            " 1 $bundle/Contents/MacOS/MyPowerTools",
            'unparseable', " 10 $bundle/Contents/MacOS/MyPowerTools"
        ))
        Assert-Equal ($ids -join ',') '10,11' 'Selected process IDs'
    }
    Invoke-Case 'empty process list is valid' {
        $ids = @(Select-AppBundleProcessIds -AppBundlePath $testRoot -ProcessLines @())
        Assert-Equal $ids.Count 0 'Empty selection'
    }
    Invoke-Case 'successful replacement retains the old bundle' {
        $f = New-SwapFixture
        Invoke-MacOSBundleSwap @f
        Assert-Equal (Read-Version $f.TargetApp) 'new' 'Installed version'
        Assert-Equal (Read-Version $f.BackupApp) 'old' 'Backup version'
        Assert-True (-not (Test-Path -LiteralPath $f.StagedApp)) 'Stage should have moved'
    }
    Invoke-Case 'activation failure restores old bundle before restoring agents' {
        $f = New-SwapFixture
        $events = [Collections.Generic.List[string]]::new()
        Assert-Throws {
            Invoke-MacOSBundleSwap @f -Prepare { $events.Add('prepare') } -Activate {
                $events.Add('activate'); throw 'injected bootstrap failure'
            } -Deactivate {
                Assert-Equal (Read-Version $f.TargetApp) 'new' 'Deactivate uses new bundle'
                $events.Add('deactivate')
            } -Restore {
                Assert-Equal (Read-Version $f.TargetApp) 'old' 'Restore uses old bundle'
                $events.Add('restore')
            }
        } 'injected bootstrap failure'
        Assert-Equal ($events -join ',') 'prepare,activate,deactivate,restore' 'Callback ordering'
        Assert-Equal (Read-Version $f.StagedApp) 'new' 'Failed candidate remains recoverable'
    }
    Invoke-Case 'preparation failure does not move either bundle' {
        $f = New-SwapFixture
        $events = [Collections.Generic.List[string]]::new()
        Assert-Throws {
            Invoke-MacOSBundleSwap @f -Prepare { throw 'injected stop failure' } -Activate {
                throw 'activation must not run'
            } -Restore { $events.Add('restore') }
        } 'injected stop failure'
        Assert-Equal (Read-Version $f.TargetApp) 'old' 'Old bundle'
        Assert-Equal (Read-Version $f.StagedApp) 'new' 'Candidate'
        Assert-Equal ($events -join ',') 'restore' 'Preparation rollback'
    }
    Invoke-Case 'first-install activation failure leaves no installed bundle' {
        $f = New-SwapFixture -InitialInstall
        Assert-Throws { Invoke-MacOSBundleSwap @f -Activate { throw 'first install failed' } } 'first install failed'
        Assert-True (-not (Test-Path -LiteralPath $f.TargetApp)) 'Failed new installation remains absent'
        Assert-Equal (Read-Version $f.StagedApp) 'new' 'Candidate retained'
    }
    Invoke-Case 'backup collision is rejected without overwriting recovery data' {
        $f = New-SwapFixture
        New-Item -ItemType Directory -Path $f.BackupApp -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $f.BackupApp 'version'), 'recovery')
        Assert-Throws { Invoke-MacOSBundleSwap @f } 'Backup already exists'
        Assert-Equal (Read-Version $f.BackupApp) 'recovery' 'Existing backup'
        Assert-Equal (Read-Version $f.TargetApp) 'old' 'Existing installation'
    }
    Invoke-Case 'missing staging directory is rejected before preparation' {
        $f = New-SwapFixture
        Remove-Item -LiteralPath $f.StagedApp -Recurse -Force
        Assert-Throws { Invoke-MacOSBundleSwap @f -Prepare { throw 'must not prepare' } } 'Staged application does not exist'
        Assert-Equal (Read-Version $f.TargetApp) 'old' 'Existing installation'
    }
    Invoke-Case 'failure between directory renames restores the old bundle' {
        $f = New-SwapFixture
        Assert-Throws {
            Invoke-MacOSBundleSwap @f -Prepare { Remove-Item -LiteralPath $f.StagedApp -Recurse -Force }
        } '.*'
        Assert-Equal (Read-Version $f.TargetApp) 'old' 'Old bundle restored after second rename fails'
    }
    Invoke-Case 'rollback failure preserves recovery trees and original diagnostic' {
        $f = New-SwapFixture
        $caught = $null
        try {
            Invoke-MacOSBundleSwap @f -Activate { throw 'bootstrap fault' } -Deactivate { throw 'worker still alive' }
        }
        catch { $caught = $_ }
        Assert-True ($null -ne $caught) 'Rollback should fail explicitly'
        Assert-True ($caught.Exception.Data.Contains('MptRollbackIncomplete')) 'Recovery marker'
        Assert-True ($caught.Exception.Message -match 'bootstrap fault.*worker still alive') 'Both failures reported'
        Assert-Equal (Read-Version $f.BackupApp) 'old' 'Old backup retained'
        Assert-Equal (Read-Version $f.TargetApp) 'new' 'Running candidate not moved while worker persists'
    }
    Invoke-Case 'exclusive installation lock prevents overlapping writers' {
        $path = Join-Path $testRoot 'exclusive.lock'
        $handle = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            Assert-Throws {
                $second = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $second.Dispose()
            } '.*'
        }
        finally { $handle.Dispose() }
        $next = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $next.Dispose()
        Assert-True (Test-Path -LiteralPath $path) 'Lock inode retained after release'
    }

    if ($IsMacOS) {
        $userId = (Invoke-MacOSInstallCommand '/usr/bin/id' @('-u')).Output.Trim()
        function New-NativeBundle([string]$Path, [string]$Payload) {
            $hosts = @(
                @('', 'com.mypowertools.desktop', 'MyPowerTools'),
                @('MyPowerTools Shell.app', 'com.mypowertools.shell', 'MyPowerTools.Shell.Avalonia'),
                @('MyPowerTools Runner.app', 'com.mypowertools.runner', 'MyPowerTools.Runner'),
                @('MyPowerTools ServiceManager.app', 'com.mypowertools.servicemanager', 'MyPowerTools.ServiceManager'),
                @('MyPowerTools Remote Notifications.app', 'com.mypowertools.remotenotifications', 'RemoteNotifications.Service')
            )
            foreach ($hostInfo in $hosts) {
                $bundle = if ($hostInfo[0]) { Join-Path $Path "Contents/MacOS/Helpers/$($hostInfo[0])" } else { $Path }
                $mac = Join-Path $bundle 'Contents/MacOS'
                New-Item -ItemType Directory -Path $mac -Force | Out-Null
                Copy-Item -LiteralPath $fixtureExecutable -Destination (Join-Path $mac $hostInfo[2])
                $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$($hostInfo[1])</string>
<key>CFBundleExecutable</key><string>$($hostInfo[2])</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1.0.0</string>
<key>CFBundleShortVersionString</key><string>1.0.0</string>
</dict></plist>
"@
                [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'), $plist)
            }
            foreach ($relative in @('Contents/Resources', 'Contents/MacOS/modules', 'Contents/MacOS/ServiceUnits')) {
                New-Item -ItemType Directory -Path (Join-Path $Path $relative) -Force | Out-Null
                [IO.File]::WriteAllText((Join-Path $Path "$relative/fixture.txt"), $Payload)
                # The product stores signed data under Contents/MacOS. Match its
                # data-file signing pass before sealing the outer fixture bundle.
                if ($relative.StartsWith('Contents/MacOS/', [StringComparison]::Ordinal)) {
                    [void](Invoke-MacOSInstallCommand '/usr/bin/codesign' @(
                        '--force', '--sign', '-', '--timestamp=none', (Join-Path $Path "$relative/fixture.txt")))
                }
            }
            foreach ($helper in @(Get-ChildItem -LiteralPath (Join-Path $Path 'Contents/MacOS/Helpers') -Directory)) {
                [void](Invoke-MacOSInstallCommand '/usr/bin/codesign' @('--force', '--sign', '-', '--timestamp=none', $helper.FullName))
            }
            [void](Invoke-MacOSInstallCommand '/usr/bin/codesign' @('--force', '--sign', '-', '--timestamp=none', $Path))
            Assert-MacOSInstallBundle $Path
        }
        function Start-FixtureProcess([string]$File, [string[]]$Arguments) {
            $info = [Diagnostics.ProcessStartInfo]::new($File)
            $info.UseShellExecute = $false
            foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($info)
            $processes.Add($process)
            Start-Sleep -Milliseconds 200
            if ($process.HasExited) { throw "Fixture process exited before the test: $File; exit=$($process.ExitCode)" }
            return $process
        }
        $nativeRoot = Join-Path $testRoot 'native Applications [fixtures]'
        New-Item -ItemType Directory -Path $nativeRoot -Force | Out-Null
        $nativeRoot = Get-MacOSPhysicalDirectory $nativeRoot
        # Apple system binaries can retain platform-specific requirements after copying
        # and re-signing. Build an ordinary native executable for both test architectures.
        $fixtureSource = Join-Path $nativeRoot 'sleeper.c'
        $fixtureExecutable = Join-Path $nativeRoot 'sleeper'
        [IO.File]::WriteAllText($fixtureSource, @'
#include <stdlib.h>
#include <unistd.h>
int main(int argc, char **argv) {
    unsigned int remaining = argc > 1 ? (unsigned int)strtoul(argv[1], 0, 10) : 180;
    while (remaining > 0) { remaining = sleep(remaining); }
    return 0;
}
'@)
        [void](Invoke-MacOSInstallCommand '/usr/bin/xcrun' @(
            'clang', '-O2', '-o', $fixtureExecutable, $fixtureSource))
        $apps = Join-Path $nativeRoot 'Applications'
        $data = Join-Path $nativeRoot 'Data'
        $source = Join-Path $nativeRoot 'Source.app'
        $installed = Join-Path $apps 'MyPowerTools.app'
        New-NativeBundle $source 'original'
        $registeredBundles.Add($installed)

        Invoke-Case 'native signed fixture installs into paths containing spaces and brackets' {
            & $installer -SourceApp $source -ApplicationsRoot $apps -DataRoot $data -SkipLaunchAgents
            Assert-MacOSInstallBundle $installed
            Assert-Equal ([IO.File]::ReadAllText((Join-Path $installed 'Contents/Resources/fixture.txt'))) 'original' 'Installed fixture'
        }
        Invoke-Case 'native command handling tolerates expected exit with native error preference enabled' {
            $PSNativeCommandUseErrorActionPreference = $true
            $result = Invoke-MacOSInstallCommand '/bin/test' @('-e', (Join-Path $testRoot 'absent')) -AllowFailure
            Assert-Equal $result.ExitCode 1 'Expected nonzero exit'
        }
        Invoke-Case 'damaged signature is rejected while old process and installation remain intact' {
            $damaged = Join-Path $nativeRoot 'Damaged.app'
            [void](Invoke-MacOSInstallCommand '/usr/bin/ditto' @($source, $damaged))
            [IO.File]::WriteAllText((Join-Path $damaged 'Contents/Resources/fixture.txt'), 'tampered')
            $old = Start-FixtureProcess (Join-Path $installed 'Contents/MacOS/MyPowerTools') @('180')
            try {
                Assert-Throws { & $installer -SourceApp $damaged -ApplicationsRoot $apps -DataRoot $data -SkipLaunchAgents } 'codesign'
                $old.Refresh()
                Assert-True (-not $old.HasExited) 'Verification failure must not stop old process'
                Assert-MacOSInstallBundle $installed
                Assert-Equal @(Get-ChildItem -LiteralPath $apps -Directory -Filter 'MyPowerTools.backup.*.app').Count 0 'No backup before validation succeeds'
            }
            finally { if (-not $old.HasExited) { $old.Kill(); $old.WaitForExit() } }
        }
        Invoke-Case 'replacement stops owned executable and preserves unrelated command arguments' {
            $owned = Start-FixtureProcess (Join-Path $installed 'Contents/MacOS/MyPowerTools') @('180')
            $unrelated = Start-FixtureProcess '/bin/sh' @('-c', 'while :; do /bin/sleep 1; done', "$installed/Contents/Info.plist")
            try {
                & $installer -SourceApp $source -ApplicationsRoot $apps -DataRoot $data -SkipLaunchAgents
                $owned.Refresh(); $unrelated.Refresh()
                Assert-True $owned.HasExited 'Owned executable must stop'
                Assert-True (-not $unrelated.HasExited) 'Unrelated process must survive'
                Assert-MacOSInstallBundle $installed
            }
            finally { if (-not $unrelated.HasExited) { $unrelated.Kill($true); $unrelated.WaitForExit() } }
        }
        Invoke-Case 'reinstalling from the installed bundle stages before moving the source' {
            & $installer -SourceApp $installed -ApplicationsRoot $apps -DataRoot $data -SkipLaunchAgents
            Assert-MacOSInstallBundle $installed
        }
        Invoke-Case 'native installer observes held installation lock' {
            $path = Join-Path $apps '.mypowertools-install.lock'
            $handle = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                Assert-Throws { & $installer -SourceApp $source -ApplicationsRoot $apps -DataRoot $data -SkipLaunchAgents } 'installation lock'
                Assert-MacOSInstallBundle $installed
            }
            finally { $handle.Dispose() }
        }
        Invoke-Case 'symbolic-link installation destination is rejected' {
            $linkApps = Join-Path $nativeRoot 'Linked Applications'
            New-Item -ItemType Directory -Path $linkApps -Force | Out-Null
            $link = Join-Path $linkApps 'MyPowerTools.app'
            New-Item -ItemType SymbolicLink -Path $link -Target $installed | Out-Null
            Assert-Throws { & $installer -SourceApp $source -ApplicationsRoot $linkApps -DataRoot $data -SkipLaunchAgents } 'symbolic link'
            Assert-MacOSInstallBundle $installed
        }
        Invoke-Case 'staging inside source bundle is rejected before recursive copy' {
            Assert-Throws { & $installer -SourceApp $source -ApplicationsRoot (Join-Path $source 'nested') -DataRoot $data -SkipLaunchAgents } 'inside the source bundle'
            Assert-MacOSInstallBundle $source
        }
    }
}
catch {
    $results.Add([pscustomobject]@{
        name = 'suite setup or infrastructure'; passed = $false; detail = $_.ToString()
    })
}
finally {
    foreach ($process in $processes) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() } } catch { }
        $process.Dispose()
    }
    if ($IsMacOS) {
        $lsregister = '/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
        foreach ($app in $registeredBundles) {
            foreach ($bundle in @($app) + @(Get-ChildItem -LiteralPath (Join-Path $app 'Contents/MacOS/Helpers') -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)) {
                if (Test-Path -LiteralPath $lsregister) {
                    [void](Invoke-MacOSInstallCommand $lsregister @('-u', $bundle) -AllowFailure)
                }
            }
        }
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    $report = [ordered]@{
        schemaVersion = 1
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        nativeTests = [bool]$IsMacOS
        expectedCases = $(if ($IsMacOS) { 19 } else { 11 })
        passed = @($results | Where-Object passed).Count
        failed = @($results | Where-Object { -not $_.passed }).Count
        tests = $results.ToArray()
    }
    $json = $report | ConvertTo-Json -Depth 5
    if ($ReportPath) {
        $fullReportPath = [IO.Path]::GetFullPath($ReportPath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullReportPath) -Force | Out-Null
        [IO.File]::WriteAllText($fullReportPath, $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Host $json
}
if ($report.failed -gt 0) { throw "$($report.failed) macOS installer safety test(s) failed." }

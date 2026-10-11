param([string]$UpdateScriptPath = (Join-Path $PSScriptRoot '../scripts/update-windows-dev.ps1'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($UpdateScriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Dev update script contains parse errors.' }
foreach ($name in @('Get-ProductProcessRecords', 'Assert-NoUnmanagedConflict', 'Request-ShellShutdown', 'Stop-ManagedProcesses')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
$managedProcessRoots = @('C:\Mpt')
$protectedInputRemapExecutable = 'C:\Protected\remap.exe'
$installedShellExecutable = 'C:\Mpt\Shell\MyPowerTools.Shell.Avalonia.exe'
$script:fixtures = @()
function Get-Process {
    param($Name, $Id, $ErrorAction)
    if ($Id -eq $PID) { return [pscustomobject]@{ SessionId = 23 } }
    return @($script:fixtures | Where-Object ProcessName -eq $Name)
}
function Test-IsInsidePath { param($Parent, $Child) return $Child.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase) }
function Test-Path { param($LiteralPath, $PathType) return $true }
function Wait-ForProcessExit { param($Record, $Seconds) }
function Stop-Process { param($Id, [switch]$Force, $ErrorAction) throw "Unexpected forced termination of pid=$Id" }
function Wait-Process { param($Id, $Timeout, $ErrorAction) }
function Request-RunnerShutdown { throw 'Unexpected Runner shutdown request.' }
function New-Fixture {
    param($Id, $SessionId, $Path)
    return [pscustomobject]@{ Id = $Id; SessionId = $SessionId; ProcessName = 'MyPowerTools.Shell.Avalonia'; MainModule = [pscustomobject]@{ FileName = $Path } }
}
$script:started = 0
$script:shutdownWorks = $true
function Start-ProductProcess {
    param($FilePath, $WorkingDirectory, $ArgumentList)
    if ($FilePath -ne $installedShellExecutable -or $ArgumentList[0] -ne '--shutdown-shell') { throw 'Shutdown did not use the trusted installed client.' }
    $script:started++
    if ($script:shutdownWorks) { $script:fixtures = @($script:fixtures | Where-Object SessionId -ne 23) }
    $process = [pscustomobject]@{}
    $process | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $true }
    $process | Add-Member ScriptMethod Dispose {}
    return $process
}

# Unknown current-session peer shuts down through IPC; foreign sessions are untouched.
$script:fixtures = @((New-Fixture 101 23 $null), (New-Fixture 102 0 $null))
Assert-NoUnmanagedConflict -Name @('MyPowerTools.Shell.Avalonia')
if ($script:started -ne 1 -or $script:fixtures.Count -ne 1 -or $script:fixtures[0].Id -ne 102) { throw 'Session isolation or graceful recovery failed.' }

# A known foreign path must never receive a shutdown request.
$script:fixtures = @((New-Fixture 103 23 'C:\Other\Shell.exe'))
$rejected = $false
try { Assert-NoUnmanagedConflict -Name @('MyPowerTools.Shell.Avalonia') } catch { $rejected = $true }
if (-not $rejected -or $script:started -ne 1) { throw 'Known foreign process was accepted or controlled.' }

# Failure to close an unknown peer still blocks replacement and never force-kills it.
$script:fixtures = @((New-Fixture 104 23 $null))
$script:shutdownWorks = $false
$rejected = $false
try { Assert-NoUnmanagedConflict -Name @('MyPowerTools.Shell.Avalonia') } catch { $rejected = $true }
if (-not $rejected -or $script:started -ne 2 -or $script:fixtures.Count -ne 1) { throw 'Failed recovery did not preserve the safety guard.' }
Write-Output 'PASS: IPC recovery, foreign-path rejection, session isolation, failed-recovery guard.'

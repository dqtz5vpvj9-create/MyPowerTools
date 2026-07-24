[CmdletBinding()]
param(
    [string] $PackageRoot = (Join-Path $PSScriptRoot 'artifacts\package'),
    [string] $InstallRoot = "$env:ProgramFiles\MyPowerTools",
    [string] $RunnerPatchRoot = "",
    [string] $ShellPatchRoot = "",
    [switch] $RestartRunner
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this update from an elevated PowerShell session or with Windows sudo.'
}

$source = [System.IO.Path]::GetFullPath($PackageRoot)
$install = [System.IO.Path]::GetFullPath($InstallRoot)
$target = [System.IO.Path]::GetFullPath((Join-Path $install 'modules\paste-image'))
$expectedTarget = [System.IO.Path]::GetFullPath((Join-Path "$env:ProgramFiles\MyPowerTools" 'modules\paste-image'))

if (-not [string]::Equals($target, $expectedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to update unexpected install target '$target'."
}
foreach ($required in @('module.json', 'PasteImage.MyPowerTools.dll', 'ui\tool.json', 'ui\surface\PasteImage.Surface.dll', 'shared\package.hashes.json', 'shared\package.signature.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) {
        throw "Paste Image package is missing '$required'."
    }
}

$runnerPath = Join-Path $install 'Runner\MyPowerTools.Runner.exe'
$runnerProcesses = @(Get-Process -Name 'MyPowerTools.Runner' -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, $runnerPath, [System.StringComparison]::OrdinalIgnoreCase)
})
foreach ($process in $runnerProcesses) {
    Stop-Process -Id $process.Id -Force -ErrorAction Stop
    $process.WaitForExit(10000) | Out-Null
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force
}

if (-not [string]::IsNullOrWhiteSpace($RunnerPatchRoot)) {
    $runnerPatch = [System.IO.Path]::GetFullPath($RunnerPatchRoot)
    $runnerTarget = [System.IO.Path]::GetFullPath((Join-Path $install 'Runner'))
    $expectedRunnerTarget = [System.IO.Path]::GetFullPath((Join-Path "$env:ProgramFiles\MyPowerTools" 'Runner'))
    if (-not [string]::Equals($runnerTarget, $expectedRunnerTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to patch unexpected Runner target '$runnerTarget'."
    }
    foreach ($runnerFile in @(
        'MyPowerTools.Runner.dll',
        'MyPowerTools.Runner.pdb',
        'MyPowerTools.Platform.Windows.dll',
        'MyPowerTools.Platform.Windows.pdb'
    )) {
        $patchFile = Join-Path $runnerPatch $runnerFile
        if (-not (Test-Path -LiteralPath $patchFile -PathType Leaf)) {
            throw "Runner notification patch is missing '$runnerFile'."
        }
        Copy-Item -LiteralPath $patchFile -Destination $runnerTarget -Force
    }
    Write-Output "Runner notification capability updated at $runnerTarget"
}

if (-not [string]::IsNullOrWhiteSpace($ShellPatchRoot)) {
    $shellPatch = [System.IO.Path]::GetFullPath($ShellPatchRoot)
    $shellTarget = [System.IO.Path]::GetFullPath((Join-Path $install 'Shell'))
    $expectedShellTarget = [System.IO.Path]::GetFullPath((Join-Path "$env:ProgramFiles\MyPowerTools" 'Shell'))
    if (-not [string]::Equals($shellTarget, $expectedShellTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to patch unexpected Shell target '$shellTarget'."
    }
    foreach ($shellFile in @(
        'MyPowerTools.Shell.Avalonia.dll',
        'MyPowerTools.Shell.Avalonia.pdb',
        'MyPowerTools.AvaloniaSdk.dll',
        'MyPowerTools.AvaloniaSdk.pdb'
    )) {
        $patchFile = Join-Path $shellPatch $shellFile
        if (-not (Test-Path -LiteralPath $patchFile -PathType Leaf)) {
            throw "Shell realtime-surface patch is missing '$shellFile'."
        }
        Copy-Item -LiteralPath $patchFile -Destination $shellTarget -Force
    }
    Write-Output "Shell realtime-surface capability updated at $shellTarget"
}

if ($RestartRunner -and (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    Start-Process -FilePath $runnerPath -WorkingDirectory $install -WindowStyle Hidden
}

Write-Output "Paste Image updated at $target"

<#
.SYNOPSIS
  Runs the evidence-backed MyPowerTools architecture gates.

.DESCRIPTION
  Quick   = A1 dependency boundaries + A2 dynamic discovery/data autonomy.
  Process = A3 independent ServiceManager lifecycle + A4 fault isolation.
  Release = A5 installer candidate validation, optionally on a remote Windows host.
#>
[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Process', 'Release')]
    [string]$Tier = 'Process',
    [string]$CandidateRoot = '',
    [string]$RemoteHost = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gateProject = Join-Path $repoRoot 'tests\architecture-gate\ArchitectureGate.csproj'

function Invoke-Native {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$Activity)
    Write-Host "==> $Activity" -ForegroundColor Cyan
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "$Activity failed with exit code $LASTEXITCODE." }
}

switch ($Tier) {
    'Quick' {
        Invoke-Native 'dotnet' @('build', $gateProject, '-c', 'Release', '--nologo', '-v', 'minimal') 'Build architecture gate'
        Invoke-Native 'dotnet' @('run', '--no-build', '-c', 'Release', '--project', $gateProject, '--', '--mode', 'a1') 'A1 dependency boundary gate'
        Invoke-Native 'dotnet' @('run', '--no-build', '-c', 'Release', '--project', $gateProject, '--', '--mode', 'a2') 'A2 dynamic discovery gate'
    }
    'Process' {
        Invoke-Native 'dotnet' @('build', $gateProject, '-c', 'Release', '--nologo', '-v', 'minimal') 'Build architecture gate'
        Invoke-Native 'dotnet' @('run', '--no-build', '-c', 'Release', '--project', $gateProject, '--', '--mode', 'a3') 'A3 independent lifecycle gate'
        Invoke-Native 'dotnet' @('run', '--no-build', '-c', 'Release', '--project', $gateProject, '--', '--mode', 'a4') 'A4 fault isolation gate'
    }
    'Release' {
        $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $repoRoot 'scripts\verify-release-candidate.ps1'))
        if (-not [string]::IsNullOrWhiteSpace($CandidateRoot)) { $arguments += @('-CandidateRoot', $CandidateRoot) }
        if (-not [string]::IsNullOrWhiteSpace($RemoteHost)) { $arguments += @('-RemoteHost', $RemoteHost) }
        Invoke-Native 'pwsh.exe' $arguments 'A5 installer candidate gate'
    }
}

Write-Host "Architecture tier '$Tier' passed." -ForegroundColor Green

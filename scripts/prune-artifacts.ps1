#requires -Version 7.0
<#
.SYNOPSIS
Reclaims disk space under artifacts/ according to scripts/artifacts-policy.json.

.DESCRIPTION
This is the single owner of deletion under artifacts/. Retention rules live in the
policy file, not in this script, so changing what is kept never means changing code.

Without -Class it applies only the automatic rules (age and count). Cache entries
are declared 'manual' and are left alone unless you name their class, because
losing them costs a re-download rather than a rebuild.

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -Report
Shows what exists per class and what the current rules would remove.

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -WhatIf
Lists every path automatic retention would delete without touching anything.

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -Class cache
Also reclaims the NuGet and runtime caches, which will be re-downloaded on demand.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('cache', 'build', 'scratch', 'output', 'evidence')]
    [string[]] $Class,

    # Remove paths that no policy entry covers. Review with -Report first.
    [switch] $IncludeUnclassified,

    # Also remove entries marked 'pinned'.
    [switch] $Force,

    # Print the inventory and the pending removals, then stop.
    [switch] $Report,

    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ArtifactsGovernance.psm1') -Force

$policy = Get-ArtifactsPolicy -RepoRoot $RepoRoot

if (-not (Test-Path -LiteralPath $policy.ArtifactsRoot -PathType Container)) {
    Write-Host "Nothing to prune: $($policy.ArtifactsRoot) does not exist."
    return
}

Write-Host "Measuring $($policy.ArtifactsRoot) ..."
$inventory = Get-ArtifactsInventory -Policy $policy -MeasureSize

if ($Report) {
    $usage = Measure-ArtifactsUsage -Policy $policy -Inventory $inventory

    Write-Host ''
    Write-Host 'Current usage by class'
    $usage |
        Select-Object `
            Class,
            ItemCount,
            @{ Name = 'Size'; Expression = { Format-Size -Bytes $_.SizeBytes } },
            @{ Name = 'Budget'; Expression = { if ($_.BudgetBytes -gt 0) { Format-Size -Bytes $_.BudgetBytes } else { '-' } } },
            OverBudget |
        Format-Table -AutoSize |
        Out-String |
        Write-Host

    $largest = $inventory | Sort-Object -Property SizeBytes -Descending | Select-Object -First 15
    Write-Host 'Largest declared paths'
    $largest |
        Select-Object `
            @{ Name = 'Size'; Expression = { Format-Size -Bytes $_.SizeBytes } },
            Class,
            RelativePath |
        Format-Table -AutoSize |
        Out-String |
        Write-Host
}

$candidateParams = @{
    Policy    = $policy
    Inventory = $inventory
}
if ($PSBoundParameters.ContainsKey('Class')) { $candidateParams['Class'] = $Class }
if ($IncludeUnclassified) { $candidateParams['IncludeUnclassified'] = $true }
if ($Force) { $candidateParams['Force'] = $true }

$candidates = Get-ArtifactsPruneCandidate @candidateParams

if ($candidates.Count -eq 0) {
    Write-Host 'Nothing to reclaim under the current retention rules.'
    return
}

$reclaimable = Measure-SumBytes -Item $candidates -Property 'SizeBytes'

Write-Host ''
Write-Host "Removable: $($candidates.Count) path(s), $(Format-Size -Bytes $reclaimable)"
$candidates |
    Sort-Object -Property SizeBytes -Descending |
    Select-Object `
        @{ Name = 'Size'; Expression = { Format-Size -Bytes $_.SizeBytes } },
        Class,
        RelativePath,
        Reason |
    Format-Table -AutoSize |
    Out-String |
    Write-Host

if ($Report) {
    Write-Host 'Report mode: nothing was removed.'
    return
}

$removed = [int64]0
foreach ($candidate in $candidates) {
    Remove-ArtifactsPath -Policy $policy -Path $candidate.FullPath
    if (-not (Test-Path -LiteralPath $candidate.FullPath)) {
        $removed += [Math]::Max([int64]0, $candidate.SizeBytes)
    }
}

if ($WhatIfPreference) {
    Write-Host "Would reclaim $(Format-Size -Bytes $reclaimable)."
}
else {
    Write-Host "Reclaimed $(Format-Size -Bytes $removed)."
}

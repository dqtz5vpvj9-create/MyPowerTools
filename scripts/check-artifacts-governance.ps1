#requires -Version 7.0
<#
.SYNOPSIS
Governance gate for artifacts/. Reports undeclared paths, per-class budget
overruns, and stale scratch directories.

.DESCRIPTION
The undeclared-path check is what keeps this policy alive. Any new producer that
writes somewhere unexpected shows up here, which forces its author to declare a
class and a retention rule in scripts/artifacts-policy.json instead of silently
adding another directory nobody prunes.

The dev loop runs this without -Enforce so it warns but never blocks. CI runs it
with -Enforce so undeclared paths cannot land on main.

Budget measurement walks the whole tree, so the result is cached under
artifacts/.governance-cache.json and refreshed at most once per BudgetCacheHours.

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -File scripts/check-artifacts-governance.ps1 -Enforce
#>
[CmdletBinding()]
param(
    # Exit non-zero when an undeclared path is found. Used by CI: path coverage is
    # a review contract, so it must block, while budgets stay advisory.
    [switch] $Enforce,

    # Also exit non-zero when a class exceeds its budget.
    [switch] $EnforceBudget,

    # Skip the size walk entirely and only validate path coverage.
    [switch] $SkipBudget,

    # Re-measure sizes even when the cached measurement is still fresh.
    [switch] $Refresh,

    [int] $BudgetCacheHours = 24,

    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ArtifactsGovernance.psm1') -Force

$policy = Get-ArtifactsPolicy -RepoRoot $RepoRoot

if (-not (Test-Path -LiteralPath $policy.ArtifactsRoot -PathType Container)) {
    Write-Host 'artifacts/ does not exist yet; nothing to govern.'
    return
}

$cachePath = Join-Path $policy.ArtifactsRoot '.governance-cache.json'
$measureSize = -not $SkipBudget

if ($measureSize -and -not $Refresh -and (Test-Path -LiteralPath $cachePath -PathType Leaf)) {
    $cacheAge = (Get-Date) - (Get-Item -LiteralPath $cachePath -Force).LastWriteTime
    if ($cacheAge.TotalHours -lt $BudgetCacheHours) {
        $measureSize = $false
        $cached = Get-Content -LiteralPath $cachePath -Raw -Encoding utf8 | ConvertFrom-Json
    }
}

$inventory = Get-ArtifactsInventory -Policy $policy -MeasureSize:$measureSize
$coverageViolations = [System.Collections.Generic.List[string]]::new()
$budgetViolations = [System.Collections.Generic.List[string]]::new()

$unclassified = @($inventory | Where-Object { $_.Class -eq 'unclassified' })
if ($unclassified.Count -gt 0) {
    $coverageViolations.Add("$($unclassified.Count) path(s) under artifacts/ are not declared in scripts/artifacts-policy.json")

    Write-Host ''
    Write-Host 'Undeclared paths' -ForegroundColor Yellow
    foreach ($item in ($unclassified | Sort-Object -Property RelativePath)) {
        Write-Host "  artifacts/$($item.RelativePath)"
    }
    Write-Host ''
    Write-Host '  Declare each path in scripts/artifacts-policy.json with a class and a'
    Write-Host '  retention rule, or remove it with:'
    Write-Host '    pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -IncludeUnclassified -WhatIf'
}

$leaked = @(Get-ArtifactsPruneCandidate -Policy $policy -Inventory $inventory |
    Where-Object { $_.Reason -like 'older than *' })
if ($leaked.Count -gt 0) {
    Write-Host ''
    Write-Host "Stale scratch and evidence paths past their retention window: $($leaked.Count)"
    foreach ($item in ($leaked | Sort-Object -Property RelativePath | Select-Object -First 20)) {
        Write-Host "  artifacts/$($item.RelativePath)  [$($item.Reason)]"
    }
    Write-Host '  Reclaim with: pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1'
}

if (-not $SkipBudget) {
    if ($measureSize) {
        $usage = Measure-ArtifactsUsage -Policy $policy -Inventory $inventory
        $snapshot = [pscustomobject]@{
            measuredAt = (Get-Date).ToString('o')
            usage      = @($usage | Select-Object Class, ItemCount, SizeBytes, BudgetBytes, OverBudget)
        }
        $snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $cachePath -Encoding utf8
    }
    else {
        $usage = @($cached.usage)
        Write-Host ''
        Write-Host "Using cached measurement from $($cached.measuredAt) (refresh with -Refresh)."
    }

    Write-Host ''
    Write-Host 'Usage by class'
    foreach ($class in $usage) {
        $budget = if ($class.BudgetBytes -gt 0) { Format-Size -Bytes $class.BudgetBytes } else { '-' }
        $marker = if ($class.OverBudget) { ' OVER BUDGET' } else { '' }
        Write-Host ("  {0,-13} {1,10} / {2,-10} {3} item(s){4}" -f $class.Class, (Format-Size -Bytes $class.SizeBytes), $budget, $class.ItemCount, $marker)
    }

    foreach ($class in $usage) {
        if ($class.OverBudget -and $class.Class -ne 'unclassified') {
            $budgetViolations.Add("class '$($class.Class)' is over its declared budget ($(Format-Size -Bytes $class.SizeBytes) > $(Format-Size -Bytes $class.BudgetBytes))")
        }
    }
}

Write-Host ''
if ($coverageViolations.Count -eq 0 -and $budgetViolations.Count -eq 0) {
    Write-Host 'Artifacts governance: OK' -ForegroundColor Green
    return
}

foreach ($violation in $coverageViolations) {
    if ($Enforce) {
        Write-Host "Artifacts governance violation: $violation" -ForegroundColor Red
    }
    else {
        Write-Warning "Artifacts governance: $violation"
    }
}

foreach ($violation in $budgetViolations) {
    if ($EnforceBudget) {
        Write-Host "Artifacts governance violation: $violation" -ForegroundColor Red
    }
    else {
        Write-Warning "Artifacts governance: $violation"
    }
}

$shouldFail = ($Enforce -and $coverageViolations.Count -gt 0) -or
    ($EnforceBudget -and $budgetViolations.Count -gt 0)

if ($shouldFail) {
    Write-Host ''
    Write-Host 'See docs/ARTIFACTS_GOVERNANCE.md for how to declare a new artifacts path.'
    exit 1
}

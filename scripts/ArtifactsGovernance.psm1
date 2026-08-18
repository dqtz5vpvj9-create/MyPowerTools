#requires -Version 7.0

Set-StrictMode -Version Latest

<#
Shared policy engine for artifacts governance.

scripts/prune-artifacts.ps1 and scripts/check-artifacts-governance.ps1 both build
on this module so that "which class does this path belong to" has exactly one
implementation. Adding a producer means adding an entry to artifacts-policy.json,
never teaching a second script about a new path.
#>

function Resolve-RepoRoot {
    [CmdletBinding()]
    param(
        [string] $RepoRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        return [System.IO.Path]::GetFullPath($RepoRoot)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Get-ArtifactsPolicy {
    [CmdletBinding()]
    param(
        [string] $RepoRoot,
        [string] $PolicyPath
    )

    $root = Resolve-RepoRoot -RepoRoot $RepoRoot
    if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
        $PolicyPath = Join-Path $PSScriptRoot 'artifacts-policy.json'
    }

    if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
        throw "Artifacts policy was not found at $PolicyPath"
    }

    $policy = Get-Content -LiteralPath $PolicyPath -Raw -Encoding utf8 | ConvertFrom-Json

    foreach ($entry in $policy.entries) {
        if ([string]::IsNullOrWhiteSpace($entry.path)) {
            throw "Artifacts policy contains an entry without a path."
        }
        if ($policy.classes.id -notcontains $entry.class) {
            throw "Artifacts policy entry '$($entry.path)' declares unknown class '$($entry.class)'."
        }
    }

    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $root $policy.artifactsRoot))

    return [pscustomobject]@{
        RepoRoot      = $root
        PolicyPath    = [System.IO.Path]::GetFullPath($PolicyPath)
        ArtifactsRoot = $artifactsRoot
        Classes       = $policy.classes
        Entries       = $policy.entries
    }
}

function Split-RelativePath {
    param([string] $RelativePath)

    return $RelativePath.Replace('\', '/').Trim('/').Split('/') | Where-Object { $_ -ne '' }
}

function Test-GlobMatch {
    <#
    Segment-aware glob match. A '*' matches within one path segment only, so
    'tools/*/*' matches 'tools/adb-forwarder/0.2.0' but not 'tools/adb-forwarder'.
    A trailing '/**' matches the declared prefix and everything below it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Glob
    )

    $pathSegments = @(Split-RelativePath -RelativePath $RelativePath)
    $globSegments = @(Split-RelativePath -RelativePath $Glob)

    $matchesRemainder = $false
    if ($globSegments.Count -gt 0 -and $globSegments[-1] -eq '**') {
        $matchesRemainder = $true
        $globSegments = if ($globSegments.Count -eq 1) {
            @()
        }
        else {
            @($globSegments[0..($globSegments.Count - 2)])
        }
    }

    if ($matchesRemainder) {
        if ($pathSegments.Count -lt $globSegments.Count) { return $false }
    }
    elseif ($pathSegments.Count -ne $globSegments.Count) {
        return $false
    }

    for ($index = 0; $index -lt $globSegments.Count; $index++) {
        if ($pathSegments[$index] -notlike $globSegments[$index]) { return $false }
    }

    return $true
}

function Test-GlobAncestor {
    <#
    True when RelativePath is a strict ancestor of the glob, meaning the walk must
    descend into it to reach a declared path. 'release' is an ancestor of
    'release/win-x64', so 'release' itself is not reported as unclassified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Glob
    )

    $pathSegments = @(Split-RelativePath -RelativePath $RelativePath)
    $globSegments = @(Split-RelativePath -RelativePath $Glob)

    if ($globSegments.Count -le $pathSegments.Count) { return $false }

    for ($index = 0; $index -lt $pathSegments.Count; $index++) {
        if ($pathSegments[$index] -notlike $globSegments[$index]) { return $false }
    }

    return $true
}

function Measure-SumBytes {
    <#
    Measure-Object emits nothing for an empty pipeline, so a bare .Sum access
    throws under Set-StrictMode. Every summation here goes through this helper.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()] [object[]] $Item,
        [Parameter(Mandatory)] [string] $Property
    )

    if ($null -eq $Item -or $Item.Count -eq 0) { return [int64]0 }

    $measured = $Item | Measure-Object -Property $Property -Sum
    if ($null -eq $measured -or $null -eq $measured.Sum) { return [int64]0 }
    return [int64]$measured.Sum
}

function Get-PathSize {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return [int64]0 }
    if (-not $item.PSIsContainer) { return [int64]$item.Length }

    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue)
    return Measure-SumBytes -Item $files -Property 'Length'
}

function Get-ArtifactsInventory {
    <#
    Walks the artifacts tree and classifies every path against the policy.
    Descends only where necessary: a directory that matches an entry is reported
    as one item without enumerating its children, which keeps the walk cheap.
    Pass -MeasureSize to attach byte counts (this is the expensive part).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Policy,
        [switch] $MeasureSize
    )

    if (-not (Test-Path -LiteralPath $Policy.ArtifactsRoot -PathType Container)) {
        return @()
    }

    $results = [System.Collections.Generic.List[object]]::new()

    function Add-Item {
        param(
            [System.IO.FileSystemInfo] $Item,
            [string] $RelativePath,
            [object] $Entry
        )

        $size = if ($MeasureSize) { Get-PathSize -Path $Item.FullName } else { [int64](-1) }

        $results.Add([pscustomobject]@{
            RelativePath  = $RelativePath
            FullPath      = $Item.FullName
            IsContainer   = $Item.PSIsContainer
            LastWriteTime = $Item.LastWriteTime
            SizeBytes     = $size
            Entry         = $Entry
            Class         = if ($null -eq $Entry) { 'unclassified' } else { $Entry.class }
        })
    }

    function Walk-Directory {
        param([string] $DirectoryPath, [string] $DirectoryRelativePath)

        $children = Get-ChildItem -LiteralPath $DirectoryPath -Force -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            $relative = if ([string]::IsNullOrEmpty($DirectoryRelativePath)) {
                $child.Name
            }
            else {
                "$DirectoryRelativePath/$($child.Name)"
            }

            $entry = $null
            foreach ($candidate in $Policy.Entries) {
                if (Test-GlobMatch -RelativePath $relative -Glob $candidate.path) {
                    $entry = $candidate
                    break
                }
            }

            if ($null -ne $entry) {
                Add-Item -Item $child -RelativePath $relative -Entry $entry
                continue
            }

            $hasDescendantRule = $false
            foreach ($candidate in $Policy.Entries) {
                if (Test-GlobAncestor -RelativePath $relative -Glob $candidate.path) {
                    $hasDescendantRule = $true
                    break
                }
            }

            if ($hasDescendantRule -and $child.PSIsContainer) {
                Walk-Directory -DirectoryPath $child.FullName -DirectoryRelativePath $relative
                continue
            }

            Add-Item -Item $child -RelativePath $relative -Entry $null
        }
    }

    Walk-Directory -DirectoryPath $Policy.ArtifactsRoot -DirectoryRelativePath ''

    return $results.ToArray()
}

function Measure-ArtifactsUsage {
    <#
    Aggregates a measured inventory into per-class totals and compares them with
    the budgets declared in the policy.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Policy,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Inventory
    )

    $usage = [System.Collections.Generic.List[object]]::new()

    foreach ($class in $Policy.Classes) {
        $items = @($Inventory | Where-Object { $_.Class -eq $class.id })
        $total = Measure-SumBytes -Item $items -Property 'SizeBytes'

        $budgetBytes = [int64]$class.budgetMB * 1MB
        $usage.Add([pscustomobject]@{
            Class       = $class.id
            Summary     = $class.summary
            ItemCount   = $items.Count
            SizeBytes   = [int64]$total
            BudgetBytes = $budgetBytes
            OverBudget  = ($budgetBytes -gt 0 -and $total -gt $budgetBytes)
        })
    }

    $unclassified = @($Inventory | Where-Object { $_.Class -eq 'unclassified' })
    if ($unclassified.Count -gt 0) {
        $total = Measure-SumBytes -Item $unclassified -Property 'SizeBytes'
        $usage.Add([pscustomobject]@{
            Class       = 'unclassified'
            Summary     = 'Paths with no policy entry. Declare them or delete them.'
            ItemCount   = $unclassified.Count
            SizeBytes   = [int64]$total
            BudgetBytes = [int64]0
            OverBudget  = $true
        })
    }

    return $usage.ToArray()
}

function Get-ArtifactsPruneCandidate {
    <#
    Applies each entry's retention rule to the inventory and returns the items
    that may be removed. Callers decide which classes to act on; 'manual' entries
    are only offered when their class is explicitly requested.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Policy,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Inventory,
        [string[]] $Class,
        [switch] $IncludeUnclassified,
        [switch] $Force
    )

    $now = Get-Date
    $candidates = [System.Collections.Generic.List[object]]::new()

    # @($null) yields a one-element array, which would look like a real class filter
    # and silently skip every entry, so drop empty values explicitly.
    $requestedClasses = @()
    if ($null -ne $Class) {
        $requestedClasses = @($Class | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $grouped = $Inventory | Where-Object { $null -ne $_.Entry } | Group-Object -Property { $_.Entry.path }

    foreach ($group in $grouped) {
        $entry = $group.Group[0].Entry
        if ($requestedClasses.Count -gt 0 -and $requestedClasses -notcontains $entry.class) { continue }

        $retention = $entry.retention
        $mode = if ($null -eq $retention) { 'manual' } else { $retention.mode }

        switch ($mode) {
            'pinned' {
                if (-not $Force) { continue }
                foreach ($item in $group.Group) {
                    $candidates.Add((New-Candidate -Item $item -Reason 'pinned entry removed because -Force was supplied'))
                }
            }

            'manual' {
                # Caches are only reclaimed when the caller names the class outright.
                if ($requestedClasses.Count -eq 0) { continue }
                foreach ($item in $group.Group) {
                    $candidates.Add((New-Candidate -Item $item -Reason "class '$($entry.class)' pruned on request"))
                }
            }

            'age' {
                $threshold = $null
                if ($null -ne $retention.PSObject.Properties['maxAgeHours'] -and $null -ne $retention.maxAgeHours) {
                    $threshold = $now.AddHours(-[double]$retention.maxAgeHours)
                    $window = "$($retention.maxAgeHours)h"
                }
                elseif ($null -ne $retention.PSObject.Properties['maxAgeDays'] -and $null -ne $retention.maxAgeDays) {
                    $threshold = $now.AddDays(-[double]$retention.maxAgeDays)
                    $window = "$($retention.maxAgeDays)d"
                }

                if ($null -eq $threshold) {
                    throw "Policy entry '$($entry.path)' uses mode 'age' without maxAgeHours or maxAgeDays."
                }

                foreach ($item in $group.Group) {
                    if ($item.LastWriteTime -lt $threshold) {
                        $candidates.Add((New-Candidate -Item $item -Reason "older than $window"))
                    }
                }
            }

            'count' {
                $keep = if ($null -eq $retention.PSObject.Properties['keep']) { 1 } else { [int]$retention.keep }
                $perParent = ($null -ne $retention.PSObject.Properties['perParent'] -and $retention.perParent)

                $buckets = if ($perParent) {
                    $group.Group | Group-Object -Property { Split-Path -Parent $_.FullPath }
                }
                else {
                    , ([pscustomobject]@{ Group = $group.Group })
                }

                foreach ($bucket in $buckets) {
                    $ordered = @($bucket.Group | Sort-Object -Property LastWriteTime -Descending)
                    if ($ordered.Count -le $keep) { continue }
                    foreach ($item in $ordered[$keep..($ordered.Count - 1)]) {
                        $candidates.Add((New-Candidate -Item $item -Reason "keeping the newest $keep of $($ordered.Count)"))
                    }
                }
            }

            default {
                throw "Policy entry '$($entry.path)' declares unknown retention mode '$mode'."
            }
        }
    }

    if ($IncludeUnclassified) {
        foreach ($item in @($Inventory | Where-Object { $_.Class -eq 'unclassified' })) {
            $candidates.Add((New-Candidate -Item $item -Reason 'no policy entry covers this path'))
        }
    }

    return $candidates.ToArray()
}

function New-Candidate {
    param(
        [Parameter(Mandatory)] [object] $Item,
        [Parameter(Mandatory)] [string] $Reason
    )

    return [pscustomobject]@{
        RelativePath = $Item.RelativePath
        FullPath     = $Item.FullPath
        Class        = $Item.Class
        SizeBytes    = $Item.SizeBytes
        Reason       = $Reason
    }
}

function Remove-ArtifactsPath {
    <#
    Deletes one path after proving it sits strictly inside the artifacts root.
    Every removal in this repo's governance tooling goes through here.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Policy,
        [Parameter(Mandatory)] [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Refusing to remove an empty path.'
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $root = $Policy.ArtifactsRoot.TrimEnd('\', '/')

    if ($resolved.TrimEnd('\', '/') -eq $root) {
        throw 'Refusing to remove the artifacts root itself.'
    }

    if (-not $resolved.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$resolved' because it is outside $root."
    }

    if (-not (Test-Path -LiteralPath $resolved)) { return }

    if ($PSCmdlet.ShouldProcess($resolved, 'Remove')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
    }
}

function Format-Size {
    param([int64] $Bytes)

    if ($Bytes -lt 0) { return 'n/a' }
    if ($Bytes -ge 1GB) { return '{0:N2} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N0} KB' -f ($Bytes / 1KB) }
    return "$Bytes B"
}

Export-ModuleMember -Function @(
    'Get-ArtifactsPolicy'
    'Get-ArtifactsInventory'
    'Measure-ArtifactsUsage'
    'Get-ArtifactsPruneCandidate'
    'Remove-ArtifactsPath'
    'Test-GlobMatch'
    'Test-GlobAncestor'
    'Get-PathSize'
    'Measure-SumBytes'
    'Format-Size'
)

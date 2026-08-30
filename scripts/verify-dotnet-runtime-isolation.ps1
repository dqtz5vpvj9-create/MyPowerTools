[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$repoRootFull = [IO.Path]::GetFullPath($RepoRoot)
$scanRoots = @('src', 'scripts', 'installer') |
    ForEach-Object { Join-Path $repoRootFull $_ } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Container }
$extensions = @('.cs', '.ps1', '.psm1', '.cmd', '.bat', '.iss', '.props', '.targets')
$violations = [Collections.Generic.List[string]]::new()
$migrationScript = [IO.Path]::GetFullPath((Join-Path $repoRootFull 'scripts\runtime-environment.ps1'))
$gateScript = [IO.Path]::GetFullPath($PSCommandPath)

foreach ($root in $scanRoots) {
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        if ($file.FullName.Equals($gateScript, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($extensions -notcontains $file.Extension.ToLowerInvariant()) {
            continue
        }

        $lines = [IO.File]::ReadAllLines($file.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            if ($line -notmatch 'DOTNET_ROOT') {
                continue
            }

            $windowStart = [Math]::Max(0, $index - 2)
            $windowEnd = [Math]::Min($lines.Length - 1, $index + 2)
            $window = ($lines[$windowStart..$windowEnd] -join ' ')
            $persistentApi = $window -match 'EnvironmentVariableTarget\s*\.\s*User' -or
                $window -match 'SetEnvironmentVariable\s*\([^\)]*[''\"]User[''\"]' -or
                $window -match 'HKCU:.*DOTNET_ROOT|HKEY_CURRENT_USER.*DOTNET_ROOT'
            if (-not $persistentApi) {
                continue
            }

            $isApprovedCleanup = $file.FullName.Equals($migrationScript, [StringComparison]::OrdinalIgnoreCase) -and
                $window -match 'SetEnvironmentVariable\s*\(\s*[''\"]DOTNET_ROOT[''\"]\s*,\s*\$null\s*,\s*[''\"]User[''\"]\s*\)'
            if (-not $isApprovedCleanup) {
                $relative = [IO.Path]::GetRelativePath($repoRootFull, $file.FullName)
                $violations.Add("${relative}:$($index + 1): $($line.Trim())")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    throw "Persistent DOTNET_ROOT writes are forbidden:`n$($violations -join [Environment]::NewLine)"
}

Write-Output 'DOTNET_ROOT isolation gate passed.'

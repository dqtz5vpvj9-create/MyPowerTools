[CmdletBinding()]
param(
    [string]$ArtifactsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'),
    [double]$WebSetupLimitMiB = 5,
    [double]$CoreZipLimitMiB = 70,
    [double]$FullZipLimitMiB = 205
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ArtifactsRoot)

function Assert-Size {
    param([string]$Path, [double]$LimitMiB, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $size = (Get-Item -LiteralPath $Path).Length / 1MB
    if ($size -gt $LimitMiB) { throw ("{0} is {1:N2} MiB; limit is {2:N2} MiB." -f $Label, $size, $LimitMiB) }
    Write-Output ("{0}: {1:N2} MiB / {2:N2} MiB" -f $Label, $size, $LimitMiB)
}

$webSetup = Get-ChildItem -LiteralPath $root -Filter 'MyPowerTools-Web-Setup-*-win-x64.exe' -File |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $webSetup) { throw 'Web Setup executable is missing.' }
Assert-Size -Path $webSetup.FullName -LimitMiB $WebSetupLimitMiB -Label 'Web Setup'
Assert-Size -Path (Join-Path $root 'MyPowerTools-core-win-x64.zip') -LimitMiB $CoreZipLimitMiB -Label 'Web core ZIP'
Assert-Size -Path (Join-Path $root 'MyPowerTools-win-x64.zip') -LimitMiB $FullZipLimitMiB -Label 'Full ZIP'

foreach ($layoutName in @('win-x64', 'win-x64-core')) {
    $layout = Join-Path $root $layoutName
    if (-not (Test-Path -LiteralPath $layout -PathType Container)) { throw "Release layout is missing: $layout" }
    $layoutPrefix = [IO.Path]::GetFullPath($layout).TrimEnd('\') + '\'
    $privateRuntimePrefix = [IO.Path]::GetFullPath((Join-Path $layout 'Runtime\dotnet')).TrimEnd('\') + '\'
    $runtimeDuplicates = @(Get-ChildItem -LiteralPath $layout -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @('hostfxr.dll', 'coreclr.dll') -and
            -not $_.FullName.StartsWith($privateRuntimePrefix, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($runtimeDuplicates.Count -gt 0) {
        throw "Duplicate .NET runtime carriers in ${layoutName}: $($runtimeDuplicates.FullName -join ', ')"
    }
    $pdbs = @(Get-ChildItem -LiteralPath $layout -Recurse -Filter '*.pdb' -File)
    if ($pdbs.Count -gt 0) { throw "Debug symbols found in ${layoutName}: $($pdbs.FullName -join ', ')" }
}

$nssm = Get-ChildItem -LiteralPath (Join-Path $root 'win-x64\modules\nssm-manager') -Recurse -Filter 'nssm-manager.exe' -File |
    Sort-Object Length -Descending | Select-Object -First 1
if ($null -eq $nssm) { throw 'Published nssm-manager.exe is missing.' }
if ($nssm.Length -gt 5MB) { throw ("Native AOT nssm-manager.exe is {0:N2} MiB; limit is 5 MiB." -f ($nssm.Length / 1MB)) }
Write-Output ("nssm-manager Native AOT: {0:N2} MiB" -f ($nssm.Length / 1MB))

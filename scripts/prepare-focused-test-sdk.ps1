#requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repo 'artifacts/sdk/nuget'
$cache = Join-Path $repo 'artifacts/sdk/global-packages'

# No custom source fingerprint or skip-build cache: native Build/Pack evaluates
# imports, source/resource inputs, package versions and dependencies every time.
# Separate staging prevents a newer Release package from satisfying a Debug pack
# (or another SDK's pack) through a shared output timestamp.
Push-Location $repo
$outputList = [IO.Path]::GetTempFileName()
try {
    # Never publish a new same-version SDK while consumers use another cache,
    # and never evict a user's global cache. Reject overrides before any writes.
    $cacheListing = @(& dotnet nuget locals global-packages --list --force-english-output)
    if ($LASTEXITCODE -ne 0 -or $cacheListing.Count -ne 1 -or
        $cacheListing[0] -notmatch '^global-packages:\s*(.+)$') {
        throw 'Could not determine the active NuGet global-packages path.'
    }
    $activeCache = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Matches[1].Trim(), $repo))
    $cache = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($cache))
    $pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals($activeCache, $cache, $pathComparison)) {
        throw "Focused tests require the repository NuGet cache '$cache'. Remove the NUGET_PACKAGES/configuration override for '$activeCache'."
    }
    if (-not [string]::IsNullOrWhiteSpace($env:RestorePackagesPath)) {
        $restoreCache = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:RestorePackagesPath, $repo))
        if (-not [string]::Equals($restoreCache, $cache, $pathComparison)) {
            throw "Focused tests require the repository NuGet cache '$cache'. Remove the RestorePackagesPath override for '$restoreCache'."
        }
    }
    $sdkVersion = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^[0-9][0-9A-Za-z.+-]*$') {
        throw 'Could not determine the selected .NET SDK version.'
    }
    if ($Configuration -notmatch '^[0-9A-Za-z._-]+$') { throw 'Invalid build configuration name.' }
    New-Item -ItemType Directory -Force -Path $feed | Out-Null
    $staging = Join-Path $repo "artifacts/build/obj/FocusedTestSdk/$sdkVersion/$Configuration"
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    & dotnet msbuild (Join-Path $PSScriptRoot 'focused-test-sdk.proj') -restore -target:Pack -maxCpuCount `
        "-property:Configuration=$Configuration" "-property:PackageOutputPath=$staging" `
        "-property:FocusedSdkOutputList=$outputList" "-property:RestorePackagesPath=$cache"
    if ($LASTEXITCODE -ne 0) { throw 'Focused-test SDK preparation failed.' }

    $packages = @(Get-Content -LiteralPath $outputList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($packages.Count -eq 0) { throw 'SDK packing returned no package outputs.' }
    $invalidated = 0
    foreach ($package in $packages) {
        if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "SDK package is missing: $package" }
        $archive = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            $nuspecs = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
            if ($nuspecs.Count -ne 1) { throw "SDK package must contain one root nuspec: $package" }
            $reader = [IO.StreamReader]::new($nuspecs[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $id = [string]$nuspec.package.metadata.id
        }
        finally { $archive.Dispose() }
        if ($id -notmatch '^MyPowerTools\.[A-Za-z0-9_.-]+$') { throw "Unexpected SDK package ID: $id" }
        $name = [IO.Path]::GetFileName($package)
        if (-not $name.StartsWith("$id.", [StringComparison]::OrdinalIgnoreCase) -or -not $name.EndsWith('.nupkg')) {
            throw "SDK package filename does not match its ID: $name"
        }
        # Pack's output filename contains NuGet's normalized version, including
        # prerelease normalization, so it also identifies the exact cache folder.
        $version = $name.Substring($id.Length + 1, $name.Length - $id.Length - 7).ToLowerInvariant()
        if ($version -notmatch '^[0-9][0-9A-Za-z.+-]*$') { throw "Invalid SDK package version: $version" }
        $packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        $published = Join-Path $feed $name
        if (-not (Test-Path -LiteralPath $published -PathType Leaf) -or
            (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne $packageHash) {
            Copy-Item -LiteralPath $package -Destination $published -Force
        }
        $versionCache = Join-Path (Join-Path $cache $id.ToLowerInvariant()) $version
        if (Test-Path -LiteralPath $versionCache -PathType Container) {
            $cachedPackage = Join-Path $versionCache $name.ToLowerInvariant()
            if (-not (Test-Path -LiteralPath $cachedPackage -PathType Leaf) -or
                (Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA256).Hash -ne $packageHash) {
                Remove-Item -LiteralPath $versionCache -Recurse -Force
                $invalidated++
            }
        }
    }
    Write-Host "Prepared $($packages.Count) SDK packages; invalidated $invalidated changed package cache entries."
}
finally {
    Remove-Item -LiteralPath $outputList -Force -ErrorAction SilentlyContinue
    Pop-Location
}

# Shared helpers for the embedded OpenList runtime packaging scripts.
#
# Everything here is deliberately dependency-free PowerShell (no Android SDK, no Go) so the
# packaging path works on Windows, macOS and Linux build machines. The only hard requirements
# are an HTTPS connection to the pinned official GitHub release and `tar` (present on Windows 10+
# as bsdtar, and on every macOS/Linux host).
Set-StrictMode -Version Latest

function Get-OpenListRuntimeManifest {
    <#
    .SYNOPSIS
    Loads the pinned runtime manifest that sits next to these scripts.
    #>
    param([Parameter(Mandatory = $true)][string]$RuntimeRoot)

    $path = Join-Path $RuntimeRoot 'openlist-runtime.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "OpenList runtime manifest is missing: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-OpenListReleaseUrl {
    <#
    .SYNOPSIS
    Absolute URL of one pinned official release asset.
    #>
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Asset
    )
    return '{0}/{1}/{2}' -f $Manifest.releaseDownloadBase.TrimEnd('/'), $Manifest.version, $Asset
}

function Get-OpenListAndroidBuildPlan {
    <#
    .SYNOPSIS
    Returns the pinned source-build section of the runtime manifest, failing loudly when a field the
    build depends on is missing. Keeping the values in the manifest is what makes the payload
    reproducible: tag, toolchain, tags, page size and frontend release are all pinned in one place.
    #>
    param([Parameter(Mandatory = $true)]$Manifest)

    if (-not $Manifest.android.PSObject.Properties['build']) {
        throw 'The runtime manifest has no android.build section, so the Android payload cannot be built from source.'
    }
    $build = $Manifest.android.build
    foreach ($field in @('sourceUrl', 'sourceArchiveRoot', 'gitCommit', 'goToolchain', 'goMinimum', 'builtAt', 'gitAuthor')) {
        if (-not $build.PSObject.Properties[$field] -or -not $build.$field) {
            throw "android.build.$field is missing from the runtime manifest."
        }
    }
    foreach ($field in @('tags', 'frontend', 'ndk')) {
        if (-not $build.PSObject.Properties[$field]) {
            throw "android.build.$field is missing from the runtime manifest."
        }
    }
    if (@($build.tags).Count -eq 0) { throw 'android.build.tags must list at least one Go build tag.' }
    foreach ($field in @('downloadBase', 'tag', 'asset')) {
        if (-not $build.frontend.PSObject.Properties[$field] -or -not $build.frontend.$field) {
            throw "android.build.frontend.$field is missing from the runtime manifest."
        }
    }
    foreach ($field in @('release', 'revision', 'apiLevel')) {
        if (-not $build.ndk.PSObject.Properties[$field] -or -not $build.ndk.$field) {
            throw "android.build.ndk.$field is missing from the runtime manifest."
        }
    }
    return $build
}

function Get-OpenListAndroidHostTag {
    <#
    .SYNOPSIS
    NDK prebuilt directory name for the current build host.
    #>
    if ($IsWindows) { return 'windows-x86_64' }
    if ($IsMacOS) { return 'darwin-x86_64' }
    return 'linux-x86_64'
}

function Test-OpenListAndroidNdk {
    <#
    .SYNOPSIS
    Validates one directory as a usable NDK installation and reports its revision, or returns $null
    when the directory carries no LLVM toolchain for this host.
    #>
    param(
        [string]$Root,
        [Parameter(Mandatory = $true)][string]$HostTag,
        [string]$ExpectedRevision = ''
    )

    if (-not $Root) { return $null }
    $bin = Join-Path $Root "toolchains/llvm/prebuilt/$HostTag/bin"
    if (-not (Test-Path -LiteralPath $bin -PathType Container)) { return $null }
    $revision = ''
    $properties = Join-Path $Root 'source.properties'
    if (Test-Path -LiteralPath $properties -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $properties) {
            if ($line -match '^\s*Pkg\.Revision\s*=\s*(\S+)') { $revision = $Matches[1]; break }
        }
    }
    return [pscustomobject]@{
        Root                  = (Resolve-Path -LiteralPath $Root).Path
        Revision              = $revision
        Bin                   = (Resolve-Path -LiteralPath $bin).Path
        MatchesPinnedRevision = ($ExpectedRevision -and $revision -eq $ExpectedRevision)
    }
}

function Resolve-OpenListAndroidNdk {
    <#
    .SYNOPSIS
    Finds the pinned Android NDK used to compile the embedded runtime.

    .DESCRIPTION
    Search order: -NdkRoot, ANDROID_NDK_ROOT / ANDROID_NDK_HOME / ANDROID_NDK, the Android SDK
    ndk/<revision> directories, then the conventional per-host locations. Only the pinned revision is
    accepted implicitly; a different revision is used only when the caller named it through
    -NdkRoot. -Download fetches the pinned official NDK zip into the cache when nothing matches
    (linux-x86_64 hosts only, because that is the archive the manifest pins).
    #>
    param(
        [string]$Requested = '',
        [Parameter(Mandatory = $true)]$Manifest,
        [string]$CacheRoot = '',
        [switch]$Download
    )

    $build = Get-OpenListAndroidBuildPlan -Manifest $Manifest
    $hostTag = Get-OpenListAndroidHostTag
    $pinned = $build.ndk.revision

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($Requested) { $candidates.Add($Requested) }
    foreach ($name in @('ANDROID_NDK_ROOT', 'ANDROID_NDK_HOME', 'ANDROID_NDK')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $candidates.Add($value) }
    }
    foreach ($name in @('ANDROID_HOME', 'ANDROID_SDK_ROOT')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $candidates.Add((Join-Path $value "ndk/$pinned")) }
    }
    $candidates.Add("/android/sdk/ndk/$pinned")
    # Join-Path rejects an empty Path, and HOME/LOCALAPPDATA are absent on some hosts.
    foreach ($homeRoot in @($HOME, $env:USERPROFILE)) {
        if (-not $homeRoot) { continue }
        $candidates.Add((Join-Path $homeRoot "Android/Sdk/ndk/$pinned"))
        $candidates.Add((Join-Path $homeRoot "Library/Android/sdk/ndk/$pinned"))
        $candidates.Add((Join-Path $homeRoot "android-ndk-$($build.ndk.release)"))
    }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA "Android/Sdk/ndk/$pinned")) }

    $mismatch = $null
    foreach ($candidate in $candidates) {
        $probe = Test-OpenListAndroidNdk -Root $candidate -HostTag $hostTag -ExpectedRevision $pinned
        if (-not $probe) { continue }
        if ($probe.MatchesPinnedRevision) { return $probe }
        if (-not $mismatch) { $mismatch = $probe }
    }

    if ($Requested -and $mismatch) {
        Write-Warning "Using NDK $($mismatch.Revision) from -NdkRoot instead of the pinned revision $pinned; the payload is no longer byte-reproducible."
        return $mismatch
    }

    if ($Download) {
        if (-not $CacheRoot) { throw '-CacheRoot is required when -Download is used.' }
        if (-not $IsLinux) {
            throw "Automatic NDK download is only implemented for linux-x86_64 hosts; install NDK $pinned and pass -NdkRoot."
        }
        $url = $build.ndk.downloadUrl
        if (-not $url) { throw 'android.build.ndk.downloadUrl is missing from the runtime manifest.' }
        $zip = Join-Path $CacheRoot "android-ndk-$($build.ndk.release)-linux.zip"
        if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
            Write-Host "  > download NDK $($build.ndk.release) ($url)" -ForegroundColor DarkGray
            Invoke-WebRequest -Uri $url -OutFile "$zip.partial"
            Move-Item -LiteralPath "$zip.partial" -Destination $zip -Force
        }
        $extractRoot = Join-Path $CacheRoot 'ndk'
        if (-not (Test-Path -LiteralPath $extractRoot -PathType Container)) {
            New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
            & tar -xf $zip -C $extractRoot
            if ($LASTEXITCODE -ne 0) { throw "tar failed to extract the NDK archive (exit $LASTEXITCODE)." }
        }
        $downloaded = Join-Path $extractRoot "android-ndk-$($build.ndk.release)"
        $probe = Test-OpenListAndroidNdk -Root $downloaded -HostTag $hostTag -ExpectedRevision $pinned
        if ($probe -and $probe.MatchesPinnedRevision) { return $probe }
        throw "The NDK archive at $url did not contain the pinned revision $pinned."
    }

    $hint = if ($mismatch) { " Found NDK $($mismatch.Revision) at $($mismatch.Root), but the manifest pins $pinned." } else { '' }
    throw ("Android NDK $pinned was not found.$hint Install it (sdkmanager 'ndk;$pinned'), pass -NdkRoot, " +
        "or pass -DownloadNdk to fetch $($build.ndk.downloadUrl).")
}

function Install-OpenListStagedLibrary {
    <#
    .SYNOPSIS
    Replaces one staged runtime library atomically: the payload is copied next to the target and only
    then moved over it, so an interrupted or failing build can never leave a partial library in the
    APK staging layout.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$AndroidAbi,
        [Parameter(Mandatory = $true)][string]$LibraryFileName
    )

    $target = Join-Path $OutputDirectory "lib/$AndroidAbi/$LibraryFileName"
    $directory = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $staged = "$target.staged"
    Copy-Item -LiteralPath $Source -Destination $staged -Force
    if (-not $IsWindows) {
        try {
            [IO.File]::SetUnixFileMode($staged,
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
        }
        catch { }
    }
    Move-Item -LiteralPath $staged -Destination $target -Force
    return $target
}

function Write-OpenListStageManifest {
    <#
    .SYNOPSIS
    Writes openlist-runtime-stage.json next to the staged lib/<abi> tree.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)]$StageManifest
    )

    $path = Join-Path $OutputDirectory 'openlist-runtime-stage.json'
    $StageManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Resolve-OpenListTempRoot {
    <#
    .SYNOPSIS
    Picks a temporary/cache root: the shared /mnt/cache/data-cache location when it is writable,
    otherwise the repository scratch class artifacts/.tmp-* that scripts/artifacts-policy.json
    already governs. Never creates an ungoverned repository path.
    #>
    param(
        [string]$Requested = '',
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Leaf
    )

    if ($Requested) { return [IO.Path]::GetFullPath($Requested) }
    if (-not $IsWindows) {
        $shared = "/mnt/cache/data-cache/mpt-openlist-runtime/$Leaf"
        try {
            New-Item -ItemType Directory -Path $shared -Force -ErrorAction Stop | Out-Null
            # Creating the directory is not enough: a read-only or sandboxed filesystem can accept
            # the directory call and still refuse files, so prove a real write before using it.
            $probe = Join-Path $shared ('.write-probe-' + [Guid]::NewGuid().ToString('N'))
            Set-Content -LiteralPath $probe -Value 'ok' -ErrorAction Stop
            Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
            return $shared
        }
        catch { }
    }
    return Join-Path $RepositoryRoot "artifacts/.tmp-openlist-runtime/$Leaf"
}

function Find-BytePattern {
    <#
    .SYNOPSIS
    Naive byte search used by the streaming scan below. Returns the index or -1.
    #>
    param(
        [Parameter(Mandatory = $true)][byte[]]$Haystack,
        [Parameter(Mandatory = $true)][int]$Count,
        [Parameter(Mandatory = $true)][byte[]]$Needle
    )
    $limit = $Count - $Needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($Haystack[$i] -ne $Needle[0]) { continue }
        $matched = $true
        for ($j = 1; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $matched = $false; break }
        }
        if ($matched) { return $i }
    }
    return -1
}

function Get-OpenListAsciiMatches {
    <#
    .SYNOPSIS
    Streams a file once and returns which of the requested ASCII needles occur in it.

    .DESCRIPTION
    A 150 MB payload is scanned for five markers; decoding each chunk as Latin-1 (a byte-preserving
    mapping) and using the native string search keeps that to one pass instead of one PowerShell
    byte loop per marker, which matters because the verifier reads both ABIs.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Needles,
        [int]$ChunkSize = 8388608
    )

    $pending = [System.Collections.Generic.List[string]]::new()
    foreach ($needle in $Needles) { if ($needle) { $pending.Add($needle) } }
    $found = [System.Collections.Generic.List[string]]::new()
    if ($pending.Count -eq 0) { return $found.ToArray() }

    $longest = 0
    foreach ($needle in $pending) { if ($needle.Length -gt $longest) { $longest = $needle.Length } }
    $overlap = $longest - 1
    $encoding = [Text.Encoding]::GetEncoding(28591)
    $buffer = New-Object byte[] ($ChunkSize + $overlap)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $carry = 0
        while (($read = $stream.Read($buffer, $carry, $ChunkSize)) -gt 0) {
            $total = $carry + $read
            $text = $encoding.GetString($buffer, 0, $total)
            for ($i = $pending.Count - 1; $i -ge 0; $i--) {
                if ($text.IndexOf($pending[$i], [StringComparison]::Ordinal) -ge 0) {
                    $found.Add($pending[$i])
                    $pending.RemoveAt($i)
                }
            }
            if ($pending.Count -eq 0) { break }
            $carry = [Math]::Min($overlap, $total)
            [Array]::Copy($buffer, $total - $carry, $buffer, 0, $carry)
        }
    }
    finally { $stream.Dispose() }
    return $found.ToArray()
}

function Test-FileContainsAscii {
    <#
    .SYNOPSIS
    Streams a file and reports whether an ASCII needle occurs anywhere in it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Needle,
        [int]$ChunkSize = 8388608
    )
    $matches = @(Get-OpenListAsciiMatches -Path $Path -Needles @($Needle) -ChunkSize $ChunkSize)
    return ($matches.Count -gt 0)
}

function Get-OpenListElfLoadAlignment {
    <#
    .SYNOPSIS
    Smallest p_align of the PT_LOAD segments: every load segment must meet the alignment to be
    loaded on a 16 KB memory page Android device. Returns $null when the headers are unreadable.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $header = New-Object byte[] 64
        if ($stream.Read($header, 0, 64) -lt 64) { return $null }
        $programHeaderOffset = [BitConverter]::ToUInt64($header, 0x20)
        $programHeaderSize = [BitConverter]::ToUInt16($header, 0x36)
        $programHeaderCount = [BitConverter]::ToUInt16($header, 0x38)
        if ($programHeaderSize -lt 56 -or $programHeaderCount -eq 0 -or $programHeaderCount -gt 4096) { return $null }

        $table = New-Object byte[] ($programHeaderSize * $programHeaderCount)
        $stream.Position = $programHeaderOffset
        $read = 0
        while ($read -lt $table.Length) {
            $chunk = $stream.Read($table, $read, $table.Length - $read)
            if ($chunk -le 0) { break }
            $read += $chunk
        }

        $minimum = [uint64]::MaxValue
        for ($i = 0; $i -lt $programHeaderCount; $i++) {
            $base = $i * $programHeaderSize
            if ([BitConverter]::ToUInt32($table, $base) -ne 1) { continue } # PT_LOAD
            $align = [BitConverter]::ToUInt64($table, $base + 48)
            if ($align -lt $minimum) { $minimum = $align }
        }
        if ($minimum -eq [uint64]::MaxValue) { return $null }
        return $minimum
    }
    finally { $stream.Dispose() }
}

function Get-OpenListElfFacts {
    <#
    .SYNOPSIS
    Reads the ELF identity of a runtime binary without any platform tooling.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    $header = New-Object byte[] 64
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Read($header, 0, 64) -lt 64) { throw "Not an ELF file (too short): $Path" }
    }
    finally { $stream.Dispose() }

    if ($header[0] -ne 0x7F -or $header[1] -ne 0x45 -or $header[2] -ne 0x4C -or $header[3] -ne 0x46) {
        throw "Not an ELF file (bad magic): $Path"
    }
    $class = if ($header[4] -eq 2) { '64-bit' } else { '32-bit' }
    $endian = if ($header[5] -eq 1) { 'little' } else { 'big' }
    $type = [BitConverter]::ToUInt16($header, 16)
    $machine = [BitConverter]::ToUInt16($header, 18)
    $machineName = switch ($machine) {
        183 { 'aarch64' }
        62 { 'x86-64' }
        40 { 'arm' }
        3 { 'x86' }
        default { "unknown-$machine" }
    }
    $loadAlignment = Get-OpenListElfLoadAlignment -Path $Path
    return [pscustomobject]@{
        Path              = $Path
        Class             = $class
        Endian            = $endian
        Type              = switch ($type) { 2 { 'executable' } 3 { 'pie' } default { "type-$type" } }
        Machine           = $machineName
        MachineCode       = $machine
        MinLoadAlignment  = if ($null -eq $loadAlignment) { $null } else { '0x{0:x}' -f $loadAlignment }
        Supports16KbPages = if ($null -eq $loadAlignment) { $null } else { $loadAlignment -ge 0x4000 }
        Size              = (Get-Item -LiteralPath $Path).Length
    }
}

function Test-OpenListAndroidElf {
    <#
    .SYNOPSIS
    Fails an official Android runtime that is not an Android (Bionic) executable for the expected
    ABI. The interpreter checks are the guard against shipping a Linux ELF that Android can never
    execute. The 16 KB page size fact is a warning by default (legacy payloads may still be 4 KB)
    and becomes a failure when -Require16KbPages is set, which is what the source build enforces.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedMachine,
        [switch]$Require16KbPages
    )

    $facts = Get-OpenListElfFacts -Path $Path
    $problems = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    if ($facts.Class -ne '64-bit') { $problems.Add("expected a 64-bit ELF, found $($facts.Class)") }
    if ($facts.Endian -ne 'little') { $problems.Add("expected a little-endian ELF, found $($facts.Endian)") }
    if ($facts.Type -notin @('pie', 'executable')) { $problems.Add("expected a PIE/executable ELF, found $($facts.Type)") }
    if ($facts.Machine -ne $ExpectedMachine) { $problems.Add("expected ABI $ExpectedMachine, found $($facts.Machine)") }

    # One pass over the payload for every marker this validation cares about.
    $loaderNeedle = '/system/bin/linker64'
    $linuxLoaders = @('/lib64/ld-linux', '/lib/ld-linux', '/lib/ld-musl')
    $rawLstatModule = 'modernc.org/libc'
    $markers = @(@($loaderNeedle) + $linuxLoaders + @($rawLstatModule))
    $matches = @(Get-OpenListAsciiMatches -Path $Path -Needles $markers)
    if ($matches -notcontains $loaderNeedle) {
        $problems.Add('missing the Android dynamic loader /system/bin/linker64')
    }
    foreach ($glibc in $linuxLoaders) {
        if ($matches -contains $glibc) {
            $problems.Add("carries the Linux loader $glibc; this is not an Android build")
        }
    }
    # The pure-Go SQLite driver's libc shim issues raw stat-family syscalls (lstat is syscall 6 on
    # x86_64) that the Android app seccomp filter rejects, so a payload that still links it can never
    # finish admin initialization under a real app UID. The module path survives -w -s and
    # llvm-strip because Go keeps function names in pclntab for tracebacks.
    if ($matches -contains $rawLstatModule) {
        $problems.Add('links the pure-Go modernc.org/libc SQLite shim, whose raw lstat syscall the Android app seccomp filter rejects')
    }
    if ($facts.Supports16KbPages -eq $false) {
        $message = "PT_LOAD alignment $($facts.MinLoadAlignment): this payload is 4 KB aligned and is not compatible with 16 KB memory page Android devices."
        if ($Require16KbPages) { $problems.Add($message) } else { $warnings.Add($message) }
    }
    return [pscustomobject]@{ Facts = $facts; Problems = $problems; Warnings = $warnings }
}

function Get-OpenListAndroidTool {
    <#
    .SYNOPSIS
    Locates one Android build-tool executable (aapt2): PATH first, then the newest build-tools
    revision under ANDROID_HOME / ANDROID_SDK_ROOT, then an explicit directory.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$BuildToolsDirectory = ''
    )

    # Get-Command can return several matches (e.g. two copies on PATH); take the first.
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    $candidates = @()
    if ($BuildToolsDirectory) { $candidates += $BuildToolsDirectory }
    foreach ($root in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT) | Where-Object { $_ }) {
        $buildTools = Join-Path $root 'build-tools'
        if (Test-Path -LiteralPath $buildTools -PathType Container) {
            $candidates += (Get-ChildItem -LiteralPath $buildTools -Directory |
                Sort-Object { try { [version]$_.Name } catch { [version]'0.0.0' } } -Descending |
                ForEach-Object { $_.FullName })
        }
    }
    foreach ($candidate in $candidates) {
        $tool = Join-Path $candidate $Name
        if (Test-Path -LiteralPath $tool -PathType Leaf) { return $tool }
        $toolExe = "$tool.exe"
        if (Test-Path -LiteralPath $toolExe -PathType Leaf) { return $toolExe }
    }
    return $null
}

function Get-OpenListApkExtractNativeLibs {
    <#
    .SYNOPSIS
    Reads android:extractNativeLibs from the APK manifest with aapt2 when it is installed.
    Returns $null when no aapt2 is available; the caller must then report the value as unverified.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Apk,
        [string]$BuildToolsDirectory = ''
    )

    $aapt2 = Get-OpenListAndroidTool -Name 'aapt2' -BuildToolsDirectory $BuildToolsDirectory
    if (-not $aapt2) { return $null }
    $dump = & $aapt2 dump xmltree --file AndroidManifest.xml $Apk 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    foreach ($line in $dump) {
        # aapt2 prints e.g. "A: ...:extractNativeLibs(0x010104ea)=true" (and the typed form
        # "=(type 0x12)0x1" with --values), so both spellings are accepted.
        if ($line -match 'extractNativeLibs.*?=\s*(?:\(type 0x12\))?(true|false)\b') {
            return ($Matches[1] -eq 'true')
        }
        if ($line -match 'extractNativeLibs.*?=\s*(?:\(type 0x12\))?0x([0-9a-fA-F]+)\b') {
            return ([Convert]::ToInt64($Matches[1], 16) -ne 0)
        }
    }
    return $null
}

Export-ModuleMember -Function Get-OpenListRuntimeManifest, Get-OpenListReleaseUrl, Resolve-OpenListTempRoot,
    Find-BytePattern, Get-OpenListAsciiMatches, Test-FileContainsAscii, Get-OpenListElfLoadAlignment,
    Get-OpenListElfFacts, Test-OpenListAndroidElf, Get-OpenListAndroidTool, Get-OpenListApkExtractNativeLibs,
    Get-OpenListAndroidBuildPlan, Get-OpenListAndroidHostTag, Test-OpenListAndroidNdk,
    Resolve-OpenListAndroidNdk, Install-OpenListStagedLibrary, Write-OpenListStageManifest

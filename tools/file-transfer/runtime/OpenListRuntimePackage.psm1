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

function Test-FileContainsAscii {
    <#
    .SYNOPSIS
    Streams a file and reports whether an ASCII needle occurs anywhere in it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Needle,
        [int]$ChunkSize = 1048576
    )
    $needleBytes = [Text.Encoding]::ASCII.GetBytes($Needle)
    $overlap = $needleBytes.Length - 1
    $buffer = New-Object byte[] ($ChunkSize + $overlap)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $carry = 0
        while (($read = $stream.Read($buffer, $carry, $ChunkSize)) -gt 0) {
            $total = $carry + $read
            if ((Find-BytePattern -Haystack $buffer -Count $total -Needle $needleBytes) -ge 0) { return $true }
            $carry = [Math]::Min($overlap, $total)
            [Array]::Copy($buffer, $total - $carry, $buffer, 0, $carry)
        }
        return $false
    }
    finally { $stream.Dispose() }
}

function Get-OpenListElfLoadAlignment {
    <#
    .SYNOPSIS
    Largest p_align of the PT_LOAD segments: the value that decides whether the binary can be
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

        $max = [uint64]0
        for ($i = 0; $i -lt $programHeaderCount; $i++) {
            $base = $i * $programHeaderSize
            if ([BitConverter]::ToUInt32($table, $base) -ne 1) { continue } # PT_LOAD
            $align = [BitConverter]::ToUInt64($table, $base + 48)
            if ($align -gt $max) { $max = $align }
        }
        return $max
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
        MaxLoadAlignment  = if ($null -eq $loadAlignment) { $null } else { '0x{0:x}' -f $loadAlignment }
        Supports16KbPages = if ($null -eq $loadAlignment) { $null } else { $loadAlignment -ge 0x4000 }
        Size              = (Get-Item -LiteralPath $Path).Length
    }
}

function Test-OpenListAndroidElf {
    <#
    .SYNOPSIS
    Fails an official Android runtime that is not an Android (Bionic) executable for the expected
    ABI. The interpreter checks are the guard against shipping a Linux ELF that Android can never
    execute. The 16 KB page size fact is reported as a warning, never as a failure.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedMachine
    )

    $facts = Get-OpenListElfFacts -Path $Path
    $problems = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    if ($facts.Class -ne '64-bit') { $problems.Add("expected a 64-bit ELF, found $($facts.Class)") }
    if ($facts.Endian -ne 'little') { $problems.Add("expected a little-endian ELF, found $($facts.Endian)") }
    if ($facts.Type -notin @('pie', 'executable')) { $problems.Add("expected a PIE/executable ELF, found $($facts.Type)") }
    if ($facts.Machine -ne $ExpectedMachine) { $problems.Add("expected ABI $ExpectedMachine, found $($facts.Machine)") }
    if (-not (Test-FileContainsAscii -Path $Path -Needle '/system/bin/linker64')) {
        $problems.Add('missing the Android dynamic loader /system/bin/linker64')
    }
    foreach ($glibc in @('/lib64/ld-linux', '/lib/ld-linux', '/lib/ld-musl')) {
        if (Test-FileContainsAscii -Path $Path -Needle $glibc) {
            $problems.Add("carries the Linux loader $glibc; this is not an Android build")
        }
    }
    if ($facts.Supports16KbPages -eq $false) {
        $warnings.Add("PT_LOAD alignment $($facts.MaxLoadAlignment): this official binary is 4 KB aligned and is not compatible with 16 KB memory page Android devices.")
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
    Find-BytePattern, Test-FileContainsAscii, Get-OpenListElfLoadAlignment, Get-OpenListElfFacts,
    Test-OpenListAndroidElf, Get-OpenListAndroidTool, Get-OpenListApkExtractNativeLibs

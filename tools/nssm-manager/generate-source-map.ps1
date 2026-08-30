[CmdletBinding()]
param(
    [switch]$VerifyComplete
)

$ErrorActionPreference = 'Stop'
$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$sourceRoot = Join-Path $toolRoot 'original-source\src'
$managedRoot = Join-Path $toolRoot 'sdk-tool\src'
$testRoot = Join-Path $toolRoot 'sdk-tool\tests'
$outputPath = Join-Path $toolRoot 'source-map.json'

function Get-UpstreamDefinitions {
    $definitions = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cpp' -File | Sort-Object Name) {
        $lines = Get-Content -LiteralPath $file.FullName
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $trimmed = $lines[$index].Trim()
            if ($trimmed -notmatch '\)\s*\{\s*$') { continue }
            $open = $trimmed.IndexOf('(')
            if ($open -lt 1) { continue }
            $before = $trimmed.Substring(0, $open)
            if ($before -notmatch '(?<symbol>[A-Za-z_][A-Za-z0-9_]*)\s*$') { continue }
            $symbol = $Matches.symbol
            if ($symbol -in @('if', 'for', 'while', 'switch', 'catch')) { continue }
            $signature = $trimmed.Substring(0, $trimmed.LastIndexOf('{')).Trim()
            $definitions.Add([pscustomobject][ordered]@{
                source = "src/$($file.Name)"
                line = $index + 1
                symbol = $symbol
                signature = $signature
            })
        }
    }
    return $definitions
}

function Get-TestIds {
    $ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $testRoot -Filter '*.cs' -File -Recurse) {
        $type = $null
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            if ($line -match '\b(?:class|record|struct)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)') {
                $type = $Matches.type
            }
            if ($type -and $line -match '\b(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?(?:void|Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?)\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(') {
                [void]$ids.Add("$type.$($Matches.method)")
            }
        }
    }
    return ,$ids
}

function Get-ManagedMappings {
    $attributePattern = '^\s*\[NssmUpstreamFunction\("(?<source>[^"]+)",\s*(?<line>\d+),\s*"(?<signature>[^"]+)",\s*"(?<verification>[^"]+)"(?<tail>[^\]]*)\)\]\s*$'
    $mappings = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $managedRoot -Filter '*.cs' -File -Recurse | Sort-Object FullName) {
        $relative = [System.IO.Path]::GetRelativePath($toolRoot, $file.FullName).Replace('\', '/')
        $lines = Get-Content -LiteralPath $file.FullName
        $namespace = ''
        $type = ''
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            if ($line -match '^\s*namespace\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*;') {
                $namespace = $Matches.namespace
            }
            if ($line -match '\b(?:class|record|struct)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)') {
                $type = $Matches.type
            }
            if ($line -notmatch $attributePattern) { continue }

            $source = $Matches.source
            $sourceLine = [int]$Matches.line
            $signature = $Matches.signature
            $verification = $Matches.verification
            $frontendRewrite = $Matches.tail -match 'FrontendRewrite\s*=\s*true'
            $method = $null
            $managedLine = 0
            for ($candidate = $index + 1; $candidate -lt [Math]::Min($lines.Count, $index + 12); $candidate++) {
                if ($lines[$candidate] -match '\b(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?[A-Za-z_][A-Za-z0-9_<>,.?\[\]\s]*\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(') {
                    $method = $Matches.method
                    $managedLine = $candidate + 1
                    break
                }
            }
            $mappings.Add([pscustomobject][ordered]@{
                source = $source
                line = $sourceLine
                signature = $signature
                csharpFile = $relative
                csharpLine = $managedLine
                csharpType = if ($namespace -and $type) { "$namespace.$type" } else { $type }
                csharpMethod = $method
                verification = $verification
                frontendRewrite = $frontendRewrite
            })
        }
    }
    return $mappings
}

$definitions = @(Get-UpstreamDefinitions)
$managedMappings = @(Get-ManagedMappings)
$testIds = Get-TestIds
$definitionByKey = @{}
foreach ($definition in $definitions) {
    $key = "$($definition.source):$($definition.line)"
    $definitionByKey[$key] = $definition
}

$mappingGroups = @{}
foreach ($mapping in $managedMappings) {
    $key = "$($mapping.source):$($mapping.line)"
    if (-not $mappingGroups.ContainsKey($key)) {
        $mappingGroups[$key] = [System.Collections.Generic.List[object]]::new()
    }
    $mappingGroups[$key].Add($mapping)
}

$records = [System.Collections.Generic.List[object]]::new()
foreach ($definition in $definitions) {
    $key = "$($definition.source):$($definition.line)"
    $candidates = if ($mappingGroups.ContainsKey($key)) { @($mappingGroups[$key]) } else { @() }
    $status = 'missing'
    $diagnostics = [System.Collections.Generic.List[string]]::new()
    $mapping = $null
    if ($candidates.Count -gt 1) {
        $status = 'invalid'
        $diagnostics.Add("duplicate managed mappings: $($candidates.Count)")
    }
    elseif ($candidates.Count -eq 1) {
        $mapping = $candidates[0]
        if ($mapping.signature -ne $definition.signature) {
            $status = 'invalid'
            $diagnostics.Add('upstream signature mismatch')
        }
        elseif ([string]::IsNullOrWhiteSpace($mapping.csharpMethod)) {
            $status = 'invalid'
            $diagnostics.Add('managed method declaration was not found after the attribute')
        }
        elseif (-not $testIds.Contains($mapping.verification)) {
            $status = 'invalid'
            $diagnostics.Add("verification test '$($mapping.verification)' does not exist")
        }
        elseif ($mapping.frontendRewrite -and $definition.source -ne 'src/gui.cpp') {
            $status = 'invalid'
            $diagnostics.Add('FrontendRewrite is allowed only for gui.cpp')
        }
        elseif (-not $mapping.frontendRewrite -and $definition.source -eq 'src/gui.cpp') {
            $status = 'invalid'
            $diagnostics.Add('gui.cpp mappings must be marked FrontendRewrite')
        }
        else {
            $status = if ($mapping.frontendRewrite) { 'frontend-rewrite' } else { 'translated' }
        }
    }

    $records.Add([ordered]@{
        source = $definition.source
        line = $definition.line
        symbol = $definition.symbol
        signature = $definition.signature
        status = $status
        csharpFile = $mapping.csharpFile
        csharpLine = $mapping.csharpLine
        csharpType = $mapping.csharpType
        csharpMethod = $mapping.csharpMethod
        verification = $mapping.verification
        diagnostics = @($diagnostics)
    })
}

$orphaned = [System.Collections.Generic.List[object]]::new()
foreach ($mapping in $managedMappings) {
    $key = "$($mapping.source):$($mapping.line)"
    if (-not $definitionByKey.ContainsKey($key)) {
        $orphaned.Add([ordered]@{
            source = $mapping.source
            line = $mapping.line
            signature = $mapping.signature
            csharpFile = $mapping.csharpFile
            csharpType = $mapping.csharpType
            csharpMethod = $mapping.csharpMethod
            diagnostic = 'no upstream function definition exists at this source location'
        })
    }
}

$translated = @($records | Where-Object status -eq 'translated').Count
$frontend = @($records | Where-Object status -eq 'frontend-rewrite').Count
$missing = @($records | Where-Object status -eq 'missing').Count
$invalid = @($records | Where-Object status -eq 'invalid').Count
$upstreamCommands = @('version', 'start', 'stop', 'restart', 'pause', 'continue', 'status', 'statuscode', 'rotate', 'install', 'edit', 'get', 'set', 'reset', 'unset', 'dump', 'list', 'processes', 'remove')
$managedProgramPath = Join-Path $managedRoot 'NssmManager.Executable\Program.cs'
$managedProgramLines = Get-Content -LiteralPath $managedProgramPath
$upstreamMainLines = Get-Content -LiteralPath (Join-Path $sourceRoot 'nssm.cpp')
$commandRecords = foreach ($command in $upstreamCommands) {
    $sourceLine = 0
    for ($index = 0; $index -lt $upstreamMainLines.Count; $index++) {
        if ($upstreamMainLines[$index].Contains('"' + $command + '"', [System.StringComparison]::OrdinalIgnoreCase)) { $sourceLine = $index + 1; break }
    }
    $managedLine = 0
    for ($index = 0; $index -lt $managedProgramLines.Count; $index++) {
        if ($managedProgramLines[$index] -match ('case\s+"' + [regex]::Escape($command) + '"')) { $managedLine = $index + 1; break }
    }
    [ordered]@{
        name = $command
        source = 'src/nssm.cpp'
        line = $sourceLine
        csharpFile = 'sdk-tool/src/NssmManager.Executable/Program.cs'
        csharpLine = $managedLine
        status = if ($sourceLine -gt 0 -and $managedLine -gt 0) { 'translated' } else { 'missing' }
    }
}
$extraCommands = @('migrate', 'rollback') | ForEach-Object {
    $command = $_
    $managedLine = 0
    for ($index = 0; $index -lt $managedProgramLines.Count; $index++) {
        if ($managedProgramLines[$index] -match ('case\s+"' + [regex]::Escape($command) + '"')) { $managedLine = $index + 1; break }
    }
    [ordered]@{ name = $command; csharpFile = 'sdk-tool/src/NssmManager.Executable/Program.cs'; csharpLine = $managedLine; status = if ($managedLine -gt 0) { 'extension' } else { 'missing' } }
}

$settingNames = @(
    'Application', 'AppParameters', 'AppDirectory', 'AppExit', 'AppEvents', 'AppAffinity', 'AppEnvironment', 'AppEnvironmentExtra', 'AppNoConsole', 'AppPriority',
    'AppRestartDelay', 'AppStdin', 'AppStdinShareMode', 'AppStdinCreationDisposition', 'AppStdinFlagsAndAttributes', 'AppStdout', 'AppStdoutShareMode',
    'AppStdoutCreationDisposition', 'AppStdoutFlagsAndAttributes', 'AppStdoutCopyAndTruncate', 'AppStderr', 'AppStderrShareMode', 'AppStderrCreationDisposition',
    'AppStderrFlagsAndAttributes', 'AppStderrCopyAndTruncate', 'AppStopMethodSkip', 'AppStopMethodConsole', 'AppStopMethodWindow', 'AppStopMethodThreads',
    'AppKillProcessTree', 'AppThrottle', 'AppRedirectHook', 'AppRotateFiles', 'AppRotateOnline', 'AppRotateSeconds', 'AppRotateBytes', 'AppRotateBytesHigh',
    'AppRotateDelay', 'AppTimestampLog', 'DependOnGroup', 'DependOnService', 'Description', 'DisplayName', 'Environment', 'ImagePath', 'ObjectName', 'Name', 'Start', 'Type'
)
$managedSettingsPath = Join-Path $managedRoot 'NssmManager.Windows\NssmSettingsTranslation.cs'
$managedSettingsLines = Get-Content -LiteralPath $managedSettingsPath
$settingRecords = for ($settingIndex = 0; $settingIndex -lt $settingNames.Count; $settingIndex++) {
    $name = $settingNames[$settingIndex]
    $managedLine = 0
    $expectedFactory = if ($settingIndex -ge 39) { 'Native' } else { 'Registry' }
    for ($index = 0; $index -lt $managedSettingsLines.Count; $index++) {
        if ($managedSettingsLines[$index] -match ('^\s*' + $expectedFactory + '\("' + [regex]::Escape($name) + '"')) { $managedLine = $index + 1; break }
    }
    [ordered]@{
        name = $name
        source = 'src/settings.cpp'
        line = 1400 + $settingIndex
        native = ($settingIndex -ge 39)
        csharpFile = 'sdk-tool/src/NssmManager.Windows/NssmSettingsTranslation.cs'
        csharpLine = $managedLine
        status = if ($managedLine -gt 0) { 'translated' } else { 'missing' }
    }
}
$missingCommands = @($commandRecords | Where-Object status -ne 'translated').Count
$missingSettings = @($settingRecords | Where-Object status -ne 'translated').Count
$isComplete = ($missing -eq 0 -and $invalid -eq 0 -and $orphaned.Count -eq 0 -and $missingCommands -eq 0 -and $missingSettings -eq 0)
$document = [ordered]@{
    schemaVersion = 2
    upstream = [ordered]@{
        version = '2.24-101-g897c7ad'
        archiveSha256 = '99F5045FFFBFFB745D67FE3A065A953C4A3D9C253B868892D9B685B0EE7D07B8'
        functionDefinitions = $definitions.Count
    }
    summary = [ordered]@{
        translated = $translated
        frontendRewrite = $frontend
        missing = $missing
        invalid = $invalid
        orphaned = $orphaned.Count
        commands = $commandRecords.Count
        missingCommands = $missingCommands
        settings = $settingRecords.Count
        missingSettings = $missingSettings
        complete = $isComplete
    }
    mappings = $records
    commands = @($commandRecords)
    extensionCommands = @($extraCommands)
    settings = @($settingRecords)
    orphanedMappings = $orphaned
}
$document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding UTF8

Write-Output "NSSM source map: $translated translated, $frontend frontend rewrites, $missing missing, $invalid invalid, $($orphaned.Count) orphaned."
if ($VerifyComplete -and -not $isComplete) {
    throw 'NSSM one-to-one translation map is incomplete.'
}

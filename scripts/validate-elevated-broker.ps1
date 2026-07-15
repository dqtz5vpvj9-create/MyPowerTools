param(
    [Parameter(Mandatory = $true)]
    [string]$BrokerDirectory
)

$ErrorActionPreference = 'Stop'
$brokerRoot = [IO.Path]::GetFullPath($BrokerDirectory)
$brokerExe = Join-Path $brokerRoot 'MyPowerTools.ElevatedBroker.exe'
if (-not (Test-Path -LiteralPath $brokerExe -PathType Leaf)) {
    throw "NativeAOT Broker executable is missing: $brokerExe"
}

$payloadFiles = @(Get-ChildItem -LiteralPath $brokerRoot -File -Force)
if ($payloadFiles.Count -ne 1 -or $payloadFiles[0].Name -ne 'MyPowerTools.ElevatedBroker.exe') {
    $names = $payloadFiles.Name -join ', '
    throw "Elevated Broker must publish as one native EXE. Found: $names"
}

$bytes = [IO.File]::ReadAllBytes($brokerExe)
if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
    throw 'Elevated Broker is not a valid PE image.'
}
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
$optionalHeader = $peOffset + 24
$magic = [BitConverter]::ToUInt16($bytes, $optionalHeader)
$dataDirectory = if ($magic -eq 0x20b) { $optionalHeader + 0x70 } elseif ($magic -eq 0x10b) { $optionalHeader + 0x60 } else { throw 'Elevated Broker has an unknown PE optional header.' }
$clrRva = [BitConverter]::ToUInt32($bytes, $dataDirectory + (14 * 8))
$clrSize = [BitConverter]::ToUInt32($bytes, $dataDirectory + (14 * 8) + 4)
if ($clrRva -ne 0 -or $clrSize -ne 0) {
    throw "Elevated Broker contains a CLR header (RVA=$clrRva size=$clrSize)."
}

$ascii = [Text.Encoding]::ASCII.GetString($bytes)
if ($ascii.Contains('coreclr.dll', [StringComparison]::OrdinalIgnoreCase) -or
    $ascii.Contains('hostfxr.dll', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Elevated Broker still imports or embeds a CLR host dependency.'
}

$environmentNames = @(
    'DOTNET_STARTUP_HOOKS',
    'CORECLR_ENABLE_PROFILING',
    'CORECLR_PROFILER',
    'CORECLR_PROFILER_PATH'
)
$previous = @{}
foreach ($name in $environmentNames) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', 'C:\definitely-missing\mpt-startup-hook.dll', 'Process')
    [Environment]::SetEnvironmentVariable('CORECLR_ENABLE_PROFILING', '1', 'Process')
    [Environment]::SetEnvironmentVariable('CORECLR_PROFILER', '{11111111-1111-1111-1111-111111111111}', 'Process')
    [Environment]::SetEnvironmentVariable('CORECLR_PROFILER_PATH', 'C:\definitely-missing\mpt-profiler.dll', 'Process')
    $process = Start-Process -FilePath $brokerExe -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 2) {
        throw "NativeAOT Broker returned $($process.ExitCode) under hostile CLR environment; expected argument-validation exit 2."
    }
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}

Write-Host "NativeAOT Broker passed: single EXE, no CLR header, CLR startup/profiler variables inert."

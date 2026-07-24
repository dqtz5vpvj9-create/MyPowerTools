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

$utf8Image = [Text.Encoding]::UTF8.GetString($bytes)
$utf16Image = [Text.Encoding]::Unicode.GetString($bytes)
$hasRequireAdministrator =
    $utf8Image.Contains('requireAdministrator', [StringComparison]::Ordinal) -or
    $utf16Image.Contains('requireAdministrator', [StringComparison]::Ordinal)
if (-not $hasRequireAdministrator) {
    throw 'Elevated Broker does not embed a requireAdministrator application manifest.'
}

Write-Host "NativeAOT Broker passed: single EXE, no CLR header, requireAdministrator manifest embedded."

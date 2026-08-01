param(
    [Parameter(Mandatory = $true)]
    [string]$BrokerDirectory
)

$ErrorActionPreference = 'Stop'
$brokerRoot = [IO.Path]::GetFullPath($BrokerDirectory)
$brokerExe = Join-Path $brokerRoot 'MyPowerTools.ElevatedBroker.exe'
if (-not (Test-Path -LiteralPath $brokerExe -PathType Leaf)) {
    throw "Broker executable is missing: $brokerExe"
}

$payloadFiles = @(Get-ChildItem -LiteralPath $brokerRoot -File -Force)
if ($payloadFiles.Count -ne 1 -or $payloadFiles[0].Name -ne 'MyPowerTools.ElevatedBroker.exe') {
    $names = $payloadFiles.Name -join ', '
    throw "Elevated Broker must publish as one self-contained EXE. Found: $names"
}

$bytes = [IO.File]::ReadAllBytes($brokerExe)
if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
    throw 'Elevated Broker is not a valid PE image.'
}
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
$optionalHeader = $peOffset + 24
$magic = [BitConverter]::ToUInt16($bytes, $optionalHeader)
$dataDirectory = if ($magic -eq 0x20b) { $optionalHeader + 0x70 } elseif ($magic -eq 0x10b) { $optionalHeader + 0x60 } else { throw 'Elevated Broker has an unknown PE optional header.' }
$utf8Image = [Text.Encoding]::UTF8.GetString($bytes)
$utf16Image = [Text.Encoding]::Unicode.GetString($bytes)
$hasRequireAdministrator =
    $utf8Image.Contains('requireAdministrator', [StringComparison]::Ordinal) -or
    $utf16Image.Contains('requireAdministrator', [StringComparison]::Ordinal)
if (-not $hasRequireAdministrator) {
    throw 'Elevated Broker does not embed a requireAdministrator application manifest.'
}

Write-Host "Managed Broker passed: self-contained single EXE, requireAdministrator manifest embedded."

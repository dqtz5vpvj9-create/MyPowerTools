[CmdletBinding()]
param(
    [switch]$EnableAutoApply,
    [switch]$DisableAutoApply,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools')
)

$ErrorActionPreference = 'Stop'

if ($EnableAutoApply -and $DisableAutoApply) {
    throw 'Use either -EnableAutoApply or -DisableAutoApply.'
}

$autoApply = $EnableAutoApply.IsPresent
$policyRoot = Join-Path $DataRoot 'ota-state'
New-Item -ItemType Directory -Path $policyRoot -Force | Out-Null
$policyPath = Join-Path $policyRoot 'update-policy.json'
$policy = [ordered]@{
    schemaVersion = 1
    autoApply = $autoApply
    updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
[IO.File]::WriteAllText(
    $policyPath,
    ($policy | ConvertTo-Json -Depth 4),
    [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    PolicyPath = $policyPath
    AutoApply = $autoApply
} | ConvertTo-Json -Depth 3

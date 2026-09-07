<#
.SYNOPSIS
  Runs a script inside the interactive logon session and streams its output back.

.DESCRIPTION
  SSH on Windows authenticates with a network logon, which has no interactive
  logon session attached. Most things do not care. The Credential Manager does:
  CredRead answers 1312 ERROR_NO_SUCH_LOGON_SESSION instead of the 1168
  ERROR_NOT_FOUND a GitHub runner sees, so anything reading a secret fails here
  for a reason that has nothing to do with the code under test.

  A scheduled task registered with -LogonType Interactive runs in the session the
  user is actually logged into, where CredRead behaves normally. This wrapper
  registers such a task, starts it, tails its log until it exits, and returns its
  exit code, so the caller sees the same stream and the same result it would have
  seen over plain SSH.

  It needs someone logged in - `query session` must show an active session for
  this user. On a machine with nobody logged in there is no interactive session
  to borrow and the caller has to fall back to running directly over SSH.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Script,
    # Base64 so the argument string survives zsh, ssh and PowerShell parsing intact.
    # A quoted string with spaces does not: each layer strips a level of quoting
    # and the tail arrives as separate tokens.
    [string]$ArgumentsBase64 = '',
    [string]$LogPath = 'C:\ci\incoming\session-run.log',
    [string]$TaskName = 'MptCiLocalRun',
    [int]$TimeoutMinutes = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$Arguments = if ($ArgumentsBase64) {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
} else { '' }

# The task writes its own transcript; -File does not parse redirection operators
# out of the argument string, so the wrapper below owns the redirection.
$runner = Join-Path (Split-Path -Parent $LogPath) 'session-runner.ps1'
@"
`$ErrorActionPreference = 'Continue'
try {
    & pwsh.exe -NoLogo -NoProfile -File '$Script' $Arguments *>&1 |
        Tee-Object -FilePath '$LogPath'
    exit `$LASTEXITCODE
} catch {
    `$_ | Out-String | Add-Content -LiteralPath '$LogPath'
    exit 1
}
"@ | Set-Content -LiteralPath $runner -Encoding utf8

Remove-Item -LiteralPath $LogPath -ErrorAction SilentlyContinue
New-Item -ItemType File -Path $LogPath -Force | Out-Null

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument "-NoLogo -NoProfile -File `"$runner`""
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::FromMinutes($TimeoutMinutes))
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null

try {
    Start-ScheduledTask -TaskName $TaskName
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $offset = 0
    while ($true) {
        Start-Sleep -Milliseconds 700
        if (Test-Path -LiteralPath $LogPath) {
            $lines = @(Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue)
            if ($lines.Count -gt $offset) {
                $lines[$offset..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
                $offset = $lines.Count
            }
        }
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -eq $task) { break }
        if ($task.State -ne 'Running' -and $offset -gt 0) { break }
        if ((Get-Date) -gt $deadline) {
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            throw "The run did not finish within $TimeoutMinutes minutes."
        }
    }

    $info = Get-ScheduledTask -TaskName $TaskName | Get-ScheduledTaskInfo
    exit $info.LastTaskResult
} finally {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
}

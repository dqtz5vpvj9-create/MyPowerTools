param([Parameter(Mandatory)][string]$DataRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot 'scripts/update-windows-dev.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Dev overlay script has parse errors.' }
foreach ($name in @('Copy-DirectoryContents', 'Find-ToolServiceUnits', 'Publish-ToolServiceUnit', 'Test-IsInsidePath', 'Get-ProcessesInDirectory', 'Restore-OverlayTransaction')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $function) { throw "Missing function $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
$repositoryRoot = $DataRoot
$publishRoot = Join-Path $DataRoot 'published'
$unitRoot = Join-Path $DataRoot 'tools/fixture/service-units/fixture.service'
$unitBin = Join-Path $unitRoot 'bin'
New-Item -ItemType Directory -Path $unitBin -Force | Out-Null
@{ id = 'fixture.service'; toolId = 'fixture'; exec = 'run.ps1' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $unitRoot 'unit-manifest.json')
'Write-Output fixture' | Set-Content -LiteralPath (Join-Path $unitBin 'run.ps1')
$units = @(Find-ToolServiceUnits -RequestedToolId fixture)
if ($units.Count -ne 1 -or $null -ne $units[0].Project) { throw 'Script-only service was not discovered.' }
$output = Publish-ToolServiceUnit -RequestedToolId fixture -Unit $units[0]
if ((Get-Content -LiteralPath (Join-Path $output 'bin/run.ps1') -Raw).Trim() -ne 'Write-Output fixture') { throw 'Script-only payload was not staged.' }
if (-not (Test-Path -LiteralPath (Join-Path $output 'unit-manifest.json'))) { throw 'Service manifest was not staged.' }
$hostedDirectory = Join-Path $DataRoot 'selected-unit'
$script:processFixtures = @(
    [pscustomobject]@{ Name = 'pwsh.exe'; ProcessId = 100; ParentProcessId = 1; ExecutablePath = 'C:\PowerShell\pwsh.exe'; CommandLine = "pwsh -File `"$hostedDirectory\bin\run.ps1`"" },
    [pscustomobject]@{ Name = 'cmd.exe'; ProcessId = 101; ParentProcessId = 100; ExecutablePath = 'C:\Windows\cmd.exe'; CommandLine = 'cmd /c helper.cmd' },
    [pscustomobject]@{ Name = 'pwsh.exe'; ProcessId = 200; ParentProcessId = 1; ExecutablePath = 'C:\PowerShell\pwsh.exe'; CommandLine = "pwsh -File `"${hostedDirectory}-other\bin\run.ps1`"" }
)
function Get-CimInstance { param($ClassName) return $script:processFixtures }
$selectedIds = @(Get-ProcessesInDirectory -Directory $hostedDirectory | ForEach-Object ProcessId)
if ($selectedIds.Count -ne 2 -or 100 -notin $selectedIds -or 101 -notin $selectedIds) { throw 'Hosted script or descendants escaped process cleanup, or another unit was selected.' }

function Request-ShellShutdown {}
function Request-RunnerShutdown {}
function Request-ServiceManagerShutdown {}
function Stop-ToolPackageRuntimes { param($Components) }
function Start-InstalledRuntime { param([switch]$OpenShell) }
$canonicalInstallRoot = Join-Path $DataRoot 'installation'
$NoOpenShell = $false
$overlayManifestBackup = Join-Path $DataRoot 'absent-backup.json'
$installedOverlayManifest = Join-Path $DataRoot 'absent-installed.json'
$target = Join-Path $canonicalInstallRoot 'unit'
$backup = Join-Path $DataRoot 'backup/unit'
New-Item -ItemType Directory -Path $target, $backup -Force | Out-Null
'remaining' | Set-Content -LiteralPath (Join-Path $target 'remaining.txt')
'moved' | Set-Content -LiteralPath (Join-Path $backup 'moved.txt')
$restored = Restore-OverlayTransaction -AppliedComponents @([pscustomobject]@{ Target = $target; Backup = $backup; Applied = $false; HadOriginal = $true; PackageId = ''; RuntimeExecutables = @() })
if (-not $restored -or -not (Test-Path -LiteralPath (Join-Path $target 'moved.txt')) -or -not (Test-Path -LiteralPath (Join-Path $target 'remaining.txt')) -or (Test-Path -LiteralPath (Join-Path $target 'unit'))) { throw 'Partial move rollback nested or lost the unit payload.' }
@{ discovered = $units.Count; payload = $true; manifest = $true; scriptDescendants = $true; partialRollback = $true } | ConvertTo-Json -Compress

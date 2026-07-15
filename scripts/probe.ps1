$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Copy the EXACT registry block from build-all-tools.ps1
$toolRegistry = @(
    @{
        Id                = 'adb-forwarder'
        BuildScript       = 'tools\adb-forwarder\build.ps1'
        SurfaceProject    = 'tools\adb-forwarder\current-integration\src\AdbForwarder.Surface\AdbForwarder.Surface.csproj'
        RuntimeStagePath  = 'tools\adb-forwarder\artifacts\package'
    }
    @{
        Id                = 'screenease'
        BuildScript       = 'tools\screenease\build.ps1'
        SurfaceProject    = 'tools\screenease\current-integration\src\ScreenEase.Surface\ScreenEase.Surface.csproj'
        RuntimeStagePath  = 'tools\screenease\artifacts\package'
    }
)

$ToolId = @('screenease')
$wanted = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($id in $ToolId) { [void]$wanted.Add($id) }
$toolRegistry = @($toolRegistry | Where-Object { $wanted.Contains([string]$_['Id']) })
Write-Host "regCount=$($toolRegistry.Count)"
foreach ($tool in $toolRegistry) {
    $toolId = [string]$tool['Id']
    Write-Host "toolType=$($tool.GetType().FullName) toolId=[$toolId] toolIdType=$($toolId.GetType().FullName)"
    $v = $tool['Id']
    Write-Host "  raw Id type=$($v.GetType().FullName) isString=$($v -is [string]) count=$(if ($v.Count) { $v.Count } else { 1 })"
}

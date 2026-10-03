[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $repositoryRoot
try {
    $sdkFeed = Join-Path $repositoryRoot 'artifacts/sdk/nuget'
    if (-not (Test-Path -LiteralPath (Join-Path $sdkFeed 'MyPowerTools.AvaloniaSdk.0.2.0.nupkg'))) {
        & (Join-Path $PSScriptRoot 'build-sdk.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'E2E SDK bootstrap failed.' }
    }
    # ADB's CLI refusal tests intentionally inspect the Release entrypoint.
    & dotnet build src/MyPowerTools.Cli/MyPowerTools.Cli.csproj -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'E2E Release CLI prerequisite build failed.' }
    & dotnet build src/MyPowerTools.Tests/MyPowerTools.Tests.csproj -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'E2E workflow assembly build failed.' }
    & dotnet build tools/nssm-manager/sdk-tool/tests/NssmManager.Tests/NssmManager.Tests.csproj -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'NSSM workflow assembly build failed.' }
    & dotnet build tests/PersonalUx.Tests/PersonalUx.Tests.csproj -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Personal UX workflow assembly build failed.' }
    & dotnet build tests/e2e/xbrd-workflows/Xbrd.Workflows.Tests.csproj -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'XBRD workflow assembly build failed.' }
}
finally { Pop-Location }

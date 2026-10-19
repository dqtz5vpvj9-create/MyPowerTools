"""Offline contracts for development/publish fast paths.

Only isolated functions/packaging tails are executed in temporary directories.
No SDK build, native install, signing, network, real credentials or service use.
Set MPT_PIPELINE_ROOT to a pre-change snapshot for comparable regression evidence.
"""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest

REPO = Path(__file__).resolve().parents[2]
ROOT = Path(os.environ.get('MPT_PIPELINE_ROOT', REPO))
PWSH = os.environ.get('MPT_TEST_PWSH', 'pwsh')


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


class DevPipelineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='mpt-pipeline-regression-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def source(self, path):
        return (ROOT / path).read_text(encoding='utf-8-sig')

    def run_ps(self, body):
        script = self.root / 'fixture.ps1'
        script.write_text("$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version Latest\n" + body)
        result = subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-NonInteractive',
                                 '-File', str(script)], cwd=self.root,
                                text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=60)
        self.assertEqual(0, result.returncode, result.stdout)
        return result.stdout

    def load_functions(self, relative, *names):
        # Parse and evaluate only the named function declarations, never a script
        # entry point (the real entry points stop apps/install services).
        names_ps = ','.join(quote(name) for name in names)
        return f"""
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    {quote(ROOT / relative)}, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) {{ throw ($parseErrors | Out-String) }}
foreach ($name in @({names_ps})) {{
    $node = $ast.Find({{
        param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
    }}, $true)
    if ($null -eq $node) {{ throw "Missing function: $name" }}
    . ([scriptblock]::Create($node.Extent.Text))
}}
"""

    def test_windows_layout_only_preserves_zip_but_keeps_embedded_manifest(self):
        source = self.source('scripts/publish-windows.ps1')
        start = source.rfind('\nif (', 0, source.index('Remove-Item -LiteralPath $ZipPath -Force'))
        end = source.index('\nif ($isWebDistribution)', source.index('Write-Sha256File -Path $ZipPath'))
        tail = source[start:end]
        helper = self.root / 'compress-release-zip.ps1'
        helper.write_text("param($Root, $OutputPath)\nSet-Content -LiteralPath $OutputPath -Value 'new archive'\n")
        for layout_only in (True, False):
            with self.subTest(layout_only=layout_only):
                layout = self.root / ('layout-only' if layout_only else 'portable')
                layout.mkdir()
                archive = self.root / f'{layout.name}.zip'
                archive.write_text('existing release archive')
                manifest = self.root / f'{layout.name}.manifest.json'
                self.run_ps(f"""
$LayoutOnly = ${str(layout_only).lower()}
$PortableOnly = $true
$isWebDistribution = $false
$PublishRoot = {quote(layout)}
$ZipPath = {quote(archive)}
$ManifestAssetPath = {quote(manifest)}
$Version = '1.2.3'
function Invoke-Native {{
    param($FilePath, $ArgumentList)
    $pathIndex = [array]::IndexOf($ArgumentList, '-OutputPath')
    Set-Content -LiteralPath $ArgumentList[$pathIndex + 1] -Value '{{"version":"1.2.3"}}'
}}
function Write-Sha256File {{ param($Path); Set-Content -LiteralPath "$Path.sha256" -Value 'hash' }}
& {{
{tail}
}}
""")
                self.assertEqual('1.2.3', json.loads((layout / 'MyPowerTools-win-x64.manifest.json').read_text('utf-8-sig'))['version'])
                if layout_only:
                    self.assertEqual('existing release archive', archive.read_text())
                    self.assertFalse(Path(str(archive) + '.sha256').exists())
                else:
                    self.assertEqual('new archive', archive.read_text().strip())
                    self.assertTrue(Path(str(archive) + '.sha256').exists())
        # The only new early return stays after the existing validation and
        # provenance gates, rather than bypassing preparation of an installable tree.
        early_return = source.index('if ($LayoutOnly) {')
        self.assertLess(source.index("'validate',"), early_return)
        self.assertLess(source.index("'build-provenance.json'"), early_return)
        self.assertLess(source.index('Copy-Item -LiteralPath $ManifestAssetPath'), early_return)
        self.assertLess(early_return, source.index('$deltaPackages ='))

    def test_layout_only_rejects_conflicting_distribution_modes(self):
        source = self.source('scripts/publish-windows.ps1')
        prefix = source[:source.index('$RepoRoot =')]
        script = self.root / 'parameter-contract.ps1'
        script.write_text(prefix)
        for arguments, expected in ((['-LayoutOnly'], 0),
                                    (['-PortableOnly'], 0),
                                    (['-LayoutOnly', '-PortableOnly'], 1),
                                    (['-LayoutOnly', '-Distribution', 'Web'], 1),
                                    (['-LayoutOnly', '-Distribution', 'All'], 1)):
            with self.subTest(arguments=arguments):
                result = subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-NonInteractive',
                                         '-File', str(script), *arguments], text=True,
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
                self.assertEqual(expected, result.returncode, result.stdout)

    def test_source_install_requests_layout_only_and_propagates_publish_failure(self):
        repository = self.root / 'repository'
        (repository / 'scripts').mkdir(parents=True)
        child = repository / 'scripts/publish-windows.ps1'
        child.write_text("""param([switch]$LayoutOnly, [switch]$PortableOnly, [switch]$AllowUnsigned, $Channel)
@{layout=$LayoutOnly.IsPresent; portable=$PortableOnly.IsPresent; unsigned=$AllowUnsigned.IsPresent; channel=$Channel} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'arguments.json')
if ($env:MPT_FIXTURE_FAIL -eq '1') { exit 9 }
""")
        self.run_ps(self.load_functions('scripts/install-windows.ps1', 'Invoke-SourcePortableBuild') + f"""
function Get-Command {{ param($Name, $CommandType, $ErrorAction); [pscustomobject]@{{Source={quote(shutil.which(PWSH) or PWSH)}}} }}
Invoke-SourcePortableBuild -RepositoryRoot {quote(repository)}
$env:MPT_FIXTURE_FAIL = '1'
$threw = $false
try {{ Invoke-SourcePortableBuild -RepositoryRoot {quote(repository)} }}
catch {{ $threw = $_.Exception.Message -like '*exit code 9*' }}
if (-not $threw) {{ throw 'Publish failure was swallowed.' }}
""")
        arguments = json.loads((repository / 'scripts/arguments.json').read_text('utf-8-sig'))
        self.assertEqual({'layout': True, 'portable': False, 'unsigned': True, 'channel': 'local'}, arguments)

    def test_macos_wrapper_skips_only_discarded_archive(self):
        source = self.source('scripts/publish-macos-base.ps1')
        start = source.find('if ($SkipArchive) {')
        if start == -1:
            start = source.index("$releaseRoot = Join-Path $artifactsRoot 'release'")
        for skip in (True, False):
            with self.subTest(skip=skip):
                artifacts = self.root / str(skip)
                app = artifacts / 'MyPowerTools.app'
                app.mkdir(parents=True)
                (app / 'fixture.txt').write_text('signed bundle fixture')
                self.run_ps(f"""
$SkipArchive = ${str(skip).lower()}
$artifactsRoot = {quote(artifacts)}
$appBundle = {quote(app)}
$Architecture = 'arm64'
& {{
{source[start:]}
}}
""")
                archive = artifacts / 'release/MyPowerTools-macos-arm64.zip'
                self.assertEqual(not skip, archive.exists())
                self.assertEqual(not skip, Path(str(archive) + '.sha256').exists())
                self.assertEqual('signed bundle fixture', (app / 'fixture.txt').read_text())
        wrapper = self.source('scripts/publish-macos.ps1')
        parameters = wrapper[wrapper.index('$baseParameters = @{'):wrapper.index('if ($SkipNativeBuild)')]
        self.assertIn('SkipArchive = $true', parameters)
        self.assertIn('SkipCodeSign = $true', parameters)
        self.assertLess(source.index("-Activity 'codesign verification'"), start)
        self.assertIn('Copy-Item -LiteralPath $zipPath -Destination $legacyZipPath -Force', wrapper)
        self.assertIn("'--verify'", wrapper)

    def test_child_parameter_inspection_never_executes_the_script(self):
        self.run_ps(self.load_functions('scripts/update-windows-dev.ps1', 'Test-ScriptParameter') + f"""
$path = Join-Path {quote(self.root)} 'declarations.ps1'
foreach ($header in @('param([switch]$NoRestore)', 'param([string]$Configuration)', '', 'param()')) {{
    Set-Content -LiteralPath $path -Value ($header + "`nthrow 'Inspection must never execute the child.'")
    $expected = $header -eq 'param([switch]$NoRestore)'
    if ((Test-ScriptParameter -ScriptPath $path -ParameterName NoRestore) -ne $expected) {{
        throw "Incorrect parameter inspection: $header"
    }}
}}
Set-Content -LiteralPath $path -Value 'param('
$threw = $false
try {{ Test-ScriptParameter -ScriptPath $path -ParameterName NoRestore }}
catch {{ $threw = $true }}
if (-not $threw) {{ throw 'Malformed child declarations must fail inspection.' }}
""")

    def test_timings_record_native_success_and_failure_without_hiding_failure(self):
        source = self.source('scripts/update-windows-dev.ps1')
        # Isolate the safe report writer from the enclosing native-install finally.
        start = source.index('    try {\n        $script:runWatch.Stop()')
        report = source[start:source.rfind('\n}')]
        output = self.root / 'timings'
        self.run_ps(self.load_functions('scripts/update-windows-dev.ps1', 'Invoke-Native', 'Write-Phase') + f"""
$scriptStart = [DateTimeOffset]::UtcNow
$script:runWatch = [Diagnostics.Stopwatch]::StartNew()
$script:phaseEvents = [Collections.Generic.List[object]]::new()
$script:nativeTimings = [Collections.Generic.List[object]]::new()
Invoke-Native -FilePath {quote(PWSH)} -ArgumentList @('-NoProfile', '-Command', 'exit 0') -Activity 'Success fixture'
$threw = $false
try {{ Invoke-Native -FilePath {quote(PWSH)} -ArgumentList @('-NoProfile', '-Command', 'exit 7') -Activity 'Failure fixture' }}
catch {{ $threw = $_.Exception.Message -like '*exit code 7*' }}
if (-not $threw) {{ throw 'Native failure was hidden.' }}
$devArtifactsParent = {quote(output)}
$Scope = 'Tools'; $Configuration = 'Debug'; $NoRestore = [switch]$true
$updateSucceeded = $false; $rollbackSucceeded = $true
{report}
""")
        timing = json.loads((output / 'last-timings.json').read_text('utf-8-sig'))
        self.assertFalse(timing['succeeded'])
        self.assertTrue(timing['rollbackSucceeded'])
        self.assertTrue(timing['noRestore'])
        self.assertEqual([0, 7], [item['exitCode'] for item in timing['nativeCommands']])
        self.assertEqual([True, False], [item['succeeded'] for item in timing['nativeCommands']])
        self.assertEqual(2, len(timing['phases']))
        for item in timing['phases'] + timing['nativeCommands']:
            self.assertGreaterEqual(item['milliseconds'], 0)
        self.assertNotIn('ArgumentList', json.dumps(timing))

    def test_legacy_tool_declarations_do_not_receive_unsupported_configuration(self):
        source = self.source('scripts/update-windows-dev.ps1')
        start = source.index('        $toolBuildParameters = @{')
        end = source.index('        Invoke-Native @toolBuildParameters', start)
        arguments = source[start:end]
        legacy_tools = ('adb-forwarder', 'doubao-computer-use', 'paste-image')
        self.run_ps(self.load_functions('scripts/update-windows-dev.ps1', 'Test-ScriptParameter') + f"""
$pwshCommand = [pscustomobject]@{{Source={quote(PWSH)}}}
$repositoryRoot = {quote(self.root)}
$NoRestore = $true
foreach ($requestedToolId in @({','.join(quote(tool) for tool in legacy_tools)})) {{
    $actual = Join-Path {quote(REPO)} "tools/$requestedToolId/build.ps1"
    # Keep real declarations and binding attributes, replace every executable
    # statement with a harmless fixture body. No actual tool build is executed.
    if (Test-Path -LiteralPath $actual) {{
        $tokens = $null; $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($actual, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) {{ throw 'Invalid actual tool declarations.' }}
        $header = $ast.Extent.Text.Substring(0, $ast.ParamBlock.Extent.EndOffset)
    }} else {{
        # The portable CI job intentionally checks out no submodules. This is
        # the exact parameter contract of the pinned adb/doubao builders.
        $header = '[CmdletBinding()] param([string] $MyPowerToolsRepoRoot)'
    }}
    $toolBuildScript = Join-Path {quote(self.root)} "$requestedToolId.ps1"
    Set-Content -LiteralPath $toolBuildScript -Value ($header + "`nWrite-Output 'fixture body reached'")
    foreach ($Configuration in @('Debug', 'Release')) {{
        $warnings = @(& {{
{arguments}
            & $toolBuildParameters.FilePath @($toolBuildParameters.ArgumentList)
            if ($LASTEXITCODE -ne 0) {{ throw "Actual legacy parameter binding failed for $requestedToolId." }}
        }} 3>&1)
        if ($Configuration -eq 'Debug' -and
            -not ($warnings | Where-Object {{ $_ -is [System.Management.Automation.WarningRecord] -and $_.Message -like '*Release*' }})) {{
            throw "A fixed-Release legacy build must not be reported as Debug: $requestedToolId."
        }}
    }}
}}
$requestedToolId = 'modern-fixture'
$toolBuildScript = Join-Path {quote(self.root)} 'modern.ps1'
Set-Content -LiteralPath $toolBuildScript -Value 'param($MyPowerToolsRepoRoot, $Configuration, [switch]$NoRestore)'
$Configuration = 'Debug'
foreach ($NoRestore in @($true, $false)) {{
{arguments}
    $index = [array]::IndexOf($toolBuildParameters.ArgumentList, '-Configuration')
    if ($index -lt 0 -or $toolBuildParameters.ArgumentList[$index + 1] -ne 'Debug' -or
        ($toolBuildParameters.ArgumentList -contains '-NoRestore') -ne $NoRestore) {{
        throw 'Supported Configuration and NoRestore parameters were not preserved.'
    }}
}}
""")

    def test_remote_commands_reuses_only_fresh_release_surface_package(self):
        self.run_ps(self.load_functions('scripts/build-all-tools.ps1', 'Invoke-ToolBuild', 'Copy-SurfaceOutput') + """
function Invoke-Native {
    param($FilePath, $ArgumentList, $Activity)
    $step = if ($FilePath -eq 'pwsh.exe') {'runtime'} else {$ArgumentList[0]}
    $calls.Add($step)
    if ($step -eq 'restore') {
        if ($ArgumentList -notcontains '--force-evaluate' -or
            $ArgumentList -notcontains "-p:MyPowerToolsRepoRoot=$fixtureRoot") { throw 'SDK refresh contract changed.' }
        return
    }
    if ($step -eq 'runtime' -and $mode -eq 'child-failure') { throw 'fixture child failed' }
    # Model the pinned child contract: it clears old packages before packing.
    if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
    New-Item -ItemType Directory $pack -Force | Out-Null
    if ($mode -ne 'missing-pack') { Set-Content (Join-Path $pack 'fresh.nupkg') $step }
    $buildConfiguration = if ($step -eq 'runtime') {'Release'} else {$Configuration}
    $build = Join-Path $fixtureRoot "surface/bin/$buildConfiguration/net10.0"
    New-Item -ItemType Directory $build -Force | Out-Null
    Set-Content (Join-Path $build 'Fixture.Surface.dll') $step
}
$fixtureRoot = Join-Path $PWD 'surface-reuse'
New-Item -ItemType Directory (Join-Path $fixtureRoot 'surface'), (Join-Path $fixtureRoot 'runtime') -Force | Out-Null
Set-Content (Join-Path $fixtureRoot 'build.ps1') '# fixture'
Set-Content (Join-Path $fixtureRoot 'surface/Fixture.csproj') '<Project />'
foreach ($case in @(@('remote-commands','Release','ok'), @('remote-commands','Debug','ok'),
                    @('paste-image','Release','ok'), @('remote-commands','Release','child-failure'),
                    @('remote-commands','Release','missing-pack'))) {
    $id, $configuration, $mode = $case
    $tool = [pscustomobject]@{Id=$id; Version='0.2.0'; BuildScript='build.ps1';
        SurfaceProject='surface/Fixture.csproj'; RuntimeStagePath='runtime';
        SurfaceTarget='ui/surface'; SurfaceAssembly='Fixture.Surface.dll'}
    $pack = Join-Path $fixtureRoot "tools/$id/artifacts/surface"
    New-Item -ItemType Directory $pack -Force | Out-Null
    Set-Content (Join-Path $pack 'stale.nupkg') 'stale'
    $calls = [Collections.Generic.List[string]]::new()
    $state = $null; $failure = ''
    try {
        $state = Invoke-ToolBuild -Tool $tool -repoRoot $fixtureRoot -Configuration $configuration `
            -Version '' -dotnet 'dotnet-fixture' -cliExe '' -prebuiltCliTools @()
    } catch { $failure = $_.Exception.Message }
    if ($mode -ne 'ok') {
        $expected = if ($mode -eq 'child-failure') {'*fixture child failed*'} else {'*produced no .nupkg*'}
        if ($state -or $failure -notlike $expected) { throw 'Failed/empty child pack must never reuse stale packages.' }
        continue
    }
    if ($failure) { throw $failure }
    $reuse = $id -eq 'remote-commands' -and $configuration -eq 'Release'
    $expectedCalls = if ($reuse) {'restore,runtime'} else {'runtime,restore,pack'}
    $expectedContent = if ($reuse) {'runtime'} else {'pack'}
    if (($calls -join ',') -ne $expectedCalls -or $state.SurfaceOutputs.Count -ne 1 -or
        (Get-Content $state.SurfaceOutputs[0]) -ne $expectedContent -or
        (Get-Content (Join-Path $fixtureRoot 'runtime/ui/surface/Fixture.Surface.dll')) -ne $expectedContent) {
        throw 'Restore order, fresh package collection, or DLL staging changed.'
    }
}
""")

    def test_tool_workers_bound_overlap_order_states_and_drain_failures(self):
        self.run_ps(self.load_functions('scripts/build-all-tools.ps1',
                                       'Invoke-ToolBuildPhase', 'Invoke-Native', 'Copy-SurfaceOutput') + """
function Invoke-ToolBuild {
    param($Tool, $Probe, $Gate, $FailId)
    $Probe.Enqueue([pscustomobject]@{Kind='start'; Id=$Tool.Id})
    try {
        Write-Host "fixture $($Tool.Id)"
        if ($Tool.WaitForPeer) {
            [void]$Gate.Signal()
            if (-not $Gate.Wait(10000)) { throw 'Workers did not overlap.' }
        }
        Start-Sleep -Milliseconds $Tool.Delay
        if ($Tool.Id -eq $FailId) { throw "fixture failure $FailId" }
        $nativeCommand = "Write-Output 'native fixture log'; [Console]::Error.WriteLine('MC: Compiling fixture.messages.mc')"
        if ($FailId -eq "native:$($Tool.Id)") { $nativeCommand += '; exit 7' }
        Invoke-Native -FilePath (Join-Path $PSHOME $(if ($IsWindows) {'pwsh.exe'} else {'pwsh'})) `
            -ArgumentList @('-NoProfile', '-Command', $nativeCommand)
        [pscustomobject]@{ToolId=$Tool.Id}
    } finally { $Probe.Enqueue([pscustomobject]@{Kind='end'; Id=$Tool.Id}) }
}
$ids = @('adb-forwarder', 'remote-notifications', 'remote-commands', 'paste-image', 'local-lag-cleaner', 'nssm-manager')
$tools = @($ids | ForEach-Object {
    [pscustomobject]@{Id=$_; Delay=$(if ($_ -eq 'adb-forwarder') {200} else {10}); WaitForPeer=$false}
})
foreach ($limit in @(2, 1)) {
    $probe = [Collections.Concurrent.ConcurrentQueue[object]]::new()
    $gate = [Threading.CountdownEvent]::new(2)
    $tools[0].WaitForPeer = $tools[1].WaitForPeer = ($limit -eq 2)
    $states = @(Invoke-ToolBuildPhase -Tools $tools -MaxWorkers $limit -Context @{Probe=$probe; Gate=$gate; FailId=''})
    $gate.Dispose()
    if (($states.ToolId -join ',') -ne ($ids -join ',')) { throw 'State order/stdout isolation changed.' }
    $running = 0; $maximum = 0; $serialRunning = 0; $serialOrder = @()
    $conflicts = @('remote-notifications', 'remote-commands', 'local-lag-cleaner', 'nssm-manager')
    foreach ($event in $probe.ToArray()) {
        $change = if ($event.Kind -eq 'start') {1} else {-1}
        $running += $change; $maximum = [Math]::Max($maximum, $running)
        if ($event.Id -in $conflicts) {
            $serialRunning += $change
            if ($serialRunning -gt 1) { throw 'Conflicting writers overlapped.' }
            if ($change -eq 1) { $serialOrder += $event.Id }
        }
    }
    if ($maximum -ne $limit -or $running -ne 0 -or ($serialOrder -join ',') -ne ($conflicts -join ',')) {
        throw 'Concurrency bound, draining, or conflict order changed.'
    }
}
# An unaudited tool might share either lane: the entire selection must serialize.
$probe = [Collections.Concurrent.ConcurrentQueue[object]]::new()
$futureTools = @($tools[0], [pscustomobject]@{Id='future-tool'; Delay=10; WaitForPeer=$false}, $tools[1])
$states = @(Invoke-ToolBuildPhase -Tools $futureTools -MaxWorkers 2 -Context @{Probe=$probe; Gate=$null; FailId=''})
$events = $probe.ToArray()
if (($states.ToolId -join ',') -ne ($futureTools.Id -join ',') -or
    ($events.Kind -join ',') -ne 'start,end,start,end,start,end') { throw 'Unknown tool must force serial fallback.' }
foreach ($failure in @('remote-notifications', 'native:remote-notifications')) {
$probe = [Collections.Concurrent.ConcurrentQueue[object]]::new()
$gate = [Threading.CountdownEvent]::new(2)
$tools[0].WaitForPeer = $tools[1].WaitForPeer = $true
$selected = if ($failure -like 'native:*') { @($tools[0], $tools[1]) } else { $tools }
$expected = if ($failure -like 'native:*') { '*failed with exit code 7*' } else { '*fixture failure remote-notifications*' }
$states = @(); $failed = $false; $phaseB = $false
try {
    $states = @(Invoke-ToolBuildPhase -Tools $selected -MaxWorkers 2 -Context @{Probe=$probe; Gate=$gate; FailId=$failure})
    $phaseB = $true
} catch { $failed = $_.Exception.Message -like $expected }
$gate.Dispose()
$events = $probe.ToArray()
if (-not $failed -or $phaseB -or $states.Count -or @(Get-Job).Count -or
    @($events | Where-Object Kind -eq start).Count -ne 2 -or @($events | Where-Object Kind -eq end).Count -ne 2) {
    throw 'Failure must drain started workers, remove jobs, and prevent states/Phase B/pending builds.'
}
}
""")

    def test_release_jobs_use_selective_pinned_checkout(self):
        for path, job in (('.github/workflows/ci.yml', 'release'),
                          ('.github/workflows/ci.yml', 'macos-release'),
                          ('.github/workflows/macos-ota-release.yml', 'publish')):
            with self.subTest(path=path, job=job):
                source = self.source(path)
                section = re.search(rf'^  {re.escape(job)}:\n(.*?)(?=^  [\w-]+:\n|\Z)', source, re.M | re.S).group(1)
                self.assertIn('submodules: false', section)
                self.assertNotIn('submodules: recursive', section)
                self.assertIn('uses: ./.github/actions/checkout-submodules', section)
                action = section.split('uses: ./.github/actions/checkout-submodules', 1)[1].split('\n      - ', 1)[0]
                self.assertIn('token: ${{ secrets.MPT_SUBMODULE_PAT }}', action)

    def test_selective_checkout_keeps_all_build_gitlinks_and_credentials_in_env(self):
        action = self.source('.github/actions/checkout-submodules/action.yml')
        body = textwrap.dedent(action.split('      run: |\n', 1)[1])
        stub = self.root / 'git'
        # No real Git invocation occurs: this records argv/env and supplies the
        # checked-in path declarations, including the pinned private dependency.
        paths = re.findall(r'^\s*path = (.+)$', (REPO / '.gitmodules').read_text(), re.M)
        records = self.root / 'git-calls.jsonl'
        stub.write_text('#!' + shutil.which('python3') + '\n' + f"""
import json, os, sys
with open({str(records)!r}, 'a') as output:
    output.write(json.dumps({{'args':sys.argv[1:], 'config':{{k:v for k,v in os.environ.items() if k.startswith('GIT_CONFIG_')}}}}) + '\\n')
if sys.argv[1:2] == ['config']:
    for index, path in enumerate({paths!r}):
        print('submodule.fixture' + str(index) + '.path ' + path)
""")
        stub.chmod(0o755)
        fixture = self.root / 'action.sh'
        fixture.write_text(body)
        env = {k: v for k, v in os.environ.items() if not k.startswith('GIT_CONFIG_')}
        env.update(PATH=str(self.root) + os.pathsep + env['PATH'],
                   SUBMODULE_TOKEN='fixture-not-a-credential', SUBMODULE_EXCLUDE='external/haidian')
        result = subprocess.run(['bash', str(fixture)], env=env, cwd=self.root, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        self.assertEqual(0, result.returncode, result.stdout)
        calls = [json.loads(line) for line in records.read_text().splitlines()]
        update = next(call for call in calls if 'update' in call['args'])
        selected = update['args'][update['args'].index('--') + 1:]
        self.assertEqual(set(paths) - {'external/haidian'}, set(selected))
        self.assertIn('external/NotifyApp', selected)
        self.assertGreaterEqual(len([path for path in selected if path.startswith('tools/')]), 10)
        self.assertNotIn('--remote', update['args'])
        for flag in ('--init', '--force', '--recursive', '--depth=1'):
            self.assertIn(flag, update['args'])
        self.assertEqual('8', update['args'][update['args'].index('--jobs') + 1])
        for call in calls:
            self.assertNotIn('fixture-not-a-credential', ' '.join(call['args']))
        self.assertEqual('1', update['config']['GIT_CONFIG_COUNT'])
        self.assertEqual('https://github.com/', update['config']['GIT_CONFIG_VALUE_0'])
        self.assertIn('fixture-not-a-credential', update['config']['GIT_CONFIG_KEY_0'])
        self.assertEqual(1, len([call for call in calls if call['args'][0] == 'config']))
        self.assertIn('--get-regexp', calls[0]['args'])


if __name__ == '__main__':
    unittest.main()

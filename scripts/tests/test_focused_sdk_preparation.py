"""Test focused SDK publication/cache safety with fake dotnet and local packages.

No build, network, installed product, or service is used. Set MPT_TEST_PWSH when
PowerShell 7 is not on PATH. Actual MSBuild invalidation/timing is tested separately.
"""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


REPO = Path(__file__).resolve().parents[2]
PWSH = os.environ.get('MPT_TEST_PWSH', 'pwsh')


class FocusedSdkPreparationTests(unittest.TestCase):
    def test_batch_preserves_the_full_sdk_project_set(self):
        build_sdk = (REPO / 'scripts/build-sdk.ps1').read_text()
        project_list = build_sdk.split('$projects = @(', 1)[1].split('\n)', 1)[0]
        expected = {path.replace('\\', '/') for path in re.findall("'([^']+)'", project_list)}
        batch = ET.parse(REPO / 'scripts/focused-test-sdk.proj')
        actual = {item.attrib['Include'].replace('$(MSBuildThisFileDirectory)../', '')
                  for item in batch.findall('.//SdkProject')}
        self.assertEqual(expected, actual)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='mpt-focused-sdk-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'scripts').mkdir()
        for name in ('prepare-focused-test-sdk.ps1', 'focused-test-sdk.proj'):
            shutil.copy2(REPO / 'scripts' / name, self.root / 'scripts' / name)
        self.fixture = self.root / 'packages'
        self.fixture.mkdir()
        self.feed = self.root / 'artifacts/sdk/nuget'
        self.cache = self.root / 'artifacts/sdk/global-packages'
        self.env = dict(os.environ, XDG_DATA_HOME=str(self.root / 'xdg-data'),
                        XDG_CONFIG_HOME=str(self.root / 'xdg-config'),
                        XDG_CACHE_HOME=str(self.root / 'xdg-cache'))
        self.env.pop('NUGET_PACKAGES', None)
        self.env.pop('RestorePackagesPath', None)
        self.wrapper = self.root / 'invoke.ps1'
        self.wrapper.write_text(r'''
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
function dotnet {
    if ($args[0] -eq 'nuget') {
        $global:LASTEXITCODE = 0
        $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $PSScriptRoot 'artifacts/sdk/global-packages' }
        "global-packages: $cache"
        return
    }
    if ($args[0] -eq '--version') {
        $global:LASTEXITCODE = 0
        if ($env:FAKE_SDK_VERSION) { $env:FAKE_SDK_VERSION } else { '10.0.301' }
        return
    }
    if ($env:FAKE_PACK_FAIL) { $global:LASTEXITCODE = 7; return }
    $list = ($args | Where-Object { $_ -like '-property:FocusedSdkOutputList=*' }).Substring(31)
    $stage = ($args | Where-Object { $_ -like '-property:PackageOutputPath=*' }).Substring(28)
    Add-Content (Join-Path $PSScriptRoot 'pack-calls.txt') $stage
    Add-Content (Join-Path $PSScriptRoot 'restore-paths.txt') ($args | Where-Object { $_ -like '-property:RestorePackagesPath=*' })
    $outputs = @(Get-ChildItem (Join-Path $PSScriptRoot 'packages') -Filter '*.nupkg' | ForEach-Object {
        $target = Join-Path $stage $_.Name
        if (-not $env:FAKE_MISSING_OUTPUT) { Copy-Item $_.FullName $target -Force }
        $target
    })
    Set-Content -LiteralPath $list -Value $outputs
    $global:LASTEXITCODE = 0
}
& (Join-Path $PSScriptRoot 'scripts/prepare-focused-test-sdk.ps1') -Configuration $Configuration
''')

    def package(self, package_id='MyPowerTools.ToolSdk', version='0.2.0', payload='new'):
        path = self.fixture / f'{package_id}.{version}.nupkg'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr(f'{package_id}.nuspec',
                             f'<package><metadata><id>{package_id}</id><version>{version}</version></metadata></package>')
            archive.writestr('lib/net10.0/tool.dll', payload)
        return path

    def cached(self, package, contents=None):
        name = package.name.lower()
        # Fixtures deliberately use the normal package/version naming convention.
        version = '0.2.1' if '.0.2.1.' in name else '0.2.0'
        package_id = name.removesuffix(f'.{version}.nupkg')
        folder = self.cache / package_id / version
        folder.mkdir(parents=True)
        (folder / name).write_bytes(package.read_bytes() if contents is None else contents)
        (folder / 'retained-sentinel').write_text('keep')
        return folder

    def run_helper(self, configuration='Release'):
        return subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-NonInteractive',
                               '-File', str(self.wrapper), '-Configuration', configuration],
                              env=self.env, cwd=self.root, text=True, stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=30)

    def test_unchanged_package_keeps_restored_cache(self):
        package = self.package()
        cached = self.cached(package)
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertTrue((cached / 'retained-sentinel').exists())
        self.assertEqual(package.read_bytes(), (self.feed / package.name).read_bytes())
        self.assertIn('invalidated 0', result.stdout)

    def test_external_cache_overrides_fail_before_pack_or_publication(self):
        package = self.package()
        for override in ('NUGET_PACKAGES', 'RestorePackagesPath'):
            with self.subTest(override=override):
                external = self.root / 'external-cache' / override
                external.mkdir(parents=True)
                sentinel = external / 'keep'
                sentinel.write_text('untouched')
                self.env[override] = str(external)
                result = self.run_helper()
                self.env.pop(override)
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertIn('require the repository NuGet cache', result.stdout)
                self.assertEqual('untouched', sentinel.read_text())
                self.assertFalse(self.feed.exists())
                self.assertFalse((self.root / 'pack-calls.txt').exists())

    def test_matching_normalized_cache_override_is_supported(self):
        package = self.package()
        cached = self.cached(package)
        self.env['NUGET_PACKAGES'] = str(self.cache / '..' / 'global-packages') + os.sep
        self.env['RestorePackagesPath'] = str(self.cache)
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertTrue((cached / 'retained-sentinel').exists())
        self.assertEqual(f'-property:RestorePackagesPath={self.cache}',
                         (self.root / 'restore-paths.txt').read_text().strip())

    def test_only_changed_exact_package_version_is_invalidated(self):
        package = self.package(version='0.2.1')
        changed = self.cached(package, b'older same-version package')
        untouched = self.cache / 'mypowertools.toolsdk/0.2.0'
        untouched.mkdir(parents=True)
        (untouched / 'keep').write_text('keep')
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertFalse(changed.exists())
        self.assertTrue((untouched / 'keep').exists())
        self.assertIn('invalidated 1', result.stdout)

    def test_missing_cached_archive_is_invalidated(self):
        package = self.package()
        cached = self.cached(package)
        (cached / package.name.lower()).unlink()
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertFalse(cached.exists())

    def test_failed_pack_does_not_clear_cache_or_publish(self):
        cached = self.cached(self.package())
        self.env['FAKE_PACK_FAIL'] = '1'
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertTrue((cached / 'retained-sentinel').exists())
        self.assertEqual([], list(self.feed.glob('*.nupkg')))

    def test_missing_output_is_an_error(self):
        self.package()
        self.env['FAKE_MISSING_OUTPUT'] = '1'
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertIn('SDK package is missing', result.stdout)

    def test_empty_output_list_is_an_error(self):
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertIn('no package outputs', result.stdout)

    def test_sdk_and_configuration_have_distinct_staging_and_always_run_pack(self):
        self.package()
        for configuration, sdk in [('Release', '10.0.301'), ('Debug', '10.0.301'), ('Debug', '10.0.302')]:
            self.env['FAKE_SDK_VERSION'] = sdk
            result = self.run_helper(configuration)
            self.assertEqual(0, result.returncode, result.stdout)
        stages = (self.root / 'pack-calls.txt').read_text().splitlines()
        self.assertEqual(3, len(set(stages)))


if __name__ == '__main__':
    unittest.main()

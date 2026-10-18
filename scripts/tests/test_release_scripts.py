"""Exercise release scripts using temporary local files and Git fixtures only.

Requires PowerShell 7 (pwsh on PATH, or set MPT_TEST_PWSH to its executable).
No SDK, network access, installed app, services, or account credentials are used.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


REPO = Path(__file__).resolve().parents[2]
SCRIPTS = REPO / 'scripts'
PWSH = os.environ.get('MPT_TEST_PWSH', 'pwsh')
TOOL_IDS = ('adb-forwarder', 'remote-notifications', 'remote-commands',
            'process-monitor', 'screenease', 'smartbird-thermostat',
            'doubao-computer-use', 'input-monitor', 'screenshot', 'xbrd')
NATIVE_TOOLS = {'screenshot', 'xbrd'}


class ReleaseScriptTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='mpt-release-regression-')
        self.addCleanup(self.temporary.cleanup)
        # Git reports physical paths (for example, /private/var on macOS).
        self.root = Path(self.temporary.name).resolve()
        self.repo = self.root / 'repository'
        self.repo.mkdir()
        self.env = dict(os.environ, GIT_AUTHOR_NAME='Release Regression',
                        GIT_AUTHOR_EMAIL='release-regression@local.invalid',
                        GIT_COMMITTER_NAME='Release Regression',
                        GIT_COMMITTER_EMAIL='release-regression@local.invalid',
                        GIT_TERMINAL_PROMPT='0')

    def git(self, directory, *arguments):
        return subprocess.run(['git', '-c', 'commit.gpgSign=false', '-C', str(directory),
                               *arguments], env=self.env, check=True, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout.strip()

    def run_script(self, script, *arguments):
        return subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-NonInteractive',
                               '-File', str(SCRIPTS / script), *map(str, arguments)],
                              env=self.env, text=True, stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=120, cwd=self.root)

    def assert_success(self, result):
        self.assertEqual(0, result.returncode, result.stdout)

    def write_tool(self, root, tool_id, legacy=False):
        has_original = legacy or tool_id not in NATIVE_TOOLS
        directories = ['current-integration']
        if has_original:
            directories.append('original-source')
        for directory in directories:
            (root / directory).mkdir(parents=True)
            (root / directory / 'source.txt').write_text(f'{tool_id} {directory}\n')
        source_map = {'toolId': tool_id}
        if not legacy:
            source_map['originalSnapshotPath'] = 'original-source' if has_original else None
        (root / 'source-map.json').write_text(json.dumps(source_map))
        (root / 'tool-release.json').write_text(json.dumps({
            'status': 'active', 'packageId': tool_id,
        }))

    def initialize_repository(self):
        self.git(self.repo, 'init', '--initial-branch=main')
        (self.repo / 'README.md').write_text('Isolated release script test repository.\n')
        self.git(self.repo, 'add', 'README.md')
        self.git(self.repo, 'commit', '-m', 'fixture')

    def metadata_fixture(self):
        artifacts = self.repo / 'artifacts/release'
        artifacts.mkdir(parents=True)
        (artifacts / 'MyPowerTools-win-x64.zip').write_bytes(b'fixture archive bytes')
        (self.repo / 'version.json').write_text(json.dumps({
            'product': 'MyPowerTools', 'version': '4.5.6', 'channel': 'stable',
        }))
        return artifacts

    def test_metadata_resolves_default_version_from_sibling_script(self):
        artifacts = self.metadata_fixture()
        result = self.run_script('release-metadata.ps1', '-RepoRoot', self.repo,
                                 '-ArtifactsRoot', artifacts)
        self.assert_success(result)
        metadata = json.loads((artifacts / 'release-metadata.json').read_text('utf-8-sig'))
        scoop = json.loads((artifacts / 'package-managers/scoop/mypowertools.json').read_text('utf-8-sig'))
        self.assertEqual('4.5.6', metadata['version'])
        self.assertEqual('4.5.6', scoop['version'])
        self.assertEqual('local', metadata['channel'])

    def test_metadata_preserves_explicit_version_and_channel(self):
        artifacts = self.metadata_fixture()
        result = self.run_script('release-metadata.ps1', '-RepoRoot', self.repo,
                                 '-ArtifactsRoot', artifacts, '-Version', '7.8.9',
                                 '-Channel', 'nightly')
        self.assert_success(result)
        metadata = json.loads((artifacts / 'release-metadata.json').read_text('utf-8-sig'))
        self.assertEqual('7.8.9', metadata['version'])
        self.assertEqual('nightly', metadata['channel'])

    def test_source_bundle_includes_every_tool_submodule(self):
        self.initialize_repository()
        # DDNS keeps a source entrypoint under a package-layout bin directory.
        # It must survive source packaging without admitting generated binaries
        # or the retired duplicate DDNS implementation beside it.
        ddns = self.repo / 'tools/ddns'
        ddns_bin = ddns / 'service-units/ddns.service/bin'
        ddns_bin.mkdir(parents=True)
        (ddns / 'ddns.ps1').write_text('# canonical DDNS implementation\n')
        (ddns_bin / 'ddns-runner.cmd').write_text('@echo off\r\npwsh -File ddns.ps1\r\n')
        (ddns_bin / 'generated.dll').write_bytes(b'generated binary fixture')
        (ddns_bin / 'ddns.ps1').write_text('# stale duplicate must not be bundled\n')
        (ddns_bin / 'other-generated.cmd').write_text('@echo generated\n')
        self.git(self.repo, 'add', 'tools/ddns')
        self.git(self.repo, 'commit', '-m', 'DDNS source package fixture')
        for tool_id in TOOL_IDS:
            root = self.repo / 'tools' / tool_id
            self.write_tool(root, tool_id)
            self.git(root, 'init', '--initial-branch=main')
            self.git(root, 'add', '.')
            self.git(root, 'commit', '-m', 'tool fixture')
            sha = self.git(root, 'rev-parse', 'HEAD')
            self.git(self.repo, 'update-index', '--add', '--cacheinfo',
                     f'160000,{sha},tools/{tool_id}')
            self.git(self.repo, 'config', '-f', '.gitmodules',
                     f'submodule.tools/{tool_id}.path', f'tools/{tool_id}')
            self.git(self.repo, 'config', '-f', '.gitmodules',
                     f'submodule.tools/{tool_id}.url', root.as_uri())
        self.git(self.repo, 'add', '.gitmodules')
        self.git(self.repo, 'commit', '-m', 'tool gitlinks')
        bundle = self.repo / 'artifacts/source-bundle'
        archive = self.repo / 'artifacts/release/source.zip'
        result = self.run_script('create-source-bundle.ps1', '-RepoRoot', self.repo,
                                 '-OutputRoot', bundle, '-ArchivePath', archive)
        self.assert_success(result)
        manifest = json.loads((bundle / 'bundle-manifest.json').read_text('utf-8-sig'))
        self.assertEqual(set(TOOL_IDS), {tool['toolId'] for tool in manifest['tools']})
        with zipfile.ZipFile(archive) as zipped:
            contents = set(zipped.namelist())
            ddns_prefix = 'superproject/tools/ddns/'
            runner = 'service-units/ddns.service/bin/ddns-runner.cmd'
            self.assertIn(ddns_prefix + runner, contents)
            self.assertEqual((ddns / runner).read_bytes(), zipped.read(ddns_prefix + runner))
            self.assertIn(ddns_prefix + 'ddns.ps1', contents)
            for excluded in ('generated.dll', 'ddns.ps1', 'other-generated.cmd'):
                self.assertNotIn(ddns_prefix + 'service-units/ddns.service/bin/' + excluded, contents)
        for tool_id in TOOL_IDS:
            self.assertIn(f'superproject/tools/{tool_id}/source-map.json', contents)
            self.assertTrue((bundle / 'superproject/tools' / tool_id / 'source-map.json').is_file())
        # Check the actual producer/consumer contract, not just independent
        # hand-written fixtures of their manifest formats.
        destination = self.root / 'bundled-tool-repositories'
        result = self.run_script('materialize-tool-submodules.ps1', '-RepoRoot', self.repo,
                                 '-SourceBundleRoot', bundle, '-ToolReposRoot', destination,
                                 '-SkipSubmoduleAdd')
        self.assert_success(result)
        self.assertEqual('screenshot current-integration', self.git(
            destination / 'screenshot', 'show', 'HEAD:current-integration/source.txt'))
        for tool_id in NATIVE_TOOLS:
            self.assertFalse((bundle / 'superproject/tools' / tool_id / 'original-source').exists())
            self.assertFalse((destination / tool_id / 'original-source').exists())

    def make_source_bundle(self, schema_version):
        bundle = self.root / 'bundle'
        tool_relative = 'superproject/tools' if schema_version == 2 else 'tools'
        key = 'toolId' if schema_version == 2 else 'id'
        for tool_id in TOOL_IDS:
            self.write_tool(bundle / tool_relative / tool_id, tool_id, legacy=schema_version == 1)
        (bundle / 'bundle-manifest.json').write_text(json.dumps({
            'schemaVersion': schema_version, 'tools': [{key: tool_id} for tool_id in TOOL_IDS],
        }))
        return bundle

    def check_materialization(self, schema_version):
        self.initialize_repository()
        bundle = self.make_source_bundle(schema_version)
        destination = self.root / 'materialized-tools'
        before = self.git(self.repo, 'status', '--porcelain')
        result = self.run_script('materialize-tool-submodules.ps1', '-RepoRoot', self.repo,
                                 '-SourceBundleRoot', bundle, '-ToolReposRoot', destination,
                                 '-SkipSubmoduleAdd')
        self.assert_success(result)
        self.assertEqual(before, self.git(self.repo, 'status', '--porcelain'))
        self.assertFalse((self.repo / '.gitmodules').exists())
        for tool_id in TOOL_IDS:
            source = self.git(destination / tool_id, 'show', 'HEAD:current-integration/source.txt')
            self.assertEqual(f'{tool_id} current-integration', source)
            if schema_version == 1 or tool_id not in NATIVE_TOOLS:
                original = self.git(destination / tool_id, 'show', 'HEAD:original-source/source.txt')
                self.assertEqual(f'{tool_id} original-source', original)
            else:
                self.assertFalse((destination / tool_id / 'original-source').exists())
            self.assertEqual('', self.git(destination / tool_id, 'status', '--porcelain'))
        # A no-op rerun must recognize native tools as already materialized.
        self.assert_success(self.run_script(
            'materialize-tool-submodules.ps1', '-RepoRoot', self.repo,
            '-SourceBundleRoot', bundle, '-ToolReposRoot', destination, '-SkipSubmoduleAdd'))

    def test_materializer_reads_current_schema_two(self):
        self.check_materialization(2)

    def test_materializer_retains_legacy_schema_one(self):
        self.check_materialization(1)

    def test_incomplete_source_bundle_does_not_create_destinations(self):
        self.initialize_repository()
        bundle = self.make_source_bundle(2)
        shutil.rmtree(bundle / 'superproject/tools/input-monitor/original-source')
        destination = self.root / 'materialized-tools'
        result = self.run_script('materialize-tool-submodules.ps1', '-RepoRoot', self.repo,
                                 '-SourceBundleRoot', bundle, '-ToolReposRoot', destination,
                                 '-SkipSubmoduleAdd')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(destination.exists())
        self.assertEqual('', self.git(self.repo, 'status', '--porcelain'))

    def test_legacy_map_without_snapshot_field_still_requires_original_source(self):
        self.initialize_repository()
        bundle = self.make_source_bundle(1)
        shutil.rmtree(bundle / 'tools/input-monitor/original-source')
        destination = self.root / 'materialized-tools'
        result = self.run_script('materialize-tool-submodules.ps1', '-RepoRoot', self.repo,
                                 '-SourceBundleRoot', bundle, '-ToolReposRoot', destination,
                                 '-SkipSubmoduleAdd')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('input-monitor original-source', result.stdout)
        self.assertFalse(destination.exists())
        self.assertEqual('', self.git(self.repo, 'status', '--porcelain'))

    def test_native_tool_still_requires_current_integration(self):
        self.initialize_repository()
        bundle = self.make_source_bundle(2)
        shutil.rmtree(bundle / 'superproject/tools/screenshot/current-integration')
        destination = self.root / 'materialized-tools'
        result = self.run_script('materialize-tool-submodules.ps1', '-RepoRoot', self.repo,
                                 '-SourceBundleRoot', bundle, '-ToolReposRoot', destination,
                                 '-SkipSubmoduleAdd')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('screenshot current-integration', result.stdout)
        self.assertFalse(destination.exists())
        self.assertEqual('', self.git(self.repo, 'status', '--porcelain'))

    def test_refresh_can_remove_an_explicitly_retired_original_snapshot(self):
        self.initialize_repository()
        bundle = self.make_source_bundle(2)
        source = bundle / 'superproject/tools/screenshot'
        original = source / 'original-source'
        original.mkdir()
        (original / 'source.txt').write_text('old snapshot\n')
        mapping = source / 'source-map.json'
        mapping.write_text(json.dumps({'toolId': 'screenshot', 'originalSnapshotPath': 'original-source'}))
        destination = self.root / 'materialized-tools'
        arguments = ('-RepoRoot', self.repo, '-SourceBundleRoot', bundle,
                     '-ToolReposRoot', destination, '-SkipSubmoduleAdd')
        self.assert_success(self.run_script('materialize-tool-submodules.ps1', *arguments))
        self.assertEqual('old snapshot', self.git(
            destination / 'screenshot', 'show', 'HEAD:original-source/source.txt'))

        shutil.rmtree(original)
        mapping.write_text(json.dumps({'toolId': 'screenshot', 'originalSnapshotPath': None}))
        self.assert_success(self.run_script('materialize-tool-submodules.ps1', *arguments, '-ForceRefresh'))
        target = destination / 'screenshot'
        self.assertFalse((target / 'original-source').exists())
        self.assertEqual('', self.git(target, 'ls-tree', '-r', '--name-only', 'HEAD', '--', 'original-source'))
        self.assertEqual('', self.git(target, 'status', '--porcelain'))
        self.assertEqual('', self.git(self.repo, 'status', '--porcelain'))


if __name__ == '__main__':
    unittest.main()

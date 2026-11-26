"""Tailnet deployment boundary: reject broad binds and preserve relay auth policy."""
from __future__ import annotations
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT))
from mpt_relay.config import Config
SPEC = importlib.util.spec_from_file_location('install_tailnet', ROOT / 'deploy/install-tailnet.py')
INSTALL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALL)


def writable_temp_root() -> str:
    """The recipe's preferred cache mount when writable, else the repo's governed scratch area.

    CI and locked-down hosts may mount ``/mnt/cache/data-cache`` read-only; the test must still
    run without changing any production behaviour.
    """
    candidates = [
        Path('/mnt/cache/data-cache'),
        REPO_ROOT / 'artifacts' / '.tmp-android-verify' / 'public-relay-server',
    ]
    for candidate in candidates:
        try:
            candidate.mkdir(parents=True, exist_ok=True)
            probe = tempfile.mkdtemp(prefix='mpt-tail-probe-', dir=str(candidate))
        except OSError:
            continue
        os.rmdir(probe)
        return str(candidate)
    return tempfile.gettempdir()


class TailnetDeploymentTests(unittest.TestCase):
    def test_profile_loads_with_existing_auth_and_durable_store_defaults(self):
        with tempfile.TemporaryDirectory(prefix='mpt-tail-profile-', dir=writable_temp_root()) as directory:
            path = Path(directory) / 'relay.env'
            path.write_text(INSTALL.environment('100.64.0.1'))
            config = Config.load(['--config', str(path)], environ={})
        self.assertEqual(config.host, '100.64.0.1')
        self.assertEqual(config.data_dir, '/var/lib/mpt-tail-relay')
        self.assertTrue(config.trust_proxy_headers)
        self.assertEqual(config.trusted_proxies, ('100.64.0.1',))
        self.assertFalse(config.dav_auto_register)
        self.assertTrue(config.mask_log_ids)
        self.assertEqual(config.kdf_iterations, Config().kdf_iterations)
        self.assertEqual(config.global_bytes, Config().global_bytes)

    def test_preview_needs_neither_root_nor_tailscale_and_does_not_install(self):
        result = subprocess.run([sys.executable, str(ROOT / 'deploy/install-tailnet.py'), '--address', '100.64.0.1'], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('MPT_RELAY_HOST=100.64.0.1', result.stdout)
        self.assertNotIn('Installed ', result.stdout)

    def test_rejects_public_lan_and_wildcard_bind_before_apply(self):
        for address in ['0.0.0.0', '::', '8.8.8.8', '192.168.1.2', '127.0.0.1']:
            with self.subTest(address=address):
                result = subprocess.run([sys.executable, str(ROOT / 'deploy/install-tailnet.py'), '--address', address], capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)

    def test_enable_requires_apply(self):
        result = subprocess.run([sys.executable, str(ROOT / 'deploy/install-tailnet.py'), '--address', '100.64.0.1', '--enable'], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('--enable requires --apply', result.stderr)

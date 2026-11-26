#!/usr/bin/env python3
"""Install an isolated Tailnet relay; default is a read-only deployment preview.

sudo python3 deploy/install-tailnet.py --address 100.64.0.1 --apply --enable
Does not alter the public relay, nginx, routes, DNS, firewall or existing data.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request
from datetime import datetime, timezone

SOURCE = Path(__file__).resolve().parents[1]
ROOT = Path('/opt/mpt-tail-relay')
DATA = Path('/var/lib/mpt-tail-relay')
ENV = Path('/etc/mpt-tail-relay/relay.env')
UNIT = Path('/etc/systemd/system/mpt-tail-relay.service')
SERVICE = 'mpt-tail-relay'


def tail_address(value: str) -> str:
    address = ipaddress.ip_address(value)
    network = ipaddress.ip_network('100.64.0.0/10') if address.version == 4 else ipaddress.ip_network('fd7a:115c:a1e0::/48')
    if address not in network:
        raise argparse.ArgumentTypeError('address must be a Tailnet address, never a wildcard/public bind')
    return str(address)


def environment(address: str) -> str:
    return f'''# Dedicated local relay. Credentials and files stay separate from the public relay.
MPT_RELAY_HOST={address}
MPT_RELAY_PORT=18765
MPT_RELAY_BASE_PATH=/mpt/relay
MPT_RELAY_DATA_DIR={DATA}
# Only this host's nginx may supply a client address. Other peers cannot forge it.
MPT_RELAY_TRUST_PROXY_HEADERS=1
MPT_RELAY_TRUSTED_PROXIES={address}
MPT_RELAY_DAV_AUTO_REGISTER=0
MPT_RELAY_MASK_LOG_IDS=1
'''


def run(*args: str) -> None:
    subprocess.run(args, check=True)


def check_health(address: str, timeout: float = 20) -> None:
    host = f'[{address}]' if ':' in address else address
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    deadline = time.monotonic() + timeout
    while True:
        try:
            with opener.open(f'http://{host}:18765/mpt/relay/health', timeout=2) as response:
                if json.load(response).get('ok') is True:
                    return
        except (OSError, ValueError):
            pass
        if time.monotonic() >= deadline:
            raise RuntimeError('Tailnet relay health did not become ready; inspect journalctl -u mpt-tail-relay')
        time.sleep(.5)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--address', required=True, type=tail_address)
    parser.add_argument('--apply', action='store_true', help='install code and isolated unit; default only previews')
    parser.add_argument('--enable', action='store_true', help='enable/start and verify; requires --apply')
    args = parser.parse_args()
    if args.enable and not args.apply:
        parser.error('--enable requires --apply')
    unit_text = (SOURCE / 'deploy/mpt-tail-relay.service').read_text()
    if not args.apply:
        print(f'code={ROOT}\ndata={DATA}\nunit={UNIT}\n')
        print(environment(args.address))
        print(unit_text)
        return
    if os.geteuid() != 0:
        parser.error('--apply requires root')
    state = json.loads(subprocess.check_output(['tailscale', 'status', '--json']))
    if args.address not in state.get('Self', {}).get('TailscaleIPs', []):
        parser.error('--address must belong to this machine according to tailscale status')
    if ENV.exists():
        # Keep operator tuning; refuse to silently change a deployed endpoint or data root.
        import sys
        sys.path.insert(0, str(SOURCE))
        from mpt_relay.config import Config
        config = Config.load(['--config', str(ENV)], environ={})
        if (config.host, config.port, config.data_dir, config.base_path) != (args.address, 18765, str(DATA), '/mpt/relay'):
            parser.error('existing relay.env endpoint/data path differs; review it before upgrading')
        if config.trust_proxy_headers and config.trusted_proxies != (args.address,):
            parser.error('Tailnet relay may trust only this host as its nginx proxy')
    if subprocess.run(['getent', 'passwd', SERVICE], stdout=subprocess.DEVNULL).returncode:
        run('useradd', '--system', '--user-group', '--home-dir', str(ROOT), '--shell', '/usr/sbin/nologin', SERVICE)
    run('install', '-d', '-m', '0750', '-o', SERVICE, '-g', SERVICE, str(DATA))
    run('install', '-d', '-m', '0755', str(ROOT / 'releases'), str(ENV.parent))
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    release = ROOT / 'releases' / stamp
    release.mkdir(mode=0o755)
    shutil.copytree(SOURCE / 'mpt_relay', release / 'mpt_relay', ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    shutil.copy2(SOURCE / 'README.md', release / 'README.md')
    for path in release.rglob('*'):
        path.chmod(0o755 if path.is_dir() else 0o644)
    if not ENV.exists():
        ENV.write_text(environment(args.address))
        run('chown', f'root:{SERVICE}', str(ENV))
        ENV.chmod(0o640)
    old_unit = UNIT.read_bytes() if UNIT.exists() else None
    if old_unit is not None:
        UNIT.with_name(f'{UNIT.name}.backup-{stamp}').write_bytes(old_unit)
    current = ROOT / 'current'
    old_target = os.readlink(current) if current.is_symlink() else None
    staged = ROOT / f'next-{stamp}'
    staged.symlink_to(release)
    os.replace(staged, current)
    UNIT.write_text(unit_text)
    UNIT.chmod(0o644)
    run('systemctl', 'daemon-reload')
    if args.enable:
        try:
            run('systemctl', 'restart', SERVICE)
            check_health(args.address)
            run('systemctl', 'is-active', '--quiet', SERVICE)
            run('systemctl', 'enable', SERVICE)
        except Exception:
            if old_target is not None:
                staged.symlink_to(old_target)
                os.replace(staged, current)
                if old_unit is not None:
                    UNIT.write_bytes(old_unit)
                run('systemctl', 'daemon-reload')
                run('systemctl', 'restart', SERVICE)
            else:
                run('systemctl', 'stop', SERVICE)
            raise
    print(f'Installed {release}; old releases and data retained. enable={args.enable}')
    print('Protocol acceptance: python3 deploy/smoke.py --base http://' + (f'[{args.address}]' if ':' in args.address else args.address) + ':18765')


if __name__ == '__main__':
    main()

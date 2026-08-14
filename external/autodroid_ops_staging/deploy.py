#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path


SAFE_VERSION = re.compile(r"^[A-Za-z0-9._-]{1,96}$")
SAFE_SHA = re.compile(r"^[0-9a-f]{7,64}$")


def run(command: list[str], *, dry_run: bool = False) -> None:
    print("+", " ".join(command))
    if not dry_run:
        subprocess.run(command, check=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--confirm-target", required=True)
    parser.add_argument("--artifact", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--git-sha", required=True)
    parser.add_argument("--release-root", default="/opt/remote-notifications")
    parser.add_argument("--service", default="simple_http_notification_server_lxr.service")
    parser.add_argument("--ssh-option", action="append", default=[])
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if args.target != args.confirm_target:
        parser.error("--confirm-target must exactly match --target")
    if not args.artifact.is_file():
        parser.error("artifact does not exist")
    if not SAFE_VERSION.fullmatch(args.version):
        parser.error("version contains unsafe characters")
    if not SAFE_SHA.fullmatch(args.git_sha):
        parser.error("git SHA must contain 7-64 lowercase hexadecimal characters")
    if not args.release_root.startswith("/opt/remote-notifications"):
        parser.error("release root must stay below /opt/remote-notifications")

    artifact = args.artifact.resolve()
    artifact_hash = sha256(artifact)
    release_name = f"{args.version}-{args.git_sha[:12]}"
    remote_archive = f"/tmp/remote-notifications-{release_name}.tar.gz"
    release_dir = f"{args.release_root}/releases/{release_name}"
    ssh = ["ssh"]
    scp = ["scp"]
    for option in args.ssh_option:
        ssh.extend(["-o", option])
        scp.extend(["-o", option])

    print(json.dumps({
        "target": args.target,
        "release": release_dir,
        "git_sha": args.git_sha,
        "artifact_sha256": artifact_hash,
    }, indent=2))

    run([*scp, str(artifact), f"{args.target}:{remote_archive}"], dry_run=args.dry_run)
    remote_script = "\n".join([
        "set -euo pipefail",
        f"test \"$(sha256sum {remote_archive} | awk '{{print $1}}')\" = {artifact_hash}",
        f"sudo mkdir -p {release_dir}",
        f"sudo tar -xzf {remote_archive} -C {release_dir}",
        f"sudo test -f {release_dir}/py_modules/simple_http_notification_server.py",
        f"sudo ln -sfn {release_dir} {args.release_root}/current.next",
        f"sudo mv -Tf {args.release_root}/current.next {args.release_root}/current",
        f"sudo systemctl restart {args.service}",
        f"sudo systemctl is-active --quiet {args.service}",
        f"rm -f {remote_archive}",
    ])
    run([*ssh, args.target, "bash", "-lc", remote_script], dry_run=args.dry_run)
    return 0


if __name__ == "__main__":
    sys.exit(main())


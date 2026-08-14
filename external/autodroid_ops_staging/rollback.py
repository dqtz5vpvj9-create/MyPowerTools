#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
import subprocess


SAFE_RELEASE = re.compile(r"^[A-Za-z0-9._-]{1,128}$")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--confirm-target", required=True)
    parser.add_argument("--release", required=True)
    parser.add_argument("--release-root", default="/opt/remote-notifications")
    parser.add_argument("--service", default="simple_http_notification_server_lxr.service")
    args = parser.parse_args()
    if args.target != args.confirm_target:
        parser.error("--confirm-target must exactly match --target")
    if not SAFE_RELEASE.fullmatch(args.release):
        parser.error("invalid release name")
    release_dir = f"{args.release_root}/releases/{args.release}"
    script = "\n".join([
        "set -euo pipefail",
        f"sudo test -f {release_dir}/py_modules/simple_http_notification_server.py",
        f"sudo ln -sfn {release_dir} {args.release_root}/current.next",
        f"sudo mv -Tf {args.release_root}/current.next {args.release_root}/current",
        f"sudo systemctl restart {args.service}",
        f"sudo systemctl is-active --quiet {args.service}",
    ])
    subprocess.run(["ssh", args.target, "bash", "-lc", script], check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

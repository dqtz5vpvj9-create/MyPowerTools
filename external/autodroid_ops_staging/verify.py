#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--service", default="simple_http_notification_server_lxr.service")
    args = parser.parse_args()
    command = [
        "ssh", args.target,
        "systemctl", "show", args.service,
        "-p", "ActiveState", "-p", "SubState", "-p", "MainPID", "-p", "WorkingDirectory",
    ]
    result = subprocess.run(command, check=True, text=True, capture_output=True)
    state = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
    print(json.dumps(state, indent=2))
    return 0 if state.get("ActiveState") == "active" and state.get("SubState") == "running" else 1


if __name__ == "__main__":
    raise SystemExit(main())


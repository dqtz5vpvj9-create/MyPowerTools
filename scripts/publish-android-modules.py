#!/usr/bin/env python3
"""Build ONLY the 4 Android tool modules (fast, ~5-10s, no APK/NDK rebuild),
package tiny delta .zip files (100-650 KB each, omitting unchanged BouncyCastle),
generate manifest.json with SHA-256 checksums, and publish to:
  1. Tailscale nginx static dir (/var/www/mpt-android-modules) -> live immediately at
     http://mpt-relay.tail.lixinrui000.cn/mpt/android-modules/manifest.json
  2. artifacts/android-modules/ -> ready for GitHub Release upload (public proxy fallback).
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]

MODULES = [
    {
        "id": "file-transfer",
        "projects": [
            "tools/file-transfer/src/FileTransfer.MyPowerTools/FileTransfer.MyPowerTools.csproj",
            "tools/file-transfer/src/FileTransfer.Surface/FileTransfer.Surface.csproj",
        ],
        "package_dir": "tools/file-transfer/artifacts/package",
        "include_crypto": False,
    },
    {
        "id": "remote-notifications-android",
        "projects": [
            "tools/remote-notifications/android-integration/src/RemoteNotifications.Android/RemoteNotifications.Android.csproj",
            "src/MyPowerTools.MobileNotifications/MyPowerTools.MobileNotifications.csproj",
        ],
        "package_dir": "tools/remote-notifications/android-integration/artifacts/package/remote-notifications-android",
        "include_crypto": False,
    },
    {
        "id": "remote-commands-android",
        "projects": [
            "tools/remote-commands/android-integration/src/RemoteCommands.Android/RemoteCommands.Android.csproj",
            "src/MyPowerTools.MobileRemoteCommands/MyPowerTools.MobileRemoteCommands.csproj",
        ],
        "package_dir": "tools/remote-commands/android-integration/artifacts/package/remote-commands-android",
        "include_crypto": False,
    },
    {
        "id": "mobile-tool-control",
        "projects": [
            "tools/remote-tool-gateway/android-integration/src/MobileToolControl.Android/MobileToolControl.Android.csproj",
            "src/MyPowerTools.MobileToolControl/MyPowerTools.MobileToolControl.csproj",
        ],
        "package_dir": "tools/remote-tool-gateway/android-integration/artifacts/package/mobile-tool-control",
        "include_crypto": False,
    },
]

EXCLUDED_NAMES = {
    "MyPowerTools.Abstractions.dll",
    "MyPowerTools.Platform.Abstractions.dll",
    "MyPowerTools.AvaloniaSdk.dll",
}


def should_exclude(rel: Path, include_crypto: bool) -> bool:
    name = rel.name
    if name.endswith((".pdb", ".deps.json", ".local.json")):
        return True
    if name in EXCLUDED_NAMES or name.startswith("Avalonia"):
        return True
    if not include_crypto and name == "BouncyCastle.Cryptography.dll":
        return True
    if "state" in rel.parts:
        return True
    return False


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--version", default=datetime.datetime.now(datetime.timezone.utc).strftime("0.2.20-%Y%m%d.%H%M"))
    parser.add_argument("--min-host-version-code", type=int, default=24)
    parser.add_argument("--output-dir", type=Path, default=REPO_ROOT / "artifacts/android-modules")
    parser.add_argument("--tailnet-dir", type=Path, default=Path("/var/www/mpt-android-modules"))
    parser.add_argument("--proxy-ssh", default="proxy", help="SSH host for public proxy.lixinrui000.cn fallback sync (empty to skip).")
    parser.add_argument("--proxy-dir", default="/var/www/mpt-android-modules")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--include-crypto", action="store_true", help="Include BouncyCastle.Cryptography.dll inside module zips.")
    args = parser.parse_args()

    if not args.skip_build:
        env = os.environ.copy()
        env["PATH"] = f"{Path.home() / '.dotnet'}:{env.get('PATH', '')}"
        # Build packaged module outputs (without building Android APK!)
        build_ps1 = REPO_ROOT / "scripts/build-android.ps1"
        for mod in MODULES:
            for proj in mod["projects"]:
                p = REPO_ROOT / proj
                print(f"[build] {proj}")
                subprocess.run(
                    ["dotnet", "build", str(p), "-c", args.configuration, "--nologo", "-v", "minimal"],
                    cwd=REPO_ROOT,
                    env=env,
                    check=True,
                )

    args.output_dir.mkdir(parents=True, exist_ok=True)
    entries = []
    total_bytes = 0

    for mod in MODULES:
        pkg_dir = REPO_ROOT / mod["package_dir"]
        if not pkg_dir.exists():
            raise SystemExit(f"Missing packaged module directory: {pkg_dir} (run module packaging first)")

        zip_name = f"{mod['id']}.zip"
        zip_path = args.output_dir / zip_name
        file_count = 0
        with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
            for file_path in sorted(pkg_dir.rglob("*")):
                if not file_path.is_file():
                    continue
                rel = file_path.relative_to(pkg_dir)
                if should_exclude(rel, args.include_crypto or mod["include_crypto"]):
                    continue
                zf.write(file_path, rel.as_posix())
                file_count += 1

        size = zip_path.stat().st_size
        digest = sha256_file(zip_path)
        total_bytes += size
        entries.append({
            "id": mod["id"],
            "version": args.version,
            "file": zip_name,
            "sha256": digest,
            "sizeBytes": size,
        })
        print(f"  packed {zip_name:34s} {size / 1024:7.1f} KB ({file_count:2d} files) sha256={digest[:12]}")

    manifest = {
        "schemaVersion": 1,
        "generatedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
        "minHostVersionCode": args.min_host_version_code,
        "modules": entries,
    }
    manifest_path = args.output_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Total delta payload: {total_bytes / 1024:.1f} KB -> {manifest_path}")

    if args.tailnet_dir and args.tailnet_dir.exists() and os.access(args.tailnet_dir, os.W_OK):
        for f in [manifest_path] + [args.output_dir / e["file"] for e in entries]:
            tmp = args.tailnet_dir / (f.name + ".tmp")
            shutil.copy2(f, tmp)
            os.replace(tmp, args.tailnet_dir / f.name)
        print(f"Published live to Tailnet dir: {args.tailnet_dir}")
        print("Tailscale URL: http://mpt-relay.tail.lixinrui000.cn/mpt/android-modules/manifest.json")

    if args.proxy_ssh:
        files = [str(manifest_path)] + [str(args.output_dir / e["file"]) for e in entries]
        remote_stage = f"{args.proxy_dir}/.incoming"
        subprocess.run(["ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", args.proxy_ssh, f"mkdir -p {remote_stage}"], stdin=subprocess.DEVNULL, check=True)
        subprocess.run(["scp", "-q", "-o", "BatchMode=yes", *files, f"{args.proxy_ssh}:{remote_stage}/"], stdin=subprocess.DEVNULL, check=True)
        subprocess.run(["ssh", "-T", "-o", "BatchMode=yes", args.proxy_ssh, f"mv -f {remote_stage}/* {args.proxy_dir}/ && rmdir {remote_stage}"], stdin=subprocess.DEVNULL, check=True)
        print(f"Published live to Public Proxy ({args.proxy_ssh}:{args.proxy_dir})")
        print("Public Proxy URL: https://proxy.lixinrui000.cn/mpt/android-modules/manifest.json")



if __name__ == "__main__":
    main()

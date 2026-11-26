#!/usr/bin/env python3
"""
Cross-platform portable tool packager for MyPowerTools ("轻巧可分可合").

Produces two win-x64 layouts under artifacts/portable/:
  1. composable-suite/
     - shared-ui/          (MptStandaloneHost + Avalonia + win-x64 Skia/HarfBuzz)
     - MptFileTransfer/    (hundred-KB tool-only folder + .mptboot.json)
     - MptImeManager/
     - MptPasteImage/
     - MptAudioRelay/
     - MptLagCleaner/
     - MptNssmManager/
     - MptRemoteGateway/
  2. standalone/
     - MptFileTransfer/ + MptFileTransfer.zip (self-sufficient portable folder with portable.flag)
     - MptImeManager/   + MptImeManager.zip
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

APPHOST_PLACEHOLDER = b"c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"
APPHOST_PATH_MAX_BYTES = 1024

STRIP_EXTENSIONS = {".pdb", ".xml", ".dbg", ".so", ".dylib", ".a", ".dsym"}


@dataclass(frozen=True)
class ToolSpec:
    folder_name: str
    exe_name: str
    tool_id: str
    title: str
    kind: str  # "module" (Layout A) or "sdk" (Layout B)
    publish_projects: list[str]
    manifest_root: str
    copy_files: list[str] = field(default_factory=list)
    copy_dirs: list[str] = field(default_factory=list)
    build_standalone_zip: bool = False


TOOLS: list[ToolSpec] = [
    ToolSpec(
        folder_name="MptFileTransfer",
        exe_name="MptFileTransfer",
        tool_id="file-transfer",
        title="文件互传",
        kind="module",
        publish_projects=[
            "tools/file-transfer/src/FileTransfer.MyPowerTools/FileTransfer.MyPowerTools.csproj",
            "tools/file-transfer/src/FileTransfer.Surface/FileTransfer.Surface.csproj",
        ],
        manifest_root="tools/file-transfer/package",
        copy_files=["module.json", "commands.index.json"],
        copy_dirs=["ui"],
        build_standalone_zip=True,
    ),
    ToolSpec(
        folder_name="MptImeManager",
        exe_name="MptImeManager",
        tool_id="ime-manager",
        title="输入法管理器",
        kind="sdk",
        publish_projects=[
            "tools/ime-manager/sdk-tool/src/ImeManager.Tool/ImeManager.Tool.csproj",
            "tools/ime-manager/sdk-tool/src/ImeManager.Runtime/ImeManager.Runtime.csproj",
        ],
        manifest_root="tools/ime-manager/sdk-tool",
        copy_files=["tool.json", "settings.schema.json", "settings.json"],
        build_standalone_zip=True,
    ),
    ToolSpec(
        folder_name="MptPasteImage",
        exe_name="MptPasteImage",
        tool_id="paste-image",
        title="Paste Image",
        kind="module",
        publish_projects=[
            "tools/paste-image/current-integration/src/PasteImage.MyPowerTools/PasteImage.MyPowerTools.csproj",
            "tools/paste-image/current-integration/src/PasteImage.Surface/PasteImage.Surface.csproj",
        ],
        manifest_root="tools/paste-image/current-integration/modules/paste-image",
        copy_files=["module.json", "commands.index.json"],
        copy_dirs=["ui"],
    ),
    ToolSpec(
        folder_name="MptAudioRelay",
        exe_name="MptAudioRelay",
        tool_id="audio-relay",
        title="AudioRelay",
        kind="sdk",
        publish_projects=[
            "tools/audio-relay/sdk-tool/src/AudioRelay.Tool/AudioRelay.Tool.csproj",
            "tools/audio-relay/sdk-tool/src/AudioRelay.Runtime/AudioRelay.Runtime.csproj",
        ],
        manifest_root="tools/audio-relay/sdk-tool",
        copy_files=["tool.json", "settings.schema.json", "settings.json"],
    ),
    ToolSpec(
        folder_name="MptLagCleaner",
        exe_name="MptLagCleaner",
        tool_id="local-lag-cleaner",
        title="本机卡顿专清",
        kind="sdk",
        publish_projects=[
            "tools/local-lag-cleaner/sdk-tool/src/LocalLagCleaner.Tool/LocalLagCleaner.Tool.csproj",
            "tools/local-lag-cleaner/sdk-tool/src/LocalLagCleaner.Runtime/LocalLagCleaner.Runtime.csproj",
        ],
        manifest_root="tools/local-lag-cleaner/sdk-tool",
        copy_files=["tool.json", "settings.schema.json", "settings.json"],
    ),
    ToolSpec(
        folder_name="MptNssmManager",
        exe_name="MptNssmManager",
        tool_id="nssm-manager",
        title="NSSM 服务管理器",
        kind="sdk",
        publish_projects=[
            "tools/nssm-manager/sdk-tool/src/NssmManager.Tool/NssmManager.Tool.csproj",
            "tools/nssm-manager/sdk-tool/src/NssmManager.Runtime/NssmManager.Runtime.csproj",
        ],
        manifest_root="tools/nssm-manager/sdk-tool",
        copy_files=["tool.json", "settings.schema.json", "settings.json"],
    ),
    ToolSpec(
        folder_name="MptRemoteGateway",
        exe_name="MptRemoteGateway",
        tool_id="remote-tool-gateway",
        title="远程工具访问",
        kind="module",
        publish_projects=[
            "tools/remote-tool-gateway/src/RemoteToolGateway.MyPowerTools/RemoteToolGateway.MyPowerTools.csproj",
            "tools/remote-tool-gateway/src/RemoteToolGateway.Surface/RemoteToolGateway.Surface.csproj",
        ],
        manifest_root="tools/remote-tool-gateway/package",
        copy_files=["module.json", "commands.index.json"],
        copy_dirs=["ui"],
    ),
]


def build_dotnet_env() -> dict[str, str]:
    env = os.environ.copy()
    home_dotnet = str(Path.home() / ".dotnet")
    if os.path.isdir(home_dotnet):
        current_path = env.get("PATH", "")
        if home_dotnet not in current_path.split(os.pathsep):
            env["PATH"] = home_dotnet + os.pathsep + current_path
        env.setdefault("DOTNET_ROOT", home_dotnet)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def dotnet_publish(repo_root: Path, csproj_rel: str, out_dir: Path, configuration: str, rid: str, env: dict[str, str]) -> None:
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    cmd = [
        "dotnet",
        "publish",
        str(repo_root / csproj_rel),
        "-c",
        configuration,
        "-r",
        rid,
        "--self-contained",
        "false",
        "-p:EnableWindowsTargeting=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-o",
        str(out_dir),
        "-nologo",
    ]
    proc = subprocess.run(cmd, cwd=repo_root, env=env, text=True, capture_output=True)
    if proc.returncode != 0:
        sys.stderr.write(f"dotnet publish failed for {csproj_rel}:\n{proc.stdout}\n{proc.stderr}\n")
        raise SystemExit(proc.returncode)


def ensure_roll_forward_major(directory: Path) -> None:
    for rc_path in directory.rglob("*.runtimeconfig.json"):
        try:
            data = json.loads(rc_path.read_text(encoding="utf-8"))
            opts = data.setdefault("runtimeOptions", {})
            opts["rollForward"] = "Major"
            rc_path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
        except Exception:
            pass


def prune_published_tree(directory: Path, target_rid: str) -> None:
    for path in list(directory.rglob("*")):
        if path.is_file() and path.suffix.lower() in STRIP_EXTENSIONS:
            path.unlink()

    runtimes_dir = directory / "runtimes"
    if runtimes_dir.is_dir():
        allowed_rids = {target_rid.lower(), "win"}
        for child in list(runtimes_dir.iterdir()):
            if child.is_dir() and child.name.lower() not in allowed_rids:
                shutil.rmtree(child)
        if not any(runtimes_dir.iterdir()):
            runtimes_dir.rmdir()

    ensure_roll_forward_major(directory)


def find_win_apphost_template(rid: str, env: dict[str, str]) -> Path | None:
    roots = []
    if env.get("DOTNET_ROOT"):
        roots.append(Path(env["DOTNET_ROOT"]))
    roots.append(Path.home() / ".dotnet")
    roots.append(Path("C:/Program Files/dotnet"))

    exe_name = "apphost.exe" if rid.startswith("win") else "apphost"
    for root in roots:
        pack_dir = root / "packs" / f"Microsoft.NETCore.App.Host.{rid}"
        if not pack_dir.is_dir():
            continue
        candidates = sorted(pack_dir.glob(f"*/runtimes/{rid}/native/{exe_name}"))
        if candidates:
            return candidates[-1]
    return None


def write_launcher_exe(
    dest_exe: Path,
    bootstrap_exe: Path | None,
    fallback_host_exe: Path,
    apphost_template: Path | None,
    relative_host_dll_win: str,
) -> str:
    dest_exe.parent.mkdir(parents=True, exist_ok=True)
    if bootstrap_exe is not None and bootstrap_exe.is_file():
        shutil.copy2(bootstrap_exe, dest_exe)
        return "mpt-bootstrap"

    if apphost_template is not None and apphost_template.is_file():
        raw = apphost_template.read_bytes()
        idx = raw.find(APPHOST_PLACEHOLDER)
        encoded = relative_host_dll_win.encode("utf-8") + b"\x00"
        if idx >= 0 and len(encoded) <= APPHOST_PATH_MAX_BYTES:
            patched = (
                raw[:idx]
                + encoded
                + (b"\x00" * (APPHOST_PATH_MAX_BYTES - len(encoded)))
                + raw[idx + APPHOST_PATH_MAX_BYTES :]
            )
            dest_exe.write_bytes(patched)
            return "stamped-apphost"

    shutil.copy2(fallback_host_exe, dest_exe)
    return "fallback-host-exe"


def write_boot_manifest(
    dest_boot_json: Path,
    tool: ToolSpec,
    probe_dirs: list[str],
    portable: bool,
) -> None:
    payload: dict[str, object] = {
        "entryAssembly": "MptStandaloneHost.dll",
        "hostAssembly": "MptStandaloneHost.dll",
        "toolId": tool.tool_id,
        "title": tool.title,
        "minDotNetMajor": 8,
        "rollForward": "Major",
        "portable": portable,
        "probeDirs": probe_dirs,
        "args": ["--tool-id", tool.tool_id],
    }
    dest_boot_json.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def copy_tool_manifests(repo_root: Path, tool: ToolSpec, target_root: Path) -> None:
    manifest_src = repo_root / tool.manifest_root
    for rel_file in tool.copy_files:
        src_file = manifest_src / rel_file
        if src_file.is_file():
            dst_file = target_root / rel_file
            dst_file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src_file, dst_file)

    for rel_dir in tool.copy_dirs:
        src_dir = manifest_src / rel_dir
        if src_dir.is_dir():
            dst_dir = target_root / rel_dir
            if dst_dir.exists():
                shutil.rmtree(dst_dir)
            shutil.copytree(src_dir, dst_dir)


def directory_size_bytes(path: Path) -> int:
    total = 0
    for item in path.rglob("*"):
        if item.is_file():
            total += item.stat().st_size
    return total


def format_size(num_bytes: int) -> str:
    if num_bytes < 1024 * 1024:
        return f"{num_bytes / 1024:.1f} KB ({num_bytes:,} B)"
    return f"{num_bytes / (1024 * 1024):.2f} MB ({num_bytes:,} B)"


def zip_directory(source_dir: Path, zip_path: Path) -> None:
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for file_path in sorted(source_dir.rglob("*")):
            if file_path.is_file():
                arcname = f"{source_dir.name}/{file_path.relative_to(source_dir).as_posix()}"
                zf.write(file_path, arcname)


def main() -> int:
    repo_root = Path(__file__).resolve().parent.parent
    default_bootstrap = repo_root / "native" / "mpt-bootstrap" / "bin" / "win-x64" / "mpt-bootstrap.exe"

    parser = argparse.ArgumentParser(description="Build composable & standalone portable Windows tool packages.")
    parser.add_argument(
        "--bootstrap-exe",
        type=Path,
        default=default_bootstrap if default_bootstrap.is_file() else None,
        help="Path to native mpt-bootstrap.exe (defaults to native/mpt-bootstrap/bin/win-x64/mpt-bootstrap.exe if present).",
    )
    parser.add_argument("--configuration", default="Release", help="MSBuild configuration (default: Release).")
    parser.add_argument("--rid", default="win-x64", help="Target RuntimeIdentifier (default: win-x64).")
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=repo_root / "artifacts" / "portable",
        help="Output directory (default: artifacts/portable).",
    )
    args = parser.parse_args()

    bootstrap_exe: Path | None = args.bootstrap_exe.resolve() if args.bootstrap_exe and args.bootstrap_exe.is_file() else None
    env = build_dotnet_env()
    out_root = args.output_dir.resolve()
    stage_root = out_root / "_stage"

    if out_root.exists():
        shutil.rmtree(out_root)
    stage_root.mkdir(parents=True, exist_ok=True)

    print(f"[1/4] Publishing MptStandaloneHost ({args.rid}, {args.configuration})...")
    host_stage = stage_root / "MptStandaloneHost"
    dotnet_publish(
        repo_root,
        "src/MyPowerTools.StandaloneHost/MyPowerTools.StandaloneHost.csproj",
        host_stage,
        args.configuration,
        args.rid,
        env,
    )
    prune_published_tree(host_stage, args.rid)

    fallback_host_exe = host_stage / "MptStandaloneHost.exe"
    apphost_template = find_win_apphost_template(args.rid, env)

    composable_root = out_root / "composable-suite"
    shared_ui_dir = composable_root / "shared-ui"
    shared_ui_dir.mkdir(parents=True, exist_ok=True)

    # Copy host + Avalonia + Skia runtime files into shared-ui/ (excluding MptStandaloneHost.exe apphost).
    shared_filenames: set[str] = set()
    for item in sorted(host_stage.iterdir()):
        if item.is_file() and item.name.lower() == "mptstandalonehost.exe":
            continue
        dst = shared_ui_dir / item.name
        if item.is_dir():
            shutil.copytree(item, dst)
        else:
            shutil.copy2(item, dst)
            shared_filenames.add(item.name.lower())

    # Record nested native files inside shared-ui as shared too.
    for nested in shared_ui_dir.rglob("*"):
        if nested.is_file():
            shared_filenames.add(nested.name.lower())

    standalone_root = out_root / "standalone"
    standalone_root.mkdir(parents=True, exist_ok=True)

    composable_probe_dirs = [
        "app",
        "..\\shared-ui",
        "%LOCALAPPDATA%\\MyPowerTools\\shared-ui",
        "%LOCALAPPDATA%\\Programs\\MyPowerTools\\Shell",
    ]
    standalone_probe_dirs = [
        "app",
        "%LOCALAPPDATA%\\MyPowerTools\\shared-ui",
        "%LOCALAPPDATA%\\Programs\\MyPowerTools\\Shell",
    ]

    launcher_mode_used = ""
    print(f"[2/4] Publishing and packaging {len(TOOLS)} tools...")
    for tool in TOOLS:
        merged_tool_stage = stage_root / "tools" / tool.folder_name
        merged_tool_stage.mkdir(parents=True, exist_ok=True)

        for idx, proj in enumerate(tool.publish_projects):
            proj_stage = stage_root / "proj" / f"{tool.folder_name}_{idx}"
            dotnet_publish(repo_root, proj, proj_stage, args.configuration, args.rid, env)
            prune_published_tree(proj_stage, args.rid)
            for src_item in proj_stage.rglob("*"):
                if src_item.is_file():
                    rel = src_item.relative_to(proj_stage)
                    dst_item = merged_tool_stage / rel
                    dst_item.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(src_item, dst_item)

        # 1) Composable hundred-KB folder (excludes anything already in shared-ui/)
        comp_dir = composable_root / tool.folder_name
        comp_app_dir = comp_dir / "app"
        comp_app_dir.mkdir(parents=True, exist_ok=True)
        copy_tool_manifests(repo_root, tool, comp_dir)

        launcher_mode_used = write_launcher_exe(
            comp_dir / f"{tool.exe_name}.exe",
            bootstrap_exe,
            fallback_host_exe,
            apphost_template,
            "..\\shared-ui\\MptStandaloneHost.dll",
        )
        write_boot_manifest(
            comp_dir / f"{tool.exe_name}.mptboot.json",
            tool,
            composable_probe_dirs,
            portable=False,
        )

        for src_file in sorted(merged_tool_stage.rglob("*")):
            if not src_file.is_file():
                continue
            if src_file.name.lower() in shared_filenames:
                continue
            rel = src_file.relative_to(merged_tool_stage)
            dst_file = comp_app_dir / rel
            dst_file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src_file, dst_file)

        # 2) Self-sufficient standalone folder + zip (for designated standalone targets)
        if tool.build_standalone_zip:
            sa_dir = standalone_root / tool.folder_name
            sa_app_dir = sa_dir / "app"
            shutil.copytree(shared_ui_dir, sa_app_dir)
            copy_tool_manifests(repo_root, tool, sa_dir)
            (sa_dir / "portable.flag").write_text("portable=1\n", encoding="utf-8", newline="\n")

            write_launcher_exe(
                sa_dir / f"{tool.exe_name}.exe",
                bootstrap_exe,
                fallback_host_exe,
                apphost_template,
                "app\\MptStandaloneHost.dll",
            )
            write_boot_manifest(
                sa_dir / f"{tool.exe_name}.mptboot.json",
                tool,
                standalone_probe_dirs,
                portable=True,
            )

            for src_file in sorted(merged_tool_stage.rglob("*")):
                if not src_file.is_file():
                    continue
                rel = src_file.relative_to(merged_tool_stage)
                dst_file = sa_app_dir / rel
                dst_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(src_file, dst_file)

            zip_directory(sa_dir, standalone_root / f"{tool.folder_name}.zip")

    print("[3/4] Cleaning temporary build stage...")
    shutil.rmtree(stage_root, ignore_errors=True)

    print("[4/4] Package Size Summary")
    print("=" * 72)
    print(f"Launcher mode : {launcher_mode_used}" + (f" ({bootstrap_exe})" if bootstrap_exe else " (no --bootstrap-exe found; used SDK apphost)"))
    print(f"shared-ui/    : {format_size(directory_size_bytes(shared_ui_dir))}")
    print("-" * 72)
    print("Composable Suite Tool Folders (artifacts/portable/composable-suite/):")
    for tool in TOOLS:
        tdir = composable_root / tool.folder_name
        print(f"  {tool.folder_name:<22} {format_size(directory_size_bytes(tdir))}")

    print("-" * 72)
    print("Self-Sufficient Standalone Packages (artifacts/portable/standalone/):")
    for tool in TOOLS:
        if not tool.build_standalone_zip:
            continue
        sdir = standalone_root / tool.folder_name
        szip = standalone_root / f"{tool.folder_name}.zip"
        print(f"  {tool.folder_name + '/':<22} {format_size(directory_size_bytes(sdir))}")
        print(f"  {tool.folder_name + '.zip':<22} {format_size(szip.stat().st_size)}")
    print("=" * 72)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

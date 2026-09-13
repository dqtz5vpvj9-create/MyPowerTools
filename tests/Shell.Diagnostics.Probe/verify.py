"""Fault-inject separate disposable processes and inspect production diagnostic files."""
from __future__ import annotations

import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
import tempfile


def main() -> None:
    executable = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="mpt-diagnostics-test-") as temporary:
        root = Path(temporary)
        def run(mode: str, directory: Path, *, succeeds: bool = True):
            env = os.environ.copy()
            env["MPT_DIAGNOSTICS_DIRECTORY"] = str(directory)
            env["DOTNET_DbgEnableMiniDump"] = "0"
            env["COMPlus_DbgEnableMiniDump"] = "0"
            temp = root / "fallback-tmp"
            temp.mkdir(exist_ok=True)
            env["TMPDIR"] = str(temp)
            process = subprocess.run(["dotnet", str(executable), mode], env=env,
                                     capture_output=True, text=True, timeout=30)
            if succeeds and process.returncode != 0:
                raise AssertionError(f"{mode}: {process.returncode}\n{process.stdout}\n{process.stderr}")
            if not succeeds and process.returncode == 0:
                raise AssertionError(f"{mode} unexpectedly survived")
            rows = [json.loads(line) for line in process.stdout.splitlines() if line.startswith('{"logPath"')]
            assert len(rows) == 1, (mode, process.stdout, process.stderr)
            metadata = rows[0]
            log = Path(metadata["logPath"])
            events = [json.loads(line) for line in log.read_text().splitlines()]
            assert events[0]["eventName"] == "process.start", (mode, events)
            assert any(row.get("detail") == "probe.before-failure" for row in events), (mode, events)
            assert stat.S_IMODE(log.stat().st_mode) == 0o600
            return metadata, events, process

        directory = root / "clean"
        _, events, _ = run("clean", directory)
        assert not list(directory.glob("*.pending.json"))
        assert events[-1]["eventName"] == "process.exit"
        print("PASS clean shutdown removes only its running marker")

        directory = root / "recoverable"
        _, events, _ = run("recoverable", directory)
        error = next(row for row in events if row["eventName"] == "command.failed")
        assert "PROBE-OUTER" in error["exception"] and "PROBE-INNER" in error["exception"]
        assert "ThrowInner" in error["exception"] and "ThrowWithInner" in error["exception"]
        assert "never-log-this" not in json.dumps(events)
        assert any(row.get("detail") == "probe.after-recovery" for row in events)
        print("PASS contained command error keeps full inner stack, redacts credentials, and continues")

        for mode, expected in [("main", "main.unhandled"), ("thread", "runtime.unhandled"), ("ui", "ui.unhandled")]:
            directory = root / mode
            _, events, process = run(mode, directory, succeeds=False)
            error = next(row for row in events if row["eventName"] == expected)
            assert "PROBE-INNER" in error["exception"] and "ThrowWithInner" in error["exception"]
            assert "never-log-this" not in json.dumps(events)
            assert list(directory.glob("*.fatal.json"))
            assert len(list(directory.glob("*.pending.json"))) == 1
            metadata, _, _ = run("recovery-ui", directory)
            assert metadata["previous"] == 1
            assert not list(directory.glob("*.pending.json"))
            print(f"PASS {mode} exception: stack persisted before exit {process.returncode}; next-start notice opens")

        directory = root / "native"
        metadata, _, process = run("native-abort", directory, succeeds=False)
        assert process.returncode == -signal.SIGABRT, process.returncode
        assert "NATIVE-STDERR-BEFORE-ABORT" in Path(metadata["stderrPath"]).read_text()
        metadata, _, _ = run("inspect", directory)
        assert metadata["previous"] == 1
        print("PASS real libc abort: native fd 2 captured; unclean exit detected on next launch")

        directory = root / "kill"
        _, events, process = run("kill", directory, succeeds=False)
        assert process.returncode == -signal.SIGKILL, process.returncode
        assert not any(row["eventName"] == "process.exit" for row in events)
        metadata, _, _ = run("inspect", directory)
        assert metadata["previous"] == 1
        print("PASS SIGKILL: prior breadcrumb survives; no invented exception stack; next launch detects exit")

        _, events, _ = run("parallel", root / "parallel")
        assert len([row for row in events if row["eventName"] == "probe.parallel"]) == 512
        print("PASS concurrent logging: 512 complete JSON records")

        metadata, events, _ = run("clean", Path("/dev/null/not-a-directory"))
        assert Path(metadata["logPath"]).is_relative_to(root / "fallback-tmp")
        assert any(row["eventName"] == "storage.fallback" for row in events)
        print("PASS unwritable log directory falls back without preventing application startup")
        print("ALL 9 DIAGNOSTIC SCENARIOS PASSED")


if __name__ == "__main__":
    main()

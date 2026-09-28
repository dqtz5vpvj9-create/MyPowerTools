"""The service as a real process: CLI configuration, SIGTERM, restart persistence.

The in-process tests already prove the protocol; these tests prove the *deployment* shape —
``python3 -m mpt_relay`` on a fixed loopback port, a graceful systemd-style stop, and a
restart that keeps credentials, content and the revision counter.
"""

from __future__ import annotations

import hashlib
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

from relay_testkit import BASE_PATH, DAV_ROOT, RELAY_DIR, call, free_port

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"conversation-a").hexdigest()
CREDENTIALS = (CONVERSATION, CONVERSATION_KEY)
ITEM = "0123456789abcdef0123456789abcdef"


class RelayProcess:
    """A ``python3 -m mpt_relay`` child process on a fixed port."""

    def __init__(self, data_dir: Path, port: int, log_dir: Path, **environment: str) -> None:
        self.port = port
        self.log_path = log_dir / f"relay-process-{port}.log"
        self._log = self.log_path.open("w", encoding="utf-8")
        env = dict(os.environ)
        env["PYTHONPATH"] = str(RELAY_DIR)
        env["PYTHONUNBUFFERED"] = "1"
        env.update(environment)
        self.process = subprocess.Popen(
            [
                sys.executable,
                "-m",
                "mpt_relay",
                "--data-dir",
                str(data_dir),
                "--host",
                "127.0.0.1",
                "--port",
                str(port),
            ],
            cwd=str(RELAY_DIR),
            env=env,
            stdout=self._log,
            stderr=subprocess.STDOUT,
        )

    def wait_until_ready(self, timeout: float = 20.0) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise AssertionError(f"relay exited early:\n{self.log()}")
            try:
                response = call(self.port, "GET", f"{BASE_PATH}/health", timeout=1.0)
            except OSError:
                time.sleep(0.05)
                continue
            if response.status == 200:
                return
            time.sleep(0.05)
        raise AssertionError(f"relay did not become healthy:\n{self.log()}")

    def stop(self, sig: int = signal.SIGTERM, timeout: float = 10.0) -> int:
        if self.process.poll() is None:
            self.process.send_signal(sig)
        try:
            code = self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
            raise AssertionError("relay ignored SIGTERM")
        self._log.flush()
        self._log.close()
        return code

    def log(self) -> str:
        if not self._log.closed:
            self._log.flush()
        return self.log_path.read_text(encoding="utf-8", errors="replace")

    def __enter__(self) -> "RelayProcess":
        self.wait_until_ready()
        return self

    def __exit__(self, *_exc: object) -> None:
        self.stop()


class ProcessLifecycleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="mpt-relay-process-"))
        self.data_dir = self.root / "data"
        self.log_dir = self.root / "logs"
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self.port = free_port()
        self.addCleanup(self._cleanup)

    def _cleanup(self) -> None:
        for process in getattr(self, "_processes", []):
            if process.process.poll() is None:
                process.process.kill()
                process.process.wait(timeout=5)
            if not process._log.closed:
                process._log.close()

    def spawn(self, **environment: str) -> RelayProcess:
        process = RelayProcess(self.data_dir, self.port, self.log_dir, **environment)
        self._processes = getattr(self, "_processes", []) + [process]
        return process

    def test_restart_keeps_revision_credentials_and_content(self) -> None:
        process = self.spawn(MPT_RELAY_KDF_ITERATIONS="10000")
        process.wait_until_ready()

        self.assertEqual(201, call(self.port, "POST", f"{BASE_PATH}/v1/conversations", basic=CREDENTIALS).status)
        self.assertEqual(201, call(self.port, "MKCOL", f"{DAV_ROOT}assistant/", basic=CREDENTIALS).status)
        self.assertEqual(201, call(self.port, "MKCOL", f"{DAV_ROOT}assistant/{CONVERSATION}/", basic=CREDENTIALS).status)
        self.assertEqual(201, call(self.port, "MKCOL", f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS).status)
        self.assertEqual(
            201,
            call(self.port, "PUT", f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS, body=b"persisted").status,
        )
        self.assertEqual(
            201,
            call(
                self.port,
                "PUT",
                f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/manifest.json",
                basic=CREDENTIALS,
                body=json.dumps({"version": 1, "id": ITEM}).encode(),
            ).status,
        )
        self.assertEqual({"revision": 1}, call(self.port, "GET", f"{BASE_PATH}/v1/changes", basic=CREDENTIALS).json())

        self.assertEqual(0, process.stop(), "SIGTERM must be a clean exit")

        restarted = self.spawn(MPT_RELAY_KDF_ITERATIONS="10000")
        restarted.wait_until_ready()
        try:
            self.assertEqual({"revision": 1}, call(self.port, "GET", f"{BASE_PATH}/v1/changes", basic=CREDENTIALS).json())
            self.assertEqual(
                {"revision": 1},
                call(self.port, "GET", f"{BASE_PATH}/v1/changes?since=0", basic=CREDENTIALS).json(),
            )
            self.assertEqual(
                b"persisted",
                call(self.port, "GET", f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).body,
            )
            wrong = (CONVERSATION, hashlib.sha256(b"wrong").hexdigest())
            self.assertEqual(401, call(self.port, "GET", f"{BASE_PATH}/v1/changes", basic=wrong).status)
        finally:
            restarted.stop()

        # The process log never contains the key, even across a restart.
        combined = process.log() + restarted.log()
        self.assertNotIn(CONVERSATION_KEY, combined)
        self.assertIn("mpt-relay", combined)

    def test_cli_reports_version_and_effective_configuration(self) -> None:
        env = dict(os.environ, PYTHONPATH=str(RELAY_DIR))
        version = subprocess.run(
            [sys.executable, "-m", "mpt_relay", "--version"],
            cwd=str(RELAY_DIR),
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
        )
        self.assertEqual(0, version.returncode)
        self.assertRegex(version.stdout.strip(), r"^\d+\.\d+\.\d+$")

        printed = subprocess.run(
            [sys.executable, "-m", "mpt_relay", "--print-config"],
            cwd=str(RELAY_DIR),
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
        )
        self.assertEqual(0, printed.returncode)
        config = json.loads(printed.stdout)
        self.assertEqual("127.0.0.1", config["host"])
        self.assertEqual(18765, config["port"])
        self.assertEqual("/mpt/relay", config["base_path"])
        self.assertEqual(512 * 1024 * 1024, config["max_file_bytes"])
        self.assertEqual(8 * 1024 * 1024 * 1024, config["global_bytes"])

    def test_environment_file_is_honoured(self) -> None:
        config_file = self.root / "relay.env"
        config_file.write_text(
            "# operator settings\n"
            "MPT_RELAY_PER_CONVERSATION_BYTES=2097152\n"
            "MPT_RELAY_MAX_FILE_BYTES=1048576\n"
            'MPT_RELAY_LOG_LEVEL="warning"\n',
            encoding="utf-8",
        )
        env = dict(os.environ, PYTHONPATH=str(RELAY_DIR))
        printed = subprocess.run(
            [sys.executable, "-m", "mpt_relay", "--config", str(config_file), "--print-config"],
            cwd=str(RELAY_DIR),
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
        )
        self.assertEqual(0, printed.returncode, printed.stderr)
        config = json.loads(printed.stdout)
        self.assertEqual(2097152, config["per_conversation_bytes"])
        self.assertEqual(1048576, config["max_file_bytes"])
        self.assertEqual("warning", config["log_level"])

    def test_invalid_configuration_fails_loudly(self) -> None:
        env = dict(os.environ, PYTHONPATH=str(RELAY_DIR), MPT_RELAY_MAX_FILE_BYTES="999999999999")
        result = subprocess.run(
            [sys.executable, "-m", "mpt_relay", "--print-config"],
            cwd=str(RELAY_DIR),
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("configuration error", result.stderr)

    def test_deploy_smoke_script_passes_against_a_running_relay(self) -> None:
        """The operator's post-deploy script is itself exercised, over a real socket."""
        smoke = RELAY_DIR / "deploy" / "smoke.py"
        self.assertTrue(smoke.is_file())
        process = self.spawn(MPT_RELAY_KDF_ITERATIONS="10000", MPT_RELAY_LONGPOLL_MAX_SECONDS="3")
        process.wait_until_ready()
        try:
            result = subprocess.run(
                [sys.executable, str(smoke), "--base", f"http://127.0.0.1:{self.port}"],
                cwd=str(RELAY_DIR),
                capture_output=True,
                text=True,
                timeout=180,
            )
        finally:
            process.stop()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("all checks passed", result.stdout)
        self.assertNotIn("FAIL", result.stdout)


if __name__ == "__main__":
    unittest.main()

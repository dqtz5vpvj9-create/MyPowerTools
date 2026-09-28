"""Static guards for the deployment templates.

The deployment artefacts cannot be executed in this repository (no systemd, no nginx, no
root), so these tests pin the properties a reviewer must be able to check by reading:

* ``install.sh`` performs no deletion at all, and never a wildcard deletion,
* ``--enable`` verifies that the unit is active *and* healthy, and can fail,
* the nginx snippet only adds the ``/mpt/relay/`` location, keeps the prefix, disables the
  access log, streams uploads and allows the 25 second long poll,
* the unit runs as the unprivileged ``mpt-relay`` account on loopback with a writable data
  directory and no ``0.0.0.0`` bind.
"""

from __future__ import annotations

import ast
import re
import shutil
import subprocess
import unittest
from pathlib import Path

RELAY_DIR = Path(__file__).resolve().parents[1]
DEPLOY = RELAY_DIR / "deploy"
INSTALL = DEPLOY / "install.sh"
UNIT = DEPLOY / "mpt-relay.service"
NGINX = DEPLOY / "nginx-mpt-relay.conf"
ENV_EXAMPLE = DEPLOY / "relay.env.example"
SMOKE = DEPLOY / "smoke.py"


class InstallScriptTests(unittest.TestCase):
    def setUp(self) -> None:
        self.text = INSTALL.read_text(encoding="utf-8")

    @staticmethod
    def commands(text: str):
        for line in text.splitlines():
            stripped = line.strip()
            if stripped and not stripped.startswith("#"):
                yield stripped

    def test_posix_shell_syntax(self) -> None:
        shell = shutil.which("sh")
        if shell is None:  # pragma: no cover - the test image always has /bin/sh
            self.skipTest("no /bin/sh available")
        result = subprocess.run([shell, "-n", str(INSTALL)], capture_output=True, text=True, timeout=30)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_script_never_deletes_anything(self) -> None:
        for command in self.commands(self.text):
            self.assertIsNone(re.search(r"(^|[;&|(]\s*)rm\s", command), f"install.sh must not delete: {command!r}")
            self.assertNotIn("rm -rf", command)
            self.assertNotIn("rm -f", command)
            self.assertIsNone(re.search(r"find\b.*(-delete|-exec\s+rm)", command), command)
            self.assertNotIn("-delete", command)

    def test_no_data_path_is_ever_copied_or_moved(self) -> None:
        for command in self.commands(self.text):
            if command.startswith(("cp ", "mv ")):
                self.assertNotIn("/var/lib/mpt-relay", command, command)
                self.assertNotIn("/etc/mpt-relay", command, command)

    def test_upgrade_moves_the_previous_tree_aside(self) -> None:
        self.assertIn("rollback", self.text)
        self.assertIn('mv "$CODE_DIR/$fixed" "$CODE_DIR/rollback/$fixed-$STAMP"', self.text)
        # Only the two fixed, literal path names are ever swapped.
        self.assertIn("for fixed in mpt_relay deploy; do", self.text)

    def uninstall_section(self) -> str:
        return self.text.split("# ---------------------------------------------------------------------------- uninstall", 1)[1].split(
            "# ------------------------------------------------------------------------- user and dirs", 1
        )[0]

    def test_uninstall_moves_instead_of_removing(self) -> None:
        uninstall = self.uninstall_section()
        self.assertIn("mv ", uninstall)
        self.assertIn("removed-", uninstall)

    def test_enable_verifies_active_and_health_and_can_fail(self) -> None:
        enable = self.text.split("# ------------------------------------------------------------------------------ enable", 1)[1]
        self.assertIn("systemctl is-active", self.text)
        self.assertIn("systemctl restart", enable)
        self.assertIn("health", enable)
        self.assertIn("raise SystemExit(1)", enable)
        self.assertIn("exit 1", enable)
        # The verification result must not be swallowed by a trailing "|| true".
        for line in enable.splitlines():
            stripped = line.strip()
            if "is-active" in stripped or "health check failed" in stripped:
                self.assertNotIn("|| true", stripped, stripped)

    def test_uninstall_only_disables_an_installed_unit(self) -> None:
        uninstall = self.uninstall_section()
        self.assertIn('[ -f "$UNIT" ]', uninstall)


class UnitFileTests(unittest.TestCase):
    def setUp(self) -> None:
        self.text = UNIT.read_text(encoding="utf-8")

    def test_runs_as_the_unprivileged_service_account(self) -> None:
        self.assertIn("User=mpt-relay", self.text)
        self.assertIn("Group=mpt-relay", self.text)
        self.assertIn("WorkingDirectory=/opt/mpt-relay", self.text)
        self.assertIn("MPT_RELAY_DATA_DIR=/var/lib/mpt-relay", self.text)
        self.assertIn("MPT_RELAY_HOST=127.0.0.1", self.text)
        self.assertIn("MPT_RELAY_PORT=18765", self.text)
        self.assertNotIn("0.0.0.0", self.text)

    def test_hardening_and_data_write_path(self) -> None:
        self.assertIn("ReadWritePaths=/var/lib/mpt-relay", self.text)
        self.assertIn("ProtectSystem=strict", self.text)
        self.assertIn("NoNewPrivileges=yes", self.text)
        self.assertIn("Restart=on-failure", self.text)
        self.assertIn("ExecStart=/usr/bin/python3 -m mpt_relay", self.text)


class NginxSnippetTests(unittest.TestCase):
    def setUp(self) -> None:
        self.text = NGINX.read_text(encoding="utf-8")
        self.directives = [
            line.strip()
            for line in self.text.splitlines()
            if line.strip() and not line.strip().startswith("#")
        ]

    def test_only_adds_the_relay_location(self) -> None:
        self.assertEqual(1, sum(1 for directive in self.directives if directive.startswith("location ")))
        self.assertIn("location /mpt/relay/ {", self.directives)
        self.assertIn("proxy_pass http://127.0.0.1:18765;", self.text)
        # No server block, no upstream, no other location: nothing existing is rewritten.
        for directive in self.directives:
            self.assertNotIn("server_name", directive, directive)
            self.assertNotIn("server {", directive, directive)
            self.assertNotIn("headscale", directive.lower(), directive)

    def test_streaming_long_poll_and_no_credential_logging(self) -> None:
        self.assertIn("proxy_request_buffering off;", self.directives)
        self.assertIn("proxy_buffering off;", self.directives)
        self.assertIn("access_log off;", self.directives)
        timeout = re.search(r"proxy_read_timeout\s+(\d+)s;", self.text)
        self.assertIsNotNone(timeout)
        self.assertGreater(int(timeout.group(1)), 25, "the long poll needs more than its 25 s cap")
        # Credentials are forwarded but never written to a log format.
        for directive in self.directives:
            if "log_format" in directive or "access_log" in directive:
                self.assertNotIn("$http_authorization", directive, directive)
                self.assertNotIn("$request", directive.replace("$request_method", "").replace("$request_time", ""), directive)


def _render(value: object) -> str:
    """Renders a default the way relay.env.example spells it."""
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, (tuple, list)):
        return ",".join(str(item) for item in value)
    return str(value)


class ConfigExampleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.text = ENV_EXAMPLE.read_text(encoding="utf-8")

    def test_defaults_match_the_documented_production_posture(self) -> None:
        self.assertIn("MPT_RELAY_HOST=127.0.0.1", self.text)
        self.assertIn("MPT_RELAY_PORT=18765", self.text)
        self.assertIn("MPT_RELAY_PER_CONVERSATION_BYTES=1073741824", self.text)
        self.assertIn("MPT_RELAY_GLOBAL_BYTES=8589934592", self.text)
        self.assertIn("MPT_RELAY_MAX_FILE_BYTES=536870912", self.text)
        # First-up posture: no namespace from an unauthenticated DAV request.
        self.assertIn("MPT_RELAY_DAV_AUTO_REGISTER=0", self.text)
        self.assertIn("MPT_RELAY_BODY_BUDGET_SECONDS=", self.text)

    def test_example_values_match_the_shipped_defaults(self) -> None:
        from dataclasses import fields

        from mpt_relay.config import Config

        defaults = Config.load([], {})
        known = {field.name for field in fields(Config)}
        seen = set()
        for line in self.text.splitlines():
            line = line.strip()
            if not line.startswith("MPT_RELAY_") or "=" not in line:
                continue
            raw_name, raw_value = line.split("=", 1)
            name = raw_name[len("MPT_RELAY_") :].lower()
            self.assertIn(name, known, f"{line} is not a configuration key")
            seen.add(name)
            # The example documents the real defaults, so a drift is a test failure.
            self.assertEqual(
                _render(getattr(defaults, name)),
                raw_value.strip(),
                f"{name} in relay.env.example differs from the built-in default",
            )
        self.assertIn("dav_auto_register", seen)
        self.assertIn("body_budget_seconds", seen)


class SmokeScriptTests(unittest.TestCase):
    def test_smoke_script_is_python_310_compatible(self) -> None:
        source = SMOKE.read_text(encoding="utf-8")
        ast.parse(source, filename=str(SMOKE), feature_version=(3, 10))

    def test_installer_copies_only_the_relay_sources(self) -> None:
        text = INSTALL.read_text(encoding="utf-8")
        self.assertIn('cp -R "$SOURCE_DIR/mpt_relay/." "$STAGE/mpt_relay/"', text)
        self.assertIn('cp -R "$SOURCE_DIR/deploy/." "$STAGE/deploy/"', text)


if __name__ == "__main__":
    unittest.main()

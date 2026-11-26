#!/usr/bin/env python3
"""Run the relay test suite and keep a machine-readable log in the allowed scratch area.

Usage (from anywhere)::

    python3 tools/file-transfer/relay/tests/run.py
    python3 tools/file-transfer/relay/tests/run.py test_relay_dav QuotaTests

The log lands in ``artifacts/.tmp-android-verify/public-relay-server/`` (declared as
``.tmp-*`` scratch in ``scripts/artifacts-policy.json``), so a run leaves evidence without
touching any shared build output.
"""

from __future__ import annotations

import io
import os
import sys
import time
import unittest
from datetime import datetime, timezone
from pathlib import Path

RELAY_DIR = Path(__file__).resolve().parents[1]
TESTS_DIR = Path(__file__).resolve().parent
REPO_ROOT = Path(__file__).resolve().parents[4]
LOG_DIR = REPO_ROOT / "artifacts" / ".tmp-android-verify" / "public-relay-server"

sys.path.insert(0, str(RELAY_DIR))
sys.path.insert(0, str(TESTS_DIR))


class _Tee(io.TextIOBase):
    def __init__(self, *streams: io.TextIOBase) -> None:
        self._streams = streams

    def write(self, data: str) -> int:
        for stream in self._streams:
            try:
                stream.write(data)
                stream.flush()
            except ValueError:  # a stream was closed while the suite was still writing
                continue
        return len(data)

    def flush(self) -> None:
        for stream in self._streams:
            try:
                stream.flush()
            except ValueError:
                continue


def iter_tests(suite: unittest.TestSuite):
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from iter_tests(item)
        else:
            yield item


def main(argv: list) -> int:
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    log_path = LOG_DIR / f"relay-tests-{stamp}.log"

    loader = unittest.TestLoader()
    discovered = unittest.TestSuite(iter_tests(loader.discover(str(TESTS_DIR), pattern="test_*.py")))
    if argv:
        needles = [argument.lower() for argument in argv]
        discovered = unittest.TestSuite(
            test for test in iter_tests(discovered) if any(needle in test.id().lower() for needle in needles)
        )
    suite = discovered

    started = time.monotonic()
    with log_path.open("w", encoding="utf-8") as log:
        log.write(f"# mpt-relay test run {stamp}\n")
        log.write(f"# python {sys.version.split()[0]}  cwd={os.getcwd()}\n\n")
        runner = unittest.TextTestRunner(stream=_Tee(sys.stdout, log), verbosity=2)
        result = runner.run(suite)
        elapsed = time.monotonic() - started
        log.write(
            f"\n# ran={result.testsRun} failures={len(result.failures)} errors={len(result.errors)} "
            f"skipped={len(result.skipped)} seconds={elapsed:.1f}\n"
        )
    print(f"\nlog: {log_path}")
    print(
        f"ran={result.testsRun} failures={len(result.failures)} errors={len(result.errors)} "
        f"skipped={len(result.skipped)} in {elapsed:.1f}s"
    )
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

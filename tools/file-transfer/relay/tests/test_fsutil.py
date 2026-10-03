"""Durable uploads must flush successfully on Windows as well as POSIX."""

import tempfile
import unittest
from pathlib import Path

from mpt_relay.fsutil import fsync_file


class UploadDurabilityTests(unittest.TestCase):
    def test_flush_preserves_uploaded_bytes(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "payload"
            contents = bytes(range(256)) * 1024
            path.write_bytes(contents)
            fsync_file(path)
            self.assertEqual(contents, path.read_bytes())

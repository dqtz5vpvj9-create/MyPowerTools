"""Small filesystem helpers shared by the WebDAV and inbox writers.

Every mutation in this service follows the same shape: write a hidden temp file in the
destination directory, fsync it, rename it into place, fsync the directory. A cancelled or
failed request therefore never leaves a visible partial file behind.
"""

from __future__ import annotations

import os
from pathlib import Path

#: Prefix of an in-flight upload; invisible to every read path and swept on start-up.
TEMP_PREFIX = ".mpt-relay-upload-"
FSYNC_WRITES = True


def temp_name() -> str:
    return TEMP_PREFIX + os.urandom(8).hex()


def fsync_file(path: Path) -> None:
    if not FSYNC_WRITES:
        return
    # Windows FlushFileBuffers requires a writable handle. The upload is owned
    # by this service; opening it read/write preserves its contents on every OS.
    descriptor = os.open(path, os.O_RDWR)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def fsync_directory(path: Path) -> None:
    if not FSYNC_WRITES:
        return
    try:
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    except OSError:
        return
    try:
        os.fsync(descriptor)
    except OSError:
        pass
    finally:
        os.close(descriptor)


def unlink_quietly(path: Path) -> None:
    try:
        os.unlink(path)
    except OSError:
        pass


def write_atomic(path: Path, data: bytes) -> None:
    """Writes a small file (manifest, metadata, receipt) atomically."""
    temporary = path.parent / temp_name()
    try:
        with open(temporary, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        fsync_directory(path.parent)
    except BaseException:
        unlink_quietly(temporary)
        raise

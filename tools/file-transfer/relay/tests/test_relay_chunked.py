"""Chunked request bodies: bounded reads, strict framing, no client-sized allocation.

``Transfer-Encoding: chunked`` lets the client declare a chunk length. That number is
untrusted input: a length of ``ffffffff`` must cost one bounded read, never a 4 GiB
``read()``. These tests use a spy reader instead of allocating gigabytes, plus raw-socket
requests for every malformed framing the parser must refuse.
"""

from __future__ import annotations

import hashlib
import io
import socket
import tempfile
import time
import unittest
from pathlib import Path

from relay_testkit import DAV_ROOT, RelayHarness

from mpt_relay.httpd import UPLOAD_CHUNK, BodyError, HttpBody

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"conversation-a").hexdigest()
CREDENTIALS = (CONVERSATION, CONVERSATION_KEY)
ITEM = "0123456789abcdef0123456789abcdef"


class SpyReader:
    """A file-like object that records the size of every read ask."""

    def __init__(self, data: bytes) -> None:
        self._buffer = io.BytesIO(data)
        self.reads: list = []
        self.lines: list = []

    def read(self, size: int = -1) -> bytes:
        self.reads.append(size)
        return self._buffer.read(size)

    def read1(self, size: int = -1) -> bytes:
        """Single-read semantics, like a BufferedReader over a socket."""
        self.reads.append(size)
        return self._buffer.read(size)

    def readline(self, size: int = -1) -> bytes:
        self.lines.append(size)
        return self._buffer.readline(size)


class FakeHeaders(dict):
    def get(self, key, default=None):  # noqa: D102 - mirrors http.client.HTTPMessage
        return super().get(key.lower(), default)


class FakeHandler:
    def __init__(self, data: bytes, transfer_encoding: str = "chunked") -> None:
        self.rfile = SpyReader(data)
        self.headers = FakeHeaders({"transfer-encoding": transfer_encoding})


def chunked_body(payload: bytes, chunk_header: bytes = b"ffffffff") -> bytes:
    """A single declared chunk (default 4 GiB) carrying only ``payload`` bytes."""
    return chunk_header + b"\r\n" + payload + b"\r\n0\r\n\r\n"


class BoundedReadTests(unittest.TestCase):
    """No matter what the client declares, one read ask stays within the buffer bound."""

    def test_huge_declared_chunk_is_read_in_bounded_pieces(self) -> None:
        payload = b"A" * (3 * UPLOAD_CHUNK + 1234)
        handler = FakeHandler(chunked_body(payload))
        body = HttpBody(handler)  # type: ignore[arg-type]
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "payload"
            with self.assertRaises(BodyError):
                # The declared chunk is 4 GiB but the connection ends after 192 KiB, which the
                # parser must report as a truncated body rather than treat as a valid upload.
                body.stream_to(target, lambda written: None)
            # The temp file only holds what was actually sent (plus a few framing bytes), never
            # anything sized by the 4 GiB claim.
            written = target.stat().st_size
            self.assertGreaterEqual(written, len(payload))
            self.assertLessEqual(written, len(payload) + 16)
            self.assertFalse(body.consumed())

        self.assertTrue(handler.rfile.reads, "the body was never read")
        self.assertLessEqual(max(handler.rfile.reads), UPLOAD_CHUNK)
        self.assertLessEqual(max(handler.rfile.reads), 128 * 1024)
        self.assertGreaterEqual(min(handler.rfile.reads), 1)
        # The number of read asks follows the bytes actually sent, not the 4 GiB claim.
        self.assertLessEqual(len(handler.rfile.reads), len(payload) // UPLOAD_CHUNK + 8)

    def test_declared_chunk_larger_than_python_int_is_refused_before_reading(self) -> None:
        handler = FakeHandler(b"f" * 40 + b"\r\n" + b"payload\r\n0\r\n\r\n")
        body = HttpBody(handler)  # type: ignore[arg-type]
        with self.assertRaises(BodyError):
            body.read_all(1024)
        # No read ask is ever sized by the declared chunk; framing is read in LINE_CHUNK pieces.
        self.assertTrue(handler.rfile.reads, "the length line was never read")
        self.assertLessEqual(max(handler.rfile.reads), 1024)

    def test_quota_callback_sees_every_piece_before_the_next_read(self) -> None:
        from mpt_relay.store import QuotaExceeded

        payload = b"B" * (4 * UPLOAD_CHUNK)
        handler = FakeHandler(chunked_body(payload))
        body = HttpBody(handler)  # type: ignore[arg-type]
        seen: list = []

        def on_bytes(written: int) -> None:
            seen.append(written)
            if written > 2 * UPLOAD_CHUNK:
                raise QuotaExceeded("配额不足")

        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(QuotaExceeded):
                body.stream_to(Path(directory) / "payload", on_bytes)
        self.assertEqual(sorted(seen), seen)
        # The upload stopped inside the third piece instead of reading the whole 4 GiB claim.
        self.assertLessEqual(sum(handler.rfile.reads), 4 * UPLOAD_CHUNK)

    def test_terminating_chunk_and_trailers_complete_the_body(self) -> None:
        handler = FakeHandler(b"5\r\nhello\r\n0\r\n\r\n")
        body = HttpBody(handler)  # type: ignore[arg-type]
        self.assertEqual(b"hello", body.read_all(1024))
        self.assertTrue(body.consumed())

    def test_small_chunks_are_reassembled_across_reads(self) -> None:
        handler = FakeHandler(b"1\r\na\r\n2\r\nbc\r\n0\r\n\r\n")
        body = HttpBody(handler)  # type: ignore[arg-type]
        self.assertEqual(b"abc", body.read_all(1024))

    def test_extensions_and_bounded_trailers_are_accepted(self) -> None:
        handler = FakeHandler(b"5;name=value\r\nhello\r\n0\r\nX-Checksum: abc\r\n\r\n")
        body = HttpBody(handler)  # type: ignore[arg-type]
        self.assertEqual(b"hello", body.read_all(1024))
        self.assertTrue(body.consumed())


class MalformedChunkTests(unittest.TestCase):
    """Each framing error is a 400 with nothing left behind in the namespace."""

    def setUp(self) -> None:
        self.relay = RelayHarness(per_conversation_bytes=1 << 20, max_file_bytes=1 << 20)
        self.addCleanup(self.relay.stop)
        self.assertEqual(201, self.relay.conversations(CREDENTIALS).status)
        self.relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
        self.relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
        self.relay.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS)

    def send_chunked(self, raw: bytes, *, expect_reply: bool = True, half_close: bool = True) -> bytes:
        import base64

        connection = self.relay.raw_connection(timeout=5)
        try:
            token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
            connection.sendall(
                (
                    f"PUT {DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload HTTP/1.1\r\n"
                    f"Host: 127.0.0.1:{self.relay.port}\r\n"
                    f"Authorization: Basic {token}\r\n"
                    f"Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                ).encode("ascii")
            )
            try:
                connection.sendall(raw)
            except (BrokenPipeError, ConnectionResetError):
                pass  # the server may already have answered and closed
            # Half-close: a truncated body must be reported as an EOF by the parser right away
            # instead of leaving the server (and this test) waiting for more bytes.
            if half_close:
                try:
                    connection.shutdown(socket.SHUT_WR)
                except OSError:
                    pass
            if not expect_reply:
                return b""
            try:
                reply = connection.recv(4096)
            except (socket.timeout, ConnectionResetError):
                reply = b""
            if not reply:
                import time

                time.sleep(0.2)
                try:
                    reply = connection.recv(4096)
                except (socket.timeout, ConnectionResetError):
                    reply = b""
            return reply
        finally:
            connection.close()

    def assert_rejected(self, raw: bytes, label: str) -> None:
        started = time.monotonic()
        reply = self.send_chunked(raw)
        self.assertLess(time.monotonic() - started, 5.0, f"{label}: the parser did not fail fast")
        status = reply.split(b"\r\n", 1)[0] if reply else b"<no response>"
        self.assertTrue(
            b" 400 " in status or not reply,
            f"{label}: expected a 400 rejection, got {status!r}",
        )
        self.assertEqual(404, self.relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status, label)
        self.assertTrue(
            self.relay.wait_for(lambda: not list(self.relay.data_dir.rglob(".mpt-relay-upload-*"))),
            f"{label}: the aborted upload left a temp file behind",
        )

    def test_negative_chunk_length(self) -> None:
        # int("-1", 16) is -1 in Python: it must never reach read().
        self.assert_rejected(b"-1\r\nAAAA\r\n0\r\n\r\n", "negative chunk length")

    def test_non_hexadecimal_chunk_length(self) -> None:
        self.assert_rejected(b"zz\r\nAAAA\r\n0\r\n\r\n", "non-hex chunk length")
        self.assert_rejected(b"0x10\r\nAAAA\r\n0\r\n\r\n", "0x prefixed chunk length")
        self.assert_rejected(b"+5\r\nAAAAA\r\n0\r\n\r\n", "signed chunk length")
        self.assert_rejected(b" \r\nAAAA\r\n0\r\n\r\n", "empty chunk length")

    def test_chunk_length_line_without_crlf(self) -> None:
        self.assert_rejected(b"5\nhello\r\n0\r\n\r\n", "bare LF length line")

    def test_oversized_chunk_length_line(self) -> None:
        self.assert_rejected(b"f" * 200 + b"\r\nAAAA\r\n0\r\n\r\n", "oversized length line")
        self.assert_rejected(b"5;" + b"x" * 200 + b"\r\nhello\r\n0\r\n\r\n", "oversized extension line")

    def test_unbounded_trailers(self) -> None:
        trailers = b"".join(b"X-Filler: " + b"y" * 60 + b"\r\n" for _ in range(400))
        self.assert_rejected(b"2\r\nhi\r\n0\r\n" + trailers + b"\r\n", "unbounded trailers")

    def test_missing_payload_crlf(self) -> None:
        self.assert_rejected(b"5\r\nhello0\r\n\r\n", "payload without trailing CRLF")

    def test_truncated_body(self) -> None:
        self.assert_rejected(b"5\r\nhel", "truncated chunk payload")
        self.assert_rejected(b"ffffffff\r\npartial", "truncated payload of a huge claim")

    def test_truncated_chunk_length_line(self) -> None:
        self.assert_rejected(b"1f", "length line cut off at EOF")

    def test_oversized_declared_chunk_is_refused_by_quota(self) -> None:
        """A 4 GiB chunk claim is refused by the file-size quota after a bounded read."""
        # The namespace limit is 1 MiB in this harness, so a 2 MiB chunk trips 413 on the third
        # 64 KiB piece; nothing is sized by the declared 4 GiB.
        reply = self.send_chunked(b"ffffffff\r\n" + b"C" * (2 * 1024 * 1024), half_close=False)
        status = reply.split(b"\r\n", 1)[0] if reply else b"<no response>"
        self.assertIn(b" 413 ", status)
        self.assertEqual(404, self.relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)

    def test_valid_chunked_upload_still_works(self) -> None:
        reply = self.send_chunked(b"5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n")
        self.assertIn(b" 201 ", reply)
        self.assertEqual(b"hello world", self.relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).body)


class HugeChunkHttpTests(unittest.TestCase):
    """The HTTP layer keeps the same bound for a real request, without allocating gigabytes."""

    def test_giant_chunk_claim_is_streamed_and_quota_checked(self) -> None:
        with RelayHarness(per_conversation_bytes=64 * 1024, max_file_bytes=64 * 1024) as relay:
            self.assertEqual(201, relay.conversations(CREDENTIALS).status)
            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS)

            import base64

            connection = relay.raw_connection(timeout=20)
            try:
                token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
                head = (
                    f"PUT {DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload HTTP/1.1\r\n"
                    f"Host: 127.0.0.1:{relay.port}\r\nAuthorization: Basic {token}\r\n"
                    f"Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                    "ffffffff\r\n"
                )
                connection.sendall(head.encode("ascii"))
                # 256 KiB of a 4 GiB claim: enough to cross the 64 KiB quota, far too little to
                # matter if the server had tried to honour the declared length.
                for _ in range(4):
                    connection.sendall(b"D" * (64 * 1024))
                reply = connection.recv(4096)
            finally:
                connection.close()
            self.assertIn(b" 413 ", reply)
            self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)
            self.assertTrue(relay.wait_for(lambda: not list(relay.data_dir.rglob(".mpt-relay-upload-*"))))


if __name__ == "__main__":
    unittest.main()

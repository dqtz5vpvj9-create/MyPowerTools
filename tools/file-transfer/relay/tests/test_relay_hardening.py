"""Regression tests for the review findings: quota reservation, body time budget, bounded limiters.

* global quota is *reserved* before an upload body is read, so two conversations cannot both
  pass the check and then both commit past the global ceiling,
* the request body has a wall-clock budget, so a slow-drip client cannot hold a connection
  slot forever (while a long poll, which has no body, is untouched),
* the abuse trackers keep a hard key ceiling and evict the oldest key instead of scanning.
"""

from __future__ import annotations

import base64
import hashlib
import socket
import threading
import time
import unittest

from relay_testkit import DAV_ROOT, RelayHarness

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"conversation-a").hexdigest()
CREDENTIALS = (CONVERSATION, CONVERSATION_KEY)
OTHER = "self-bbbb2222"
OTHER_CREDENTIALS = (OTHER, hashlib.sha256(b"conversation-b").hexdigest())
ITEM = "0123456789abcdef0123456789abcdef"


def prepare(relay: RelayHarness, credentials, item: str = ITEM) -> None:
    relay.conversations(credentials)
    relay.dav("MKCOL", "assistant/", basic=credentials)
    relay.dav("MKCOL", f"assistant/{credentials[0]}/", basic=credentials)
    relay.dav("MKCOL", f"assistant/{credentials[0]}/{item}/", basic=credentials)


def raw_put(relay: RelayHarness, credentials, conversation_id: str, declared: int, item: str = ITEM) -> socket.socket:
    connection = relay.raw_connection(timeout=30)
    token = base64.b64encode(f"{conversation_id}:{credentials[1]}".encode()).decode()
    connection.sendall(
        (
            f"PUT {DAV_ROOT}assistant/{conversation_id}/{item}/payload HTTP/1.1\r\n"
            f"Host: 127.0.0.1:{relay.port}\r\n"
            f"Authorization: Basic {token}\r\n"
            f"Content-Length: {declared}\r\n"
            f"Connection: close\r\n\r\n"
        ).encode("ascii")
    )
    return connection


class GlobalQuotaReservationTests(unittest.TestCase):
    def test_concurrent_uploads_cannot_both_pass_the_global_check(self) -> None:
        limit = 2000
        with RelayHarness(per_conversation_bytes=limit, max_file_bytes=limit, global_bytes=limit) as relay:
            prepare(relay, CREDENTIALS)
            prepare(relay, OTHER_CREDENTIALS)

            body = b"x" * 1500
            # Both requests declare 1500 bytes and send only their headers first: whichever
            # reserves first wins, the other must be refused before its body is read.
            first = raw_put(relay, CREDENTIALS, CONVERSATION, len(body))
            second = raw_put(relay, OTHER_CREDENTIALS, OTHER, len(body))
            time.sleep(0.5)  # both headers are parsed, so both reservations have been attempted
            try:
                first.sendall(body)
                second.sendall(body)
                replies = []
                for connection in (first, second):
                    try:
                        replies.append(connection.recv(4096).split(b"\r\n", 1)[0])
                    except (socket.timeout, ConnectionResetError):
                        replies.append(b"")
            finally:
                first.close()
                second.close()

            codes = sorted(int(reply.split(b" ")[1]) for reply in replies if reply)
            self.assertEqual(1, sum(1 for code in codes if code == 201), f"expected exactly one winner: {replies}")
            self.assertEqual(1, sum(1 for code in codes if code == 507), f"expected exactly one 507: {replies}")
            self.assertLessEqual(relay.store.global_usage(), limit)
            self.assertEqual(1500, relay.store.global_usage())
            self.assertEqual(0, relay.store.global_reserved())

    def test_reservation_is_released_when_the_upload_is_cancelled(self) -> None:
        with RelayHarness(per_conversation_bytes=4000, max_file_bytes=4000, global_bytes=4000) as relay:
            prepare(relay, CREDENTIALS)
            connection = raw_put(relay, CREDENTIALS, CONVERSATION, 3000)
            try:
                time.sleep(0.4)
                self.assertEqual(3000, relay.store.global_reserved(), "the declared body must be reserved up front")
            finally:
                connection.close()
            self.assertTrue(relay.wait_for(lambda: relay.store.global_reserved() == 0), "the claim was never released")
            self.assertEqual(0, relay.store.global_usage())
            # The freed quota is usable again immediately.
            self.assertEqual(201, relay.dav("PUT", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS, body=b"y" * 3000).status)

    def test_undeclared_chunked_body_reserves_as_it_grows(self) -> None:
        with RelayHarness(per_conversation_bytes=1000, max_file_bytes=1000, global_bytes=1000) as relay:
            prepare(relay, CREDENTIALS)
            connection = relay.raw_connection(timeout=20)
            token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
            try:
                connection.sendall(
                    (
                        f"PUT {DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload HTTP/1.1\r\n"
                        f"Host: 127.0.0.1:{relay.port}\r\nAuthorization: Basic {token}\r\n"
                        f"Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                    ).encode("ascii")
                )
                for _ in range(4):
                    connection.sendall(b"400\r\n" + b"z" * 1024 + b"\r\n")
                reply = connection.recv(4096)
            finally:
                connection.close()
            self.assertIn(b" 413 ", reply)
            self.assertTrue(relay.wait_for(lambda: relay.store.global_reserved() == 0))
            self.assertEqual(0, relay.store.global_usage())
            self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)


class MkcolQuotaTests(unittest.TestCase):
    def test_full_namespace_mkcol_answers_507_not_500(self) -> None:
        with RelayHarness(max_entries_per_conversation=3) as relay:
            self.assertEqual(201, relay.conversations(CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", "assistant/", basic=CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS).status)
            blocked = relay.dav("MKCOL", f"assistant/{CONVERSATION}/{'f' * 32}/", basic=CREDENTIALS)
            self.assertEqual(507, blocked.status)
            self.assertNotEqual(b"", blocked.body)
            # The refusal is clean: no partial collection, and the existing tree still lists.
            listing = relay.dav("PROPFIND", f"assistant/{CONVERSATION}/", basic=CREDENTIALS, headers={"Depth": "1"})
            self.assertEqual(207, listing.status)
            self.assertEqual(1, listing.text().count("<D:response>") - 1)

    def test_full_namespace_put_answers_507(self) -> None:
        with RelayHarness(max_entries_per_conversation=3) as relay:
            self.assertEqual(201, relay.conversations(CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", "assistant/", basic=CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS).status)
            response = relay.dav("PUT", f"assistant/{CONVERSATION}/{ITEM}/manifest.json", basic=CREDENTIALS, body=b"{}")
            self.assertEqual(507, response.status)


class RequestBodyBudgetTests(unittest.TestCase):
    """The body budget is a wall clock over the *whole* body, not a per-recv idle timeout."""

    DRIP_INTERVAL = 0.2
    DRIP_SECONDS = 6.0

    def sustained_drip(self, connection, chunk: bytes, interval: float = DRIP_INTERVAL, seconds: float = DRIP_SECONDS):
        """Drips bytes until the server closes or ``seconds`` elapse; returns the drip window."""
        started = time.monotonic()
        while time.monotonic() - started < seconds:
            try:
                connection.sendall(chunk)
            except OSError:
                break
            time.sleep(interval)
        return time.monotonic() - started

    def assert_cut_off_near_budget(self, connection, budget: float, drip, label: str, expected: tuple = (b" 400 ",)) -> float:
        """Waits for the server's answer while a background drip keeps the socket busy."""
        worker = threading.Thread(target=drip, daemon=True)
        worker.start()
        started = time.monotonic()
        try:
            reply = connection.recv(4096)
        except (socket.timeout, ConnectionResetError):
            reply = b""
        elapsed = time.monotonic() - started
        worker.join(timeout=3)
        self.assertLess(
            elapsed,
            budget + 2.0,
            f"{label}: the drip held the connection for {elapsed:.2f}s with a {budget:.0f}s budget",
        )
        self.assertTrue(
            any(marker in reply for marker in expected) or not reply,
            f"{label}: expected one of {expected}, got {reply[:60]!r}",
        )
        return elapsed

    def test_sustained_drip_is_cut_off_at_the_budget(self) -> None:
        """A drip that never stops must still be terminated by the 1 s budget."""
        with RelayHarness(body_budget_seconds=1, socket_timeout_seconds=60) as relay:
            prepare(relay, CREDENTIALS)
            connection = raw_put(relay, CREDENTIALS, CONVERSATION, 4096)
            try:
                elapsed = self.assert_cut_off_near_budget(
                    connection, 1.0, lambda: self.sustained_drip(connection, b"d"), "content-length drip"
                )
                self.assertGreaterEqual(elapsed, 0.2, "the budget must not cut a fast body immediately")
            finally:
                connection.close()
            self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)
            self.assertTrue(relay.wait_for(lambda: not list(relay.data_dir.rglob(".mpt-relay-upload-*"))))

    def test_sustained_drip_inside_a_chunk_length_line_is_cut_off(self) -> None:
        with RelayHarness(body_budget_seconds=1, socket_timeout_seconds=60) as relay:
            prepare(relay, CREDENTIALS)
            connection = relay.raw_connection(timeout=30)
            try:
                token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
                connection.sendall(
                    (
                        f"PUT {DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload HTTP/1.1\r\n"
                        f"Host: 127.0.0.1:{relay.port}\r\nAuthorization: Basic {token}\r\n"
                        f"Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                    ).encode("ascii")
                )
                self.assert_cut_off_near_budget(
                    connection, 1.0, lambda: self.sustained_drip(connection, b"f"), "chunk length line drip"
                )
            finally:
                connection.close()
            self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)

    def test_sustained_drip_inside_trailers_is_cut_off(self) -> None:
        with RelayHarness(body_budget_seconds=1, socket_timeout_seconds=60) as relay:
            prepare(relay, CREDENTIALS)
            connection = relay.raw_connection(timeout=30)
            try:
                token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
                connection.sendall(
                    (
                        f"PUT {DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/payload HTTP/1.1\r\n"
                        f"Host: 127.0.0.1:{relay.port}\r\nAuthorization: Basic {token}\r\n"
                        f"Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                        "5\r\nhello\r\n0\r\nX-Trailer: "
                    ).encode("ascii")
                )
                self.assert_cut_off_near_budget(
                    connection, 1.0, lambda: self.sustained_drip(connection, b"t"), "trailer drip"
                )
            finally:
                connection.close()
            # The truncated trailer means the item was never published.
            self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)

    def test_repeated_drip_without_stopping_does_not_outlive_the_budget(self) -> None:
        """Regression for the reported 5.81 s: 25 drips over 5 s must not extend the budget."""
        with RelayHarness(body_budget_seconds=1, socket_timeout_seconds=60) as relay:
            prepare(relay, CREDENTIALS)
            connection = raw_put(relay, CREDENTIALS, CONVERSATION, 4096)
            try:
                drip_window = {}

                def drip() -> None:
                    drip_window["seconds"] = self.sustained_drip(connection, b"z", interval=0.2, seconds=6.0)

                self.assert_cut_off_near_budget(connection, 1.0, drip, "25+ drip")
                self.assertLess(drip_window.get("seconds", 99.0), 2.0, "the drip was still running after the budget")
            finally:
                connection.close()

    def test_unauthenticated_sustained_drip_is_also_cut_off(self) -> None:
        with RelayHarness(body_budget_seconds=1, socket_timeout_seconds=60) as relay:
            connection = relay.raw_connection(timeout=30)
            try:
                connection.sendall(
                    (
                        f"PUT {DAV_ROOT}assistant/x/y/payload HTTP/1.1\r\n"
                        f"Host: 127.0.0.1:{relay.port}\r\n"
                        f"Authorization: Basic bm90OnZhbGlk\r\n"
                        f"Content-Length: 4096\r\nConnection: close\r\n\r\n"
                    ).encode("ascii")
                )
                elapsed = self.assert_cut_off_near_budget(
                    connection,
                    1.0,
                    lambda: self.sustained_drip(connection, b"e"),
                    "unauthenticated drip",
                    expected=(b" 400 ", b" 401 "),
                )
                self.assertLess(elapsed, 3.0)
            finally:
                connection.close()

    def test_long_poll_is_not_limited_by_the_body_budget(self) -> None:
        with RelayHarness(body_budget_seconds=1, longpoll_max_seconds=3) as relay:
            prepare(relay, CREDENTIALS)
            started = time.monotonic()
            response = relay.changes(CREDENTIALS, since=0, timeout=20)
            elapsed = time.monotonic() - started
            self.assertEqual(200, response.status)
            self.assertEqual({"revision": 0}, response.json())
            self.assertGreaterEqual(elapsed, 2.5, "the long poll was cut short by the body budget")
            self.assertLess(elapsed, 10.0)

    def test_normal_upload_still_completes_inside_a_small_budget(self) -> None:
        with RelayHarness(body_budget_seconds=5) as relay:
            prepare(relay, CREDENTIALS)
            payload = b"p" * (512 * 1024)
            self.assertEqual(201, relay.dav("PUT", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS, body=payload).status)
            self.assertEqual(payload, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).body)


class BoundedTrackerTests(unittest.TestCase):
    def test_failure_window_is_hard_bounded_and_evicts_the_oldest_key(self) -> None:
        from mpt_relay.limits import FailureWindow

        window = FailureWindow(limit=3, window_seconds=300, max_keys=32)
        for index in range(500):
            window.record(f"10.0.{index // 256}.{index % 256}")
        self.assertLessEqual(window.tracked_keys(), 32)
        self.assertEqual(32, window.tracked_keys())
        # The oldest keys are gone, the newest are tracked and still enforce the limit.
        self.assertEqual(0.0, window.blocked_for("10.0.0.0"))
        for _ in range(3):
            window.record("203.0.113.9")
        self.assertGreater(window.blocked_for("203.0.113.9"), 0.0)

    def test_failure_window_stays_fast_with_many_distinct_keys(self) -> None:
        from mpt_relay.limits import FailureWindow

        window = FailureWindow(limit=60, window_seconds=300, max_keys=1024)
        started = time.monotonic()
        for index in range(20000):
            window.record(f"198.51.{index % 256}.{index % 251}")
            window.blocked_for(f"198.51.{index % 256}.{index % 251}")
        elapsed = time.monotonic() - started
        self.assertLess(elapsed, 5.0, f"tracker work grew with the number of keys ({elapsed:.2f}s)")
        self.assertLessEqual(window.tracked_keys(), 1024)

    def test_rate_limiter_is_hard_bounded(self) -> None:
        from mpt_relay.limits import RateLimiter

        limiter = RateLimiter(limit=2, window_seconds=3600, max_keys=16)
        for index in range(200):
            allowed, _retry = limiter.allow(f"key-{index}")
            self.assertTrue(allowed)
        self.assertLessEqual(limiter.tracked_keys(), 16)
        # The newest key still has its own bucket: one more allow fits, the next is refused.
        allowed, _retry = limiter.allow("key-199")
        self.assertTrue(allowed)
        allowed, retry = limiter.allow("key-199")
        self.assertFalse(allowed)
        self.assertGreater(retry, 0)
        # An evicted key starts from a fresh bucket instead of being remembered forever.
        allowed, _retry = limiter.allow("key-0")
        self.assertTrue(allowed)

    def test_per_key_events_do_not_grow_without_bound(self) -> None:
        from mpt_relay.limits import FailureWindow

        window = FailureWindow(limit=5, window_seconds=3600, max_keys=16)
        for _ in range(10_000):
            window.record("203.0.113.7")
        self.assertEqual(1, window.tracked_keys())
        self.assertGreater(window.blocked_for("203.0.113.7"), 0.0)


if __name__ == "__main__":
    unittest.main()

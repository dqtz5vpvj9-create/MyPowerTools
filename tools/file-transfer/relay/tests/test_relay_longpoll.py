"""``GET /mpt/relay/v1/changes``: the long poll that replaces the 60 second offline poll.

Contract (fixed by ``FileTransfer.Core.PublicRelayClient.ChangesAsync``):

* no ``since``            -> the current revision, immediately,
* ``since == current``    -> held for at most 25 seconds, waking on a real change,
* ``since >  current``    -> the current revision, immediately,
* a manifest or receipt PUT is a change; a payload PUT is not,
* the revision survives a service restart.
"""

from __future__ import annotations

import hashlib
import json
import threading
import time
import unittest

from relay_testkit import BASE_PATH, RelayHarness

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"conversation-a").hexdigest()
CREDENTIALS = (CONVERSATION, CONVERSATION_KEY)
OTHER = "self-bbbb2222"
OTHER_CREDENTIALS = (OTHER, hashlib.sha256(b"conversation-b").hexdigest())
ITEM = "0123456789abcdef0123456789abcdef"
ITEM_TWO = "fedcba9876543210fedcba9876543210"


def item_root(conversation: str = CONVERSATION, item: str = ITEM) -> str:
    return f"assistant/{conversation}/{item}/"


def prepare(relay: RelayHarness, credentials=CREDENTIALS, items=(ITEM, ITEM_TWO)) -> None:
    relay.conversations(credentials)
    relay.dav("MKCOL", "assistant/", basic=credentials)
    relay.dav("MKCOL", f"assistant/{credentials[0]}/", basic=credentials)
    for item in items:
        relay.dav("MKCOL", f"assistant/{credentials[0]}/{item}/", basic=credentials)


class ChangesBasicsTests(unittest.TestCase):
    def test_missing_since_returns_current_revision_immediately(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            started = time.monotonic()
            response = relay.changes(CREDENTIALS, since=None)
            elapsed = time.monotonic() - started
            self.assertEqual(200, response.status)
            self.assertEqual({"revision": 0}, response.json())
            self.assertLess(elapsed, 1.0)

    def test_since_ahead_of_the_server_returns_immediately(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            started = time.monotonic()
            response = relay.changes(CREDENTIALS, since=999)
            self.assertEqual(200, response.status)
            self.assertEqual({"revision": 0}, response.json())
            self.assertLess(time.monotonic() - started, 1.0)

    def test_invalid_since_is_a_client_error(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            self.assertEqual(400, relay.request("GET", f"{BASE_PATH}/v1/changes?since=abc", basic=CREDENTIALS).status)
            self.assertEqual(400, relay.request("GET", f"{BASE_PATH}/v1/changes?since=-1", basic=CREDENTIALS).status)

    def test_changes_requires_authentication(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            self.assertEqual(401, relay.request("GET", f"{BASE_PATH}/v1/changes").status)
            self.assertEqual(
                401,
                relay.request("GET", f"{BASE_PATH}/v1/changes", basic=(CONVERSATION, hashlib.sha256(b"bad").hexdigest())).status,
            )

    def test_timeout_returns_the_unchanged_revision(self) -> None:
        with RelayHarness(longpoll_max_seconds=1) as relay:
            prepare(relay)
            started = time.monotonic()
            response = relay.changes(CREDENTIALS, since=0, timeout=10)
            elapsed = time.monotonic() - started
            self.assertEqual(200, response.status)
            self.assertEqual({"revision": 0}, response.json())
            self.assertGreaterEqual(elapsed, 0.9)
            self.assertLess(elapsed, 5.0)


class ChangesNotificationTests(unittest.TestCase):
    def test_payload_is_silent_and_publish_markers_wake_the_poll(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            result = {}

            def poll() -> None:
                started = time.monotonic()
                result["response"] = relay.changes(CREDENTIALS, since=0, timeout=30)
                result["elapsed"] = time.monotonic() - started

            worker = threading.Thread(target=poll, daemon=True)
            worker.start()
            time.sleep(0.4)

            # A payload upload is a partial publish: it must not wake the receiver.
            self.assertEqual(201, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"data").status)
            time.sleep(1.2)
            self.assertNotIn("response", result, "payload upload woke the long poll")

            # Publishing the manifest is what makes the item real.
            self.assertEqual(201, relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b'{"version":1}').status)
            self.assertTrue(relay.wait_for(lambda: not worker.is_alive(), timeout=5))
            self.assertEqual({"revision": 1}, result["response"].json())
            self.assertLess(result["elapsed"], 5.0)

    def test_receipt_wakes_the_poll_and_each_publish_advances_once(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            self.assertEqual(201, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"data").status)
            self.assertEqual(201, relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b"{}").status)
            self.assertEqual(1, relay.changes(CREDENTIALS).json()["revision"])

            waiter = threading.Thread(
                target=lambda: relay.changes(CREDENTIALS, since=1, timeout=30), daemon=True
            )
            waiter.start()
            time.sleep(0.4)
            relay.dav("MKCOL", item_root() + "receipts/", basic=CREDENTIALS)
            self.assertEqual(
                201,
                relay.dav("PUT", item_root() + "receipts/device-1.json", basic=CREDENTIALS, body=b"{}").status,
            )
            self.assertTrue(relay.wait_for(lambda: not waiter.is_alive(), timeout=5))
            self.assertEqual(2, relay.changes(CREDENTIALS).json()["revision"])

    def test_revisions_are_isolated_per_conversation(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            prepare(relay, OTHER_CREDENTIALS, items=(ITEM,))
            self.assertEqual(201, relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b"{}").status)
            self.assertEqual(1, relay.changes(CREDENTIALS).json()["revision"])
            self.assertEqual(0, relay.changes(OTHER_CREDENTIALS).json()["revision"])

            woken = {}
            waiter = threading.Thread(
                target=lambda: woken.update(
                    response=relay.changes(OTHER_CREDENTIALS, since=0, timeout=30)
                ),
                daemon=True,
            )
            waiter.start()
            time.sleep(0.4)
            self.assertEqual(
                201,
                relay.dav("PUT", f"assistant/{OTHER}/{ITEM}/manifest.json", basic=OTHER_CREDENTIALS, body=b"{}").status,
            )
            self.assertTrue(relay.wait_for(lambda: not waiter.is_alive(), timeout=5))
            self.assertEqual({"revision": 1}, woken["response"].json())
            self.assertEqual(1, relay.changes(CREDENTIALS).json()["revision"])

    def test_delete_of_a_manifest_wakes_and_delete_of_a_payload_does_not(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"data")
            relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b"{}")
            self.assertEqual(1, relay.changes(CREDENTIALS).json()["revision"])

            waiter = threading.Thread(target=lambda: relay.changes(CREDENTIALS, since=1, timeout=30), daemon=True)
            waiter.start()
            time.sleep(0.3)
            self.assertEqual(204, relay.dav("DELETE", item_root() + "payload", basic=CREDENTIALS).status)
            time.sleep(1.0)
            self.assertTrue(waiter.is_alive(), "deleting a payload must not wake the poll")
            self.assertEqual(204, relay.dav("DELETE", item_root() + "manifest.json", basic=CREDENTIALS).status)
            self.assertTrue(relay.wait_for(lambda: not waiter.is_alive(), timeout=5))
            self.assertEqual(2, relay.changes(CREDENTIALS).json()["revision"])

    def test_abandoned_long_poll_ends_without_waiting_for_the_timeout(self) -> None:
        with RelayHarness(longpoll_max_seconds=25) as relay:
            prepare(relay)
            baseline = threading.active_count()
            connection = relay.raw_connection()
            try:
                import base64

                token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
                connection.sendall(
                    (
                        f"GET {BASE_PATH}/v1/changes?since=0 HTTP/1.1\r\n"
                        f"Host: 127.0.0.1:{relay.port}\r\nAuthorization: Basic {token}\r\n\r\n"
                    ).encode("ascii")
                )
                time.sleep(0.6)
                self.assertGreaterEqual(threading.active_count(), baseline)
            finally:
                connection.close()
            # The service notices the disconnect within its 0.5 s poll slice instead of pinning a
            # worker thread for the full 25 s cap.
            self.assertTrue(
                relay.wait_for(lambda: threading.active_count() <= baseline, timeout=6),
                "the abandoned long poll kept its worker thread",
            )


class RestartPersistenceTests(unittest.TestCase):
    def test_revision_credentials_and_content_survive_a_restart(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            payload = b"persisted-payload"
            self.assertEqual(201, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=payload).status)
            self.assertEqual(201, relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b'{"version":1}').status)
            self.assertEqual(1, relay.changes(CREDENTIALS).json()["revision"])
            relay.stop()

            restarted = RelayHarness(keep_dir=relay.root)
            try:
                self.assertEqual(1, restarted.changes(CREDENTIALS).json()["revision"])
                # A long poll started after the restart still sees the old revision as "old news".
                self.assertEqual(1, restarted.changes(CREDENTIALS, since=0).json()["revision"])
                self.assertEqual(207, restarted.dav("PROPFIND", "", basic=CREDENTIALS, headers={"Depth": "0"}).status)
                self.assertEqual(payload, restarted.dav("GET", item_root() + "payload", basic=CREDENTIALS).body)
                self.assertEqual(401, restarted.dav("PROPFIND", "", basic=(CONVERSATION, hashlib.sha256(b"bad").hexdigest()), headers={"Depth": "0"}).status)
                # Publishing after the restart continues the same counter.
                self.assertEqual(
                    201,
                    restarted.dav("PUT", f"assistant/{CONVERSATION}/{ITEM_TWO}/manifest.json", basic=CREDENTIALS, body=b"{}").status,
                )
                self.assertEqual(2, restarted.changes(CREDENTIALS, since=1).json()["revision"])
            finally:
                restarted.stop()

    def test_credentials_are_stored_as_a_salted_digest(self) -> None:
        with RelayHarness() as relay:
            prepare(relay)
            relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b"{}")
            database = relay.data_dir / "relay.sqlite3"
            self.assertTrue(database.exists())
            blob = b""
            for candidate in (database, database.with_name(database.name + "-wal"), database.with_name(database.name + "-shm")):
                if candidate.exists():
                    blob += candidate.read_bytes()
            self.assertNotIn(CONVERSATION_KEY.encode("ascii"), blob)
            self.assertNotIn(CONVERSATION_KEY.upper().encode("ascii"), blob)
            self.assertNotIn(b"Basic", blob)
            import sqlite3

            connection = sqlite3.connect(str(database))
            try:
                row = connection.execute(
                    "SELECT conversation_id, salt, digest, iterations, revision FROM conversations WHERE conversation_id = ?",
                    (CONVERSATION,),
                ).fetchone()
            finally:
                connection.close()
            self.assertIsNotNone(row)
            self.assertEqual(CONVERSATION, row[0])
            self.assertGreaterEqual(len(row[1]), 16)
            self.assertEqual(32, len(row[2]))
            self.assertGreaterEqual(row[3], 10000)
            self.assertEqual(1, row[4])


if __name__ == "__main__":
    unittest.main()

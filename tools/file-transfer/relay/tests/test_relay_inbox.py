"""Pairing inboxes: deposit/owner authority, idempotent delivery, real receipts.

The security properties under test (all over real HTTP):

* a deposit credential can never act as the owner, never lists, never reads a payload and
  never writes a receipt, and never turns into the owner by re-registering,
* an inbox id plus a random item id is the only thing a deposit credential can read back,
* a receipt only ever describes the item in its path, within the item's size,
* a delivery that arrives before the receiver registered is *retryable*, not fatal,
* the same item delivered twice is stored once and does not wake the owner twice.
"""

from __future__ import annotations

import base64
import hashlib
import json
import threading
import time
import unittest

from relay_testkit import BASE_PATH, RelayHarness, read_response

INBOX = "inbox-aaaa1111"
OWNER_KEY = hashlib.sha256(b"inbox-owner").hexdigest()
DEPOSIT_KEY = hashlib.sha256(b"inbox-deposit").hexdigest()
OTHER_INBOX = "inbox-bbbb2222"
OTHER_OWNER = hashlib.sha256(b"other-owner").hexdigest()
OTHER_DEPOSIT = hashlib.sha256(b"other-deposit").hexdigest()

OWNER = (INBOX, OWNER_KEY)
DEPOSIT = (INBOX, DEPOSIT_KEY)
ITEM = hashlib.sha256(b"item-1").hexdigest()[:32]
ITEM_TWO = hashlib.sha256(b"item-2").hexdigest()[:32]

ITEMS = f"{BASE_PATH}/v1/inboxes/items"


def item_path(item_id: str = ITEM) -> str:
    return f"{ITEMS}/{item_id}"


def file_headers(name: str = "报告.pdf", sender: str = "phone-1") -> dict:
    import urllib.parse

    return {
        "X-MPT-Kind": "file",
        "X-MPT-Name": urllib.parse.quote(name, safe=""),
        "X-MPT-Sender-Id": sender,
        "X-MPT-Sender-Name": urllib.parse.quote("Android 手机", safe=""),
    }


class InboxHarness(unittest.TestCase):
    def setUp(self) -> None:
        self.relay = RelayHarness()
        self.addCleanup(self.relay.stop)

    def register(self, credentials=OWNER, deposit_key: str = DEPOSIT_KEY, **kwargs):
        return self.relay.request(
            "POST",
            f"{BASE_PATH}/v1/inboxes",
            basic=credentials,
            body=json.dumps({"depositKey": deposit_key}).encode("utf-8"),
            **kwargs,
        )

    def deposit(self, item_id: str, payload: bytes, credentials=DEPOSIT, **kwargs):
        headers = kwargs.pop("headers", None) or file_headers()
        return self.relay.request("PUT", item_path(item_id), basic=credentials, body=payload, headers=headers, **kwargs)

    def list_items(self, credentials=OWNER, since=None, **kwargs):
        path = ITEMS if since is None else f"{ITEMS}?since={since}"
        return self.relay.request("GET", path, basic=credentials, **kwargs)

    def payload(self, item_id: str = ITEM, credentials=OWNER, **kwargs):
        return self.relay.request("GET", item_path(item_id), basic=credentials, **kwargs)

    def write_receipt(self, item_id: str, body: dict, credentials=OWNER):
        return self.relay.request("POST", item_path(item_id) + "/receipt", basic=credentials, body=json.dumps(body).encode("utf-8"))

    def read_receipt(self, item_id: str = ITEM, credentials=DEPOSIT):
        return self.relay.request("GET", item_path(item_id) + "/receipt", basic=credentials)


class RegistrationTests(InboxHarness):
    def test_first_registration_then_idempotent(self) -> None:
        created = self.register()
        self.assertEqual(201, created.status)
        payload = created.json()
        self.assertEqual(INBOX, payload["inboxId"])
        self.assertTrue(payload["created"])
        self.assertEqual(f"{BASE_PATH}/v1/inboxes/items", payload["itemsPath"])

        again = self.register()
        self.assertEqual(200, again.status)
        self.assertFalse(again.json()["created"])

    def test_deposit_key_can_never_become_owner(self) -> None:
        self.assertEqual(201, self.register().status)
        # Presenting the deposit key as the owner key must fail, with or without a body.
        self.assertEqual(401, self.register(credentials=DEPOSIT, deposit_key=OWNER_KEY).status)
        self.assertEqual(401, self.register(credentials=DEPOSIT, deposit_key=DEPOSIT_KEY).status)
        self.assertEqual(401, self.register(credentials=(INBOX, hashlib.sha256(b"stranger").hexdigest())).status)
        # The owner key still works and the deposit key is unchanged.
        self.assertEqual(200, self.register().status)
        self.assertEqual(403, self.list_items(credentials=DEPOSIT).status)
        self.assertEqual(201, self.deposit(ITEM, b"payload").status)

    def test_deposit_key_cannot_be_replaced(self) -> None:
        self.assertEqual(201, self.register().status)
        conflict = self.register(deposit_key=hashlib.sha256(b"new-deposit").hexdigest())
        self.assertEqual(409, conflict.status)
        self.assertEqual("deposit_key_locked", conflict.json()["error"])
        # The original pair code keeps working.
        self.assertEqual(201, self.deposit(ITEM, b"payload", credentials=DEPOSIT).status)

    def test_registration_validation(self) -> None:
        self.assertEqual(400, self.register(deposit_key="not-hex").status)
        self.assertEqual(400, self.register(deposit_key=OWNER_KEY).status)
        self.assertEqual(
            400,
            self.relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=OWNER, body=b"not json").status,
        )
        self.assertEqual(401, self.relay.request("POST", f"{BASE_PATH}/v1/inboxes").status)
        self.assertEqual(
            401,
            self.relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=(INBOX, "short"), body=b"{}").status,
        )

    def test_max_inboxes_is_enforced(self) -> None:
        with RelayHarness(max_inboxes=1) as relay:
            self.assertEqual(201, relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=OWNER, body=json.dumps({"depositKey": DEPOSIT_KEY}).encode()).status)
            second = relay.request(
                "POST", f"{BASE_PATH}/v1/inboxes", basic=(OTHER_INBOX, OTHER_OWNER), body=json.dumps({"depositKey": OTHER_DEPOSIT}).encode()
            )
            self.assertEqual(429, second.status)


class DepositTests(InboxHarness):
    def setUp(self) -> None:
        super().setUp()
        self.assertEqual(201, self.register().status)

    def test_delivery_metadata_and_payload(self) -> None:
        payload = "来自手机的问候".encode("utf-8")
        response = self.deposit(ITEM, payload)
        self.assertEqual(201, response.status)
        self.assertEqual(ITEM, response.json()["itemId"])
        self.assertEqual(len(payload), response.json()["size"])

        fetched = self.payload()
        self.assertEqual(200, fetched.status)
        self.assertEqual(payload, fetched.body)
        self.assertEqual(ITEM, fetched.header("x-mpt-item-id"))
        self.assertEqual("file", fetched.header("x-mpt-kind"))
        self.assertEqual("报告.pdf", __import__("urllib.parse", fromlist=["unquote"]).unquote(fetched.header("x-mpt-name")))
        self.assertEqual(len(payload), int(fetched.header("x-mpt-size")))

        head = self.relay.request("HEAD", item_path(), basic=OWNER)
        self.assertEqual(200, head.status)
        self.assertEqual(b"", head.body)
        self.assertEqual(str(len(payload)), head.header("content-length"))

    def test_text_item_carries_no_file_name(self) -> None:
        text = "hello".encode("utf-8")
        headers = {"X-MPT-Kind": "text", "X-MPT-Sender-Id": "phone-1"}
        self.assertEqual(201, self.deposit(ITEM, text, headers=headers).status)
        self.assertEqual(text, self.payload().body)
        named = dict(headers, **{"X-MPT-Name": "text.txt"})
        self.assertEqual(400, self.deposit(ITEM_TWO, text, headers=named).status)

    def test_duplicate_delivery_is_idempotent(self) -> None:
        payload = b"same bytes"
        self.assertEqual(201, self.deposit(ITEM, payload).status)
        revision = self.list_items(since=None).json()["revision"]
        duplicate = self.deposit(ITEM, payload)
        self.assertEqual(200, duplicate.status)
        self.assertTrue(duplicate.json()["duplicate"])
        self.assertEqual(revision, duplicate.json()["revision"], "a duplicate must not bump the revision again")
        self.assertEqual(1, len(self.list_items().json()["items"]))

        conflicting = self.deposit(ITEM, b"other bytes!", headers=file_headers(name="other.pdf"))
        self.assertEqual(409, conflicting.status)
        self.assertEqual("item_conflict", conflicting.json()["error"])
        self.assertEqual(payload, self.payload().body, "the stored copy is never replaced by a conflict")

    def test_validation_of_item_id_and_metadata(self) -> None:
        self.assertEqual(400, self.deposit("NOTHEX", b"x").status)
        self.assertEqual(400, self.deposit(ITEM, b"x", headers={"X-MPT-Kind": "bogus"}).status)
        self.assertEqual(400, self.deposit(ITEM, b"x", headers={"X-MPT-Kind": "file"}).status)  # no name
        self.assertEqual(400, self.deposit(ITEM, b"x", headers=file_headers(name="../escape")).status)
        self.assertEqual(400, self.deposit(ITEM, b"x", headers=file_headers(name="CON.txt")).status)
        self.assertEqual(400, self.deposit(ITEM, b"x", headers=dict(file_headers(), **{"X-MPT-Sender-Id": "bad id!"})).status)
        self.assertEqual(400, self.deposit(ITEM, b"x", headers=dict(file_headers(), **{"X-MPT-Name": "a" * 400})).status)

    def test_deposit_cannot_deliver_into_an_unregistered_inbox(self) -> None:
        response = self.relay.request("PUT", item_path(), basic=(OTHER_INBOX, OTHER_DEPOSIT), body=b"x", headers=file_headers())
        self.assertEqual(503, response.status)
        self.assertEqual("inbox_not_ready", response.json()["error"])
        self.assertIsNotNone(response.header("retry-after"))
        # After the receiver registers, the very same delivery succeeds: the sender just retries.
        self.assertEqual(201, self.register(credentials=(OTHER_INBOX, OTHER_OWNER), deposit_key=OTHER_DEPOSIT).status)
        retried = self.relay.request("PUT", item_path(), basic=(OTHER_INBOX, OTHER_DEPOSIT), body=b"x", headers=file_headers())
        self.assertEqual(201, retried.status)


class DepositAuthorityTests(InboxHarness):
    """A deposit credential is deposit-only: no listing, no payload, no receipt writing."""

    def setUp(self) -> None:
        super().setUp()
        self.assertEqual(201, self.register().status)
        self.assertEqual(201, self.deposit(ITEM, b"private payload").status)

    def test_deposit_cannot_list_or_read_payloads(self) -> None:
        self.assertEqual(403, self.list_items(credentials=DEPOSIT).status)
        self.assertEqual(403, self.payload(credentials=DEPOSIT).status)
        self.assertEqual(403, self.relay.request("HEAD", item_path(), basic=DEPOSIT).status)
        self.assertEqual(200, self.payload(credentials=OWNER).status)

    def test_deposit_cannot_write_a_receipt(self) -> None:
        forged = self.write_receipt(ITEM, {"savedAt": "2026-01-01T00:00:00Z", "bytes": 15}, credentials=DEPOSIT)
        self.assertEqual(403, forged.status)
        self.assertFalse(self.read_receipt().json()["saved"], "a forged receipt must not exist")

    def test_deposit_cannot_delete_or_use_owner_routes(self) -> None:
        self.assertEqual(403, self.relay.request("DELETE", item_path(), basic=DEPOSIT).status)
        self.assertEqual(401, self.list_items(credentials=(INBOX, hashlib.sha256(b"nope").hexdigest())).status)

    def test_deposit_cannot_reach_another_inbox(self) -> None:
        """The inbox is taken from the credential, so the two namespaces never overlap."""
        self.assertEqual(201, self.register(credentials=(OTHER_INBOX, OTHER_OWNER), deposit_key=OTHER_DEPOSIT).status)
        # A delivery authenticated as the other inbox lands there, not here.
        foreign = self.relay.request(
            "PUT", item_path(ITEM_TWO), basic=(OTHER_INBOX, OTHER_DEPOSIT), body=b"other inbox bytes", headers=file_headers()
        )
        self.assertEqual(201, foreign.status)
        self.assertEqual(404, self.payload(ITEM_TWO).status, "this inbox must not see the other inbox item")
        self.assertEqual([ITEM], [entry["itemId"] for entry in self.list_items().json()["items"]])
        # Cross-pairing an inbox id with a foreign key is a plain authentication failure.
        self.assertEqual(401, self.payload(credentials=(OTHER_INBOX, DEPOSIT_KEY)).status)
        self.assertEqual(401, self.list_items(credentials=(OTHER_INBOX, OWNER_KEY)).status)
        self.assertEqual(403, self.payload(credentials=(OTHER_INBOX, OTHER_DEPOSIT)).status)

    def test_deposit_cannot_gain_dav_or_conversation_access(self) -> None:
        """The deposit credential names an inbox, not a conversation."""
        self.assertEqual(401, self.relay.changes(DEPOSIT).status)
        with RelayHarness(dav_auto_register=0) as strict:
            self.assertEqual(401, strict.dav("PROPFIND", "", basic=DEPOSIT, headers={"Depth": "0"}).status)
        # Even with WebDAV first-use registration enabled, an inbox id is not a conversation id:
        # it can never shadow (or be shadowed by) a conversation namespace.
        self.assertEqual(401, self.relay.dav("PROPFIND", "", basic=DEPOSIT, headers={"Depth": "0"}).status)
        self.assertFalse(self.relay.store.conversation_exists(INBOX))
        self.assertEqual(401, self.relay.changes(DEPOSIT).status)

    def test_item_id_is_needed_to_read_a_receipt(self) -> None:
        unknown = hashlib.sha256(b"never-delivered").hexdigest()[:32]
        self.assertEqual(404, self.read_receipt(unknown).status)
        self.assertEqual(400, self.read_receipt("not-hex").status)
        self.assertEqual(200, self.read_receipt().status)


class ReceiptTests(InboxHarness):
    def setUp(self) -> None:
        super().setUp()
        self.assertEqual(201, self.register().status)
        self.payload_bytes = b"received bytes"
        self.assertEqual(201, self.deposit(ITEM, self.payload_bytes).status)

    def test_owner_receipt_is_visible_to_the_deposit_credential(self) -> None:
        before = self.read_receipt().json()
        self.assertFalse(before["saved"])
        self.assertEqual(len(self.payload_bytes), before["size"])

        written = self.write_receipt(
            ITEM,
            {
                "savedAt": "2026-09-28T12:00:00Z",
                "bytes": len(self.payload_bytes),
                "deviceId": "pc-1",
                "deviceName": "Windows",
            },
        )
        self.assertEqual(200, written.status)
        self.assertTrue(written.json()["saved"])

        after = self.read_receipt().json()
        self.assertTrue(after["saved"])
        self.assertEqual("2026-09-28T12:00:00Z", after["savedAt"])
        self.assertEqual(len(self.payload_bytes), after["bytes"])
        self.assertEqual("pc-1", after["deviceId"])
        self.assertEqual("Windows", after["deviceName"])
        # The receipted item leaves the pending list, and the delivery stays on disk.
        self.assertEqual([], self.list_items().json()["items"])
        self.assertEqual(self.payload_bytes, self.payload().body)

    def test_tampered_receipts_are_rejected(self) -> None:
        cases = [
            ({"savedAt": "2026-09-28T12:00:00Z", "bytes": len(self.payload_bytes) + 1}, "bytes larger than the item"),
            ({"savedAt": "2026-09-28T12:00:00Z", "bytes": -1}, "negative bytes"),
            ({"savedAt": "not a time", "bytes": 1}, "unparsable timestamp"),
            ({"bytes": 1}, "missing timestamp"),
            ({"savedAt": "2026-09-28T12:00:00Z", "bytes": 1, "itemId": ITEM_TWO}, "item id mismatch"),
            ({"savedAt": "2026-09-28T12:00:00Z", "bytes": 1, "deviceId": "bad id!"}, "invalid device id"),
            ({"savedAt": "2026-09-28T12:00:00Z", "bytes": 1, "deviceName": "x" * 200}, "oversized device name"),
        ]
        for body, label in cases:
            with self.subTest(label=label):
                response = self.write_receipt(ITEM, body)
                self.assertEqual(400, response.status, f"{label}: {response.body[:120]!r}")
        self.assertFalse(self.read_receipt().json()["saved"])
        self.assertEqual(404, self.write_receipt(ITEM_TWO, {"savedAt": "2026-09-28T12:00:00Z", "bytes": 0}).status)

    def test_duplicate_receipt_is_reported_without_rewriting(self) -> None:
        body = {"savedAt": "2026-09-28T12:00:00Z", "bytes": len(self.payload_bytes), "deviceId": "pc-1"}
        self.assertEqual(200, self.write_receipt(ITEM, body).status)
        again = self.write_receipt(ITEM, body)
        self.assertEqual(200, again.status)
        self.assertTrue(again.json()["duplicate"])


class ConcurrentDeliveryTests(InboxHarness):
    """Same-item-id races must create/charge/publish exactly once (root-reported defect)."""

    def setUp(self) -> None:
        super().setUp()
        self.assertEqual(201, self.register().status)

    def racing_put(self, item_id: str, payload: bytes, headers: dict, declared: int = None) -> list:
        """Two PUTs with the same item id whose headers are both parsed before either body.

        This is the exact interleaving that used to slip past an out-of-lock existence check:
        both requests observe "no item yet", then write one after the other.
        """
        import base64
        import socket

        declared = len(payload) if declared is None else declared
        token = base64.b64encode(f"{INBOX}:{DEPOSIT_KEY}".encode()).decode()
        connections = []
        for _ in range(2):
            connection = self.relay.raw_connection(timeout=20)
            extra = "".join(f"{name}: {value}\r\n" for name, value in headers.items())
            connection.sendall(
                (
                    f"PUT {ITEMS}/{item_id} HTTP/1.1\r\n"
                    f"Host: 127.0.0.1:{self.relay.port}\r\n"
                    f"Authorization: Basic {token}\r\n"
                    f"Content-Length: {declared}\r\n{extra}"
                    f"Connection: close\r\n\r\n"
                ).encode("ascii")
            )
            connections.append(connection)
        time.sleep(0.3)  # both requests are parsed and inside put_item
        for connection in connections:
            try:
                connection.sendall(payload)
            except OSError:
                pass
        replies = []
        for connection in connections:
            try:
                result = read_response(connection)
            except (socket.timeout, ConnectionResetError, OSError):
                result = None
            finally:
                connection.close()
            if result is None:
                replies.append((0, {}))
            else:
                replies.append((result.status, result.json() if result.body.strip() else {}))
        return replies

    def item_storage(self) -> tuple:
        directory = self.relay.store.inbox_dir(INBOX) / "items" / ITEM
        metadata = json.loads((directory / "item.json").read_text(encoding="utf-8"))
        return metadata, (directory / "payload").read_bytes(), directory

    def test_concurrent_same_id_same_metadata_creates_charges_and_publishes_once(self) -> None:
        payload = b"identical retry payload"
        replies = self.racing_put(ITEM, payload, file_headers())
        codes = sorted(status for status, _body in replies)
        self.assertEqual([200, 201], codes, f"expected one creation and one duplicate: {replies}")
        duplicate = next(body for status, body in replies if status == 200)
        self.assertTrue(duplicate["duplicate"])
        self.assertEqual(1, duplicate["revision"], "the duplicate must not bump the revision")

        metadata, stored, _directory = self.item_storage()
        self.assertEqual(len(payload), metadata["size"])
        self.assertEqual(payload, stored)
        self.assertEqual(len(payload), self.relay.store.inbox_usage(INBOX), "the namespace was charged twice")
        page = self.list_items().json()
        self.assertEqual(1, page["revision"])
        self.assertEqual([ITEM], [entry["itemId"] for entry in page["items"]])

    def test_concurrent_same_id_different_metadata_conflicts_without_overwriting(self) -> None:
        """One delivery wins with 201, the other gets 409 and never overwrites the stored copy."""
        import base64
        import urllib.parse

        payload_a = b"AAAAAAAAAAAAAAAA"
        payload_b = b"BBBBBBBBBBBBBBBB"
        token = base64.b64encode(f"{INBOX}:{DEPOSIT_KEY}".encode()).decode()
        connections = []
        for name, declared in (("first.bin", len(payload_a)), ("second.bin", len(payload_b))):
            connection = self.relay.raw_connection(timeout=20)
            connection.sendall(
                (
                    f"PUT {ITEMS}/{ITEM} HTTP/1.1\r\n"
                    f"Host: 127.0.0.1:{self.relay.port}\r\n"
                    f"Authorization: Basic {token}\r\n"
                    f"Content-Length: {declared}\r\n"
                    f"X-MPT-Kind: file\r\n"
                    f"X-MPT-Name: {urllib.parse.quote(name, safe='')}\r\n"
                    f"Connection: close\r\n\r\n"
                ).encode("ascii")
            )
            connections.append((connection, name))
        time.sleep(0.3)  # both headers parsed, both inside put_item
        connections[0][0].sendall(payload_a)
        connections[1][0].sendall(payload_b)
        responses = []
        for connection, name in connections:
            try:
                result = read_response(connection)
                status = result.status
            except (socket.timeout, ConnectionResetError, OSError):
                status = 0
            finally:
                connection.close()
            responses.append((name, status))

        statuses = sorted(status for _name, status in responses)
        self.assertEqual([201, 409], statuses, f"expected a single winner: {responses}")
        winner = next(name for name, status in responses if status == 201)

        metadata, stored, _directory = self.item_storage()
        expected = payload_a if winner == "first.bin" else payload_b
        self.assertEqual(winner, metadata["name"], "the stored metadata belongs to the loser")
        self.assertEqual(expected, stored, "the loser overwrote the winner's payload")
        self.assertEqual(len(expected), self.relay.store.inbox_usage(INBOX), "the conflict was charged")
        self.assertEqual(1, self.list_items().json()["revision"], "the conflict bumped the revision")

    def test_delete_and_receipt_race_never_leaves_an_orphan_receipt(self) -> None:
        import threading

        for _attempt in range(5):
            self.assertEqual(201, self.deposit(ITEM, b"receipt race payload").status)
            results = {}

            def write_receipt() -> None:
                results["receipt"] = self.write_receipt(
                    ITEM, {"savedAt": "2026-09-28T12:00:00Z", "bytes": 20}
                ).status

            def delete_item() -> None:
                results["delete"] = self.relay.request("DELETE", item_path(), basic=OWNER).status

            workers = [threading.Thread(target=write_receipt), threading.Thread(target=delete_item)]
            for worker in workers:
                worker.start()
            for worker in workers:
                worker.join(timeout=10)

            self.assertIn(results["receipt"], (200, 404), results)
            self.assertIn(results["delete"], (204, 404), results)
            directory = self.relay.store.inbox_dir(INBOX) / "items" / ITEM
            if directory.exists():
                # A surviving item is complete: metadata next to an optional receipt, never a
                # receipt resurrected after its item was deleted.
                self.assertTrue((directory / "item.json").is_file())
            else:
                self.assertEqual(404, self.read_receipt().status)
            if results["delete"] == 204 and not directory.exists():
                self.assertEqual(0, self.relay.store.inbox_usage(INBOX))

    def test_receipt_after_delete_is_404_and_recreates_nothing(self) -> None:
        self.assertEqual(201, self.deposit(ITEM, b"payload").status)
        self.assertEqual(204, self.relay.request("DELETE", item_path(), basic=OWNER).status)
        response = self.write_receipt(ITEM, {"savedAt": "2026-09-28T12:00:00Z", "bytes": 7})
        self.assertEqual(404, response.status)
        self.assertFalse((self.relay.store.inbox_dir(INBOX) / "items" / ITEM).exists(), "an orphan receipt was recreated")
        self.assertEqual(404, self.read_receipt().status)


class OwnerLongPollTests(InboxHarness):
    def setUp(self) -> None:
        super().setUp()
        self.assertEqual(201, self.register().status)

    def test_long_poll_wakes_on_delivery(self) -> None:
        self.assertEqual({"revision": 0, "items": [], "hasMore": False}, self.list_items().json())
        result = {}

        def poll() -> None:
            result["response"] = self.list_items(since=0, timeout=30)

        worker = threading.Thread(target=poll, daemon=True)
        worker.start()
        time.sleep(0.4)
        self.assertEqual(201, self.deposit(ITEM, b"wake up").status)
        self.assertTrue(self.relay.wait_for(lambda: not worker.is_alive(), timeout=6))
        payload = result["response"].json()
        self.assertEqual(1, payload["revision"])
        self.assertEqual([ITEM], [item["itemId"] for item in payload["items"]])

    def test_no_since_and_future_since_answer_immediately(self) -> None:
        started = time.monotonic()
        self.assertEqual(0, self.list_items().json()["revision"])
        self.assertEqual(0, self.list_items(since=999).json()["revision"])
        self.assertLess(time.monotonic() - started, 1.0)

    def test_long_poll_timeout_returns_the_same_revision(self) -> None:
        with RelayHarness(longpoll_max_seconds=1) as relay:
            credentials = (INBOX, OWNER_KEY)
            relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=credentials, body=json.dumps({"depositKey": DEPOSIT_KEY}).encode())
            started = time.monotonic()
            response = relay.request("GET", f"{ITEMS}?since=0", basic=credentials, timeout=10)
            self.assertEqual(200, response.status)
            self.assertEqual(0, response.json()["revision"])
            self.assertGreaterEqual(time.monotonic() - started, 0.9)

    def test_invalid_since_is_rejected(self) -> None:
        self.assertEqual(400, self.relay.request("GET", f"{ITEMS}?since=abc", basic=OWNER).status)
        self.assertEqual(400, self.relay.request("GET", f"{ITEMS}?since=-1", basic=OWNER).status)

    def test_listing_is_bounded_and_labelled(self) -> None:
        for index in range(3):
            item = hashlib.sha256(f"item-{index}".encode()).hexdigest()[:32]
            self.assertEqual(201, self.deposit(item, f"payload-{index}".encode(), headers=file_headers(name=f"{index}.bin")).status)
        page = self.relay.request("GET", f"{ITEMS}?limit=2", basic=OWNER).json()
        self.assertEqual(2, len(page["items"]))
        self.assertTrue(page["hasMore"])

    def test_owner_can_delete_an_item_and_reclaim_quota(self) -> None:
        self.assertEqual(201, self.deposit(ITEM, b"x" * 4096).status)
        self.assertGreater(self.relay.store.inbox_usage(INBOX), 0)
        self.assertEqual(204, self.relay.request("DELETE", item_path(), basic=OWNER).status)
        self.assertEqual(0, self.relay.store.inbox_usage(INBOX))
        self.assertEqual(404, self.payload().status)
        self.assertEqual([], self.list_items().json()["items"])


class InboxQuotaAndIsolationTests(InboxHarness):
    def test_namespace_quota_and_files_are_not_cross_counted(self) -> None:
        with RelayHarness(per_conversation_bytes=4096, max_file_bytes=4096) as relay:
            self.assertEqual(
                201,
                relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=OWNER, body=json.dumps({"depositKey": DEPOSIT_KEY}).encode()).status,
            )
            self.assertEqual(
                201,
                relay.request("PUT", item_path(), basic=DEPOSIT, body=b"a" * 3000, headers=file_headers()).status,
            )
            over = relay.request("PUT", item_path(ITEM_TWO), basic=DEPOSIT, body=b"b" * 2000, headers=file_headers())
            self.assertEqual(507, over.status)
            too_large = relay.request("PUT", item_path(ITEM_TWO), basic=DEPOSIT, body=b"b" * 5000, headers=file_headers())
            self.assertEqual(413, too_large.status)
            self.assertEqual(b"a" * 3000, relay.request("GET", item_path(), basic=OWNER).body)
            self.assertEqual(404, relay.request("GET", item_path(ITEM_TWO), basic=OWNER).status)

    def test_cancelled_delivery_leaves_nothing_visible(self) -> None:
        self.assertEqual(201, self.register().status)
        connection = self.relay.partial_put(
            f"{ITEMS}/{ITEM}",
            DEPOSIT,
            1024 * 1024,
            b"partial",
            headers=file_headers(),
        )
        connection.close()
        self.assertTrue(self.relay.wait_for(lambda: not list(self.relay.data_dir.rglob(".mpt-relay-upload-*"))))
        self.assertEqual(404, self.payload().status)
        self.assertEqual([], self.list_items().json()["items"])
        self.assertEqual(0, self.relay.store.inbox_usage(INBOX))

    def test_inbox_bytes_count_towards_the_global_quota(self) -> None:
        with RelayHarness(per_conversation_bytes=8192, max_file_bytes=8192, global_bytes=4096) as relay:
            relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=OWNER, body=json.dumps({"depositKey": DEPOSIT_KEY}).encode())
            self.assertEqual(
                201, relay.request("PUT", item_path(), basic=DEPOSIT, body=b"x" * 3000, headers=file_headers()).status
            )
            self.assertEqual(relay.store.global_usage(), relay.store.inbox_usage(INBOX))
            blocked = relay.request("PUT", item_path(ITEM_TWO), basic=DEPOSIT, body=b"y" * 2000, headers=file_headers())
            self.assertEqual(507, blocked.status)

    def test_restart_keeps_inbox_credentials_items_and_revision(self) -> None:
        with RelayHarness() as relay:
            relay.request("POST", f"{BASE_PATH}/v1/inboxes", basic=OWNER, body=json.dumps({"depositKey": DEPOSIT_KEY}).encode())
            self.assertEqual(201, relay.request("PUT", item_path(), basic=DEPOSIT, body=b"persisted inbox", headers=file_headers()).status)
            relay.stop()
            restarted = RelayHarness(keep_dir=relay.root)
            try:
                self.assertEqual(1, restarted.request("GET", f"{ITEMS}?since=0", basic=OWNER).json()["revision"])
                self.assertEqual(b"persisted inbox", restarted.request("GET", item_path(), basic=OWNER).body)
                self.assertEqual(200, restarted.request("GET", item_path() + "/receipt", basic=DEPOSIT).status)
                self.assertEqual(403, restarted.request("GET", ITEMS, basic=DEPOSIT).status)
            finally:
                restarted.stop()

    def test_deposit_credentials_are_stored_as_salted_digests(self) -> None:
        self.assertEqual(201, self.register().status)
        blob = b""
        for suffix in ("", "-wal", "-shm"):
            candidate = self.relay.data_dir / f"relay.sqlite3{suffix}"
            if candidate.exists():
                blob += candidate.read_bytes()
        self.assertNotIn(OWNER_KEY.encode(), blob)
        self.assertNotIn(DEPOSIT_KEY.encode(), blob)
        import sqlite3

        connection = sqlite3.connect(str(self.relay.data_dir / "relay.sqlite3"))
        try:
            row = connection.execute(
                "SELECT owner_salt, owner_digest, deposit_salt, deposit_digest, iterations, revision"
                " FROM inboxes WHERE inbox_id = ?",
                (INBOX,),
            ).fetchone()
        finally:
            connection.close()
        self.assertIsNotNone(row)
        self.assertEqual(32, len(row[1]))
        self.assertEqual(32, len(row[3]))
        self.assertNotEqual(row[0], row[2], "each key has its own salt")
        self.assertGreaterEqual(row[4], 10000)

    def test_logs_never_contain_inbox_credentials(self) -> None:
        self.assertEqual(201, self.register().status)
        self.deposit(ITEM, b"payload")
        self.write_receipt(ITEM, {"savedAt": "2026-09-28T12:00:00Z", "bytes": 7})
        self.assertEqual(401, self.deposit(ITEM, b"x", credentials=(INBOX, hashlib.sha256(b"bad").hexdigest())).status)
        logs = "\n".join(self.relay.logs())
        for secret in (OWNER_KEY, DEPOSIT_KEY, hashlib.sha256(b"bad").hexdigest()):
            self.assertNotIn(secret, logs)
        encoded = base64.b64encode(f"{INBOX}:{OWNER_KEY}".encode()).decode()
        self.assertNotIn(encoded, logs)
        self.assertNotIn(INBOX, logs, "the inbox id is masked in logs")


if __name__ == "__main__":
    unittest.main()

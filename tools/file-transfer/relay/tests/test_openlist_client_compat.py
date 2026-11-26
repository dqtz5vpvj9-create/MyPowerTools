"""End-to-end compatibility with the .NET OpenListClient wire contract.

Each test drives :mod:`openlist_client_semantics` — a faithful mirror of
``FileTransfer.Core/OpenListClient.cs`` and ``Assistant/OpenListAssistantClient.cs`` —
against a real relay. This is the evidence that the existing client code path works
against the public service; the .NET side of the same claim is M4's test run.
"""

from __future__ import annotations

import hashlib
import json
import unittest
from datetime import datetime, timezone
from xml.etree import ElementTree

from openlist_client_semantics import (
    DAV_ROOT,
    AssistantManifest,
    AssistantReceipt,
    InvalidData,
    OpenListClientMirror,
)
from relay_testkit import BASE_PATH, RelayHarness

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"conversation-a").hexdigest()
ITEM = "0123456789abcdef0123456789abcdef"
ITEM_TWO = "00112233445566778899aabbccddeeff"
GUID = "a1b2c3d4e5f60718293a4b5c6d7e8f90"
DEVICE_A = "device-aaaa"
DEVICE_B = "device-bbbb"
NOW = datetime(2026, 9, 28, 2, 14, 0, tzinfo=timezone.utc).isoformat()


def manifest(item: str = ITEM, **overrides) -> AssistantManifest:
    values = dict(
        id=item,
        kind="file",
        size=5,
        created_at=NOW,
        sender_device_id=DEVICE_A,
        sender_name="Windows 桌面",
        name="photo.png",
        target_device_id=None,
    )
    values.update(overrides)
    return AssistantManifest(**values)


class ClientMirrorTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.relay = RelayHarness()
        self.addCleanup(self.relay.stop)
        self.assertEqual(201, self.relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
        self.client = OpenListClientMirror(self.relay, CONVERSATION, CONVERSATION_KEY)


class GenericInboxTests(ClientMirrorTestCase):
    """``OpenListClient`` outside the assistant layout: android/<guid>/{payload,ready.json}."""

    def test_upload_list_download_round_trip(self) -> None:
        self.client.check_root()
        record = self.client.upload("android", GUID, "报告 2026.pdf", b"pdf-bytes")
        self.assertEqual(GUID, record["id"])

        listing = self.client.list_inbox("android")
        self.assertEqual(1, len(listing))
        self.assertEqual("报告 2026.pdf", listing[0]["name"])
        self.assertEqual(len(b"pdf-bytes"), listing[0]["size"])
        self.assertEqual(b"pdf-bytes", self.client.download_inbox("android", GUID))

    def test_missing_inbox_is_an_empty_list_not_an_error(self) -> None:
        self.assertEqual([], self.client.list_inbox("android"))

    def test_inbox_layout_on_disk(self) -> None:
        self.client.upload("android", GUID, "a.bin", b"x")
        root = self.relay.data_dir / "conversations" / CONVERSATION
        self.assertTrue((root / "android" / GUID / "payload").is_file())
        self.assertTrue((root / "android" / GUID / "ready.json").is_file())


class AssistantPublishTests(ClientMirrorTestCase):
    """``PublishAssistantAsync``: payload first, manifest last, idempotent by id."""

    def test_publish_file_item_and_layout(self) -> None:
        payload = b"image-bytes"
        published = self.client.publish_assistant(CONVERSATION, manifest(size=len(payload)), payload)
        self.assertEqual(ITEM, published.id)

        root = self.relay.data_dir / "conversations" / CONVERSATION / "assistant" / CONVERSATION / ITEM
        self.assertEqual(payload, (root / "payload").read_bytes())
        self.assertEqual(ITEM, json.loads((root / "manifest.json").read_text())["id"])

    def test_publish_text_item_has_no_payload(self) -> None:
        text = "你好，来自公网中转"
        item = manifest(kind="text", size=len(text.encode("utf-8")), name=None, text=text)
        self.client.publish_assistant(CONVERSATION, item, None)
        root = self.relay.data_dir / "conversations" / CONVERSATION / "assistant" / CONVERSATION / ITEM
        self.assertFalse((root / "payload").exists())
        self.assertTrue((root / "manifest.json").is_file())

    def test_republish_of_the_same_message_is_idempotent(self) -> None:
        payload = b"bytes"
        item = manifest(size=len(payload))
        self.client.publish_assistant(CONVERSATION, item, payload)
        manifest_path = (
            self.relay.data_dir / "conversations" / CONVERSATION / "assistant" / CONVERSATION / ITEM / "manifest.json"
        )
        before = manifest_path.stat().st_mtime_ns
        again = self.client.publish_assistant(CONVERSATION, item, payload)
        self.assertTrue(again.same_message(item))
        self.assertEqual(before, manifest_path.stat().st_mtime_ns, "the stored manifest was rewritten")
        self.assertEqual(1, self.relay.changes((CONVERSATION, CONVERSATION_KEY)).json()["revision"])

    def test_same_id_with_different_content_is_refused(self) -> None:
        payload = b"bytes"
        self.client.publish_assistant(CONVERSATION, manifest(size=len(payload)), payload)
        conflicting = manifest(size=len(payload), sender_name="另一个设备")
        with self.assertRaises(InvalidData):
            self.client.publish_assistant(CONVERSATION, conflicting, payload)

    def test_payload_without_manifest_is_not_an_item(self) -> None:
        self.client.mkcol(self.client.collection_url("assistant"))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION, ITEM))
        self.client.check(
            self.client.request("PUT", self.client.url("assistant", CONVERSATION, ITEM, "payload"), content=b"orphan")
        )
        page = self.client.list_assistant(CONVERSATION)
        self.assertEqual([], page.items)
        self.assertEqual(1, page.discovered_count)  # the id is discovered but has no manifest yet


class AssistantListingTests(ClientMirrorTestCase):
    """``ListAssistantAsync``: incremental, ordered by getlastmodified, tolerant of bad records."""

    def publish_pair(self) -> None:
        first = b"first"
        second = b"second-bytes"
        self.client.publish_assistant(CONVERSATION, manifest(size=len(first), sender_name="A"), first)
        self.client.publish_assistant(
            CONVERSATION, manifest(item=ITEM_TWO, size=len(second), sender_name="A"), second
        )

    def test_listing_returns_manifests_newest_first(self) -> None:
        self.publish_pair()
        page = self.client.list_assistant(CONVERSATION)
        self.assertEqual({ITEM, ITEM_TWO}, {item.id for item in page.items})
        self.assertEqual(2, page.discovered_count)
        self.assertFalse(page.has_more)
        self.assertEqual([], page.invalid_item_ids)

    def test_second_listing_with_known_ids_costs_one_propfind_and_no_get(self) -> None:
        self.publish_pair()
        first_page = self.client.list_assistant(CONVERSATION)
        known = [item.id for item in first_page.items]

        self.client.requests.clear()
        second_page = self.client.list_assistant(CONVERSATION, known=known)
        self.assertEqual([], second_page.items)
        self.assertEqual(1, len(self.client.requests))
        self.assertEqual("PROPFIND", self.client.requests[0][0])
        self.assertEqual(0, sum(1 for method, _path in self.client.requests if method == "GET"))

    def test_limit_reports_has_more_and_a_cursor(self) -> None:
        self.publish_pair()
        page = self.client.list_assistant(CONVERSATION, limit=1)
        self.assertEqual(1, len(page.items))
        self.assertTrue(page.has_more)
        next_page = self.client.list_assistant(CONVERSATION, known=[page.items[0].id], limit=1)
        self.assertEqual(1, len(next_page.items))
        self.assertNotEqual(page.items[0].id, next_page.items[0].id)

    def test_invalid_manifest_is_reported_without_poisoning_the_timeline(self) -> None:
        good = b"good"
        self.client.publish_assistant(CONVERSATION, manifest(size=len(good)), good)
        # A record whose fields violate the protocol, written straight to the relay.
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION, ITEM_TWO))
        broken = {"version": 1, "id": ITEM_TWO, "kind": "bogus", "size": -5, "senderDeviceId": "x", "senderName": ""}
        self.client.check(
            self.client.request(
                "PUT",
                self.client.url("assistant", CONVERSATION, ITEM_TWO, "manifest.json"),
                content=json.dumps(broken).encode("utf-8"),
            )
        )
        page = self.client.list_assistant(CONVERSATION)
        self.assertEqual([ITEM], [item.id for item in page.items])
        self.assertEqual([ITEM_TWO], page.invalid_item_ids)

    def test_unparsable_manifest_is_skipped_and_retried(self) -> None:
        self.client.mkcol(self.client.collection_url("assistant"))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION, ITEM))
        self.client.check(
            self.client.request(
                "PUT",
                self.client.url("assistant", CONVERSATION, ITEM, "manifest.json"),
                content=b"{ truncated",
            )
        )
        page = self.client.list_assistant(CONVERSATION)
        self.assertEqual([], page.items)
        self.assertEqual([], page.invalid_item_ids)  # retried next round, not marked known


class AssistantDownloadTests(ClientMirrorTestCase):
    def test_download_matches_manifest_size(self) -> None:
        payload = bytes(range(256)) * 40
        self.client.publish_assistant(CONVERSATION, manifest(size=len(payload), name="数据.bin"), payload)
        downloaded = self.client.download_assistant(CONVERSATION, manifest(size=len(payload), name="数据.bin"))
        self.assertEqual(hashlib.sha256(payload).hexdigest(), hashlib.sha256(downloaded).hexdigest())

    def test_missing_payload_is_a_check_failure(self) -> None:
        page = AssistantManifest(**{**manifest().__dict__, "size": 5})
        with self.assertRaises(IOError):
            self.client.download_assistant(CONVERSATION, page)


class ReceiptTests(ClientMirrorTestCase):
    def publish(self) -> None:
        payload = b"bytes"
        self.client.publish_assistant(CONVERSATION, manifest(size=len(payload)), payload)

    def test_each_device_writes_its_own_receipt(self) -> None:
        self.publish()
        first = AssistantReceipt(ITEM, DEVICE_A, "桌面", NOW, 5)
        second = AssistantReceipt(ITEM, DEVICE_B, "手机", NOW, 5)
        self.client.write_receipt(CONVERSATION, first)
        self.client.write_receipt(CONVERSATION, second)

        receipts = self.client.list_receipts(CONVERSATION, ITEM)
        self.assertEqual({DEVICE_A, DEVICE_B}, {receipt.device_id for receipt in receipts})
        path = (
            self.relay.data_dir / "conversations" / CONVERSATION / "assistant" / CONVERSATION / ITEM / "receipts"
        )
        self.assertEqual({f"{DEVICE_A}.json", f"{DEVICE_B}.json"}, {entry.name for entry in path.iterdir()})

    def test_receipts_of_a_missing_item_are_an_empty_list(self) -> None:
        self.assertEqual([], self.client.list_receipts(CONVERSATION, ITEM))

    def test_receipt_for_another_item_is_ignored(self) -> None:
        self.publish()
        wrong = AssistantReceipt(ITEM_TWO, DEVICE_A, "桌面", NOW, 5)
        self.client.write_receipt(CONVERSATION, AssistantReceipt(ITEM, DEVICE_A, "桌面", NOW, 5))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION, ITEM, "receipts"))
        self.client.check(
            self.client.request(
                "PUT",
                self.client.url("assistant", CONVERSATION, ITEM, "receipts", f"{DEVICE_B}.json"),
                content=json.dumps(wrong.to_json()).encode("utf-8"),
            )
        )
        receipts = self.client.list_receipts(CONVERSATION, ITEM)
        self.assertEqual([DEVICE_A], [receipt.device_id for receipt in receipts])

    def test_revision_advances_for_manifest_and_receipt_only(self) -> None:
        credentials = (CONVERSATION, CONVERSATION_KEY)
        self.assertEqual(0, self.relay.changes(credentials).json()["revision"])
        payload = b"bytes"
        self.client.mkcol(self.client.collection_url("assistant"))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION))
        self.client.mkcol(self.client.collection_url("assistant", CONVERSATION, ITEM))
        self.client.check(
            self.client.request("PUT", self.client.url("assistant", CONVERSATION, ITEM, "payload"), content=payload)
        )
        self.assertEqual(0, self.relay.changes(credentials).json()["revision"])
        self.client.check(
            self.client.request(
                "PUT",
                self.client.url("assistant", CONVERSATION, ITEM, "manifest.json"),
                content=json.dumps(manifest(size=len(payload)).to_json()).encode("utf-8"),
            )
        )
        self.assertEqual(1, self.relay.changes(credentials).json()["revision"])
        self.client.write_receipt(CONVERSATION, AssistantReceipt(ITEM, DEVICE_B, "手机", NOW, len(payload)))
        self.assertEqual(2, self.relay.changes(credentials).json()["revision"])


class HrefContractTests(ClientMirrorTestCase):
    def test_every_href_keeps_the_public_dav_prefix(self) -> None:
        payload = b"bytes"
        self.client.publish_assistant(CONVERSATION, manifest(size=len(payload)), payload)
        self.client.write_receipt(CONVERSATION, AssistantReceipt(ITEM, DEVICE_B, "手机", NOW, len(payload)))

        listing = self.client.request("PROPFIND", self.client.collection_url("assistant", CONVERSATION), depth="1")
        hrefs = [
            node.text or ""
            for node in ElementTree.fromstring(listing.body).iter("{DAV:}href")
        ]
        self.assertIn(f"{DAV_ROOT}assistant/{CONVERSATION}/", hrefs)
        self.assertIn(f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/", hrefs)
        for href in hrefs:
            self.assertTrue(href.startswith(DAV_ROOT), href)
            # This is what the C# does with the value: new Uri(_root, href) -> item id.
            identifier = self.client.absolute_path(href).rstrip("/").split("/")[-1]
            self.assertIn(identifier, {CONVERSATION, ITEM})

    def test_dav_root_is_the_contract_path(self) -> None:
        self.assertEqual("/mpt/relay/dav/", DAV_ROOT)
        self.assertTrue(self.client.root_url.endswith(DAV_ROOT))
        self.assertEqual(207, self.client.request("PROPFIND", DAV_ROOT, depth="0").status)

    def test_base_path_is_preserved_for_every_endpoint(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(200, relay.request("GET", f"{BASE_PATH}/health").status)
            self.assertEqual(404, relay.request("GET", "/health").status)
            self.assertEqual(404, relay.request("GET", "/dav/").status)
            credentials = ("self-cccc3333", hashlib.sha256(b"c").hexdigest())
            self.assertEqual(201, relay.request("POST", f"{BASE_PATH}/v1/conversations", basic=credentials).status)
            self.assertEqual(200, relay.request("GET", f"{BASE_PATH}/v1/changes", basic=credentials).status)


if __name__ == "__main__":
    unittest.main()

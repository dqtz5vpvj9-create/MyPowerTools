"""WebDAV behaviour: layout, method semantics, namespace isolation, traversal, atomicity, quota."""

from __future__ import annotations

import hashlib
import os
import socket
import unittest
from xml.etree import ElementTree

from relay_testkit import DAV_ROOT, RelayHarness

DAV = "{DAV:}"

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"conversation-a").hexdigest()
ITEM = "0123456789abcdef0123456789abcdef"
ITEM_TWO = "fedcba9876543210fedcba9876543210"
DEVICE = "device-1"

CREDENTIALS = (CONVERSATION, CONVERSATION_KEY)


def item_root(item: str = ITEM) -> str:
    return f"assistant/{CONVERSATION}/{item}/"


def hrefs(body: bytes) -> list:
    document = ElementTree.fromstring(body)
    return [node.text or "" for node in document.iter(f"{DAV}href")]


class LayoutTests(unittest.TestCase):
    """The exact request sequence OpenListAssistantClient performs."""

    def test_mkcol_status_codes(self) -> None:
        with RelayHarness() as relay:
            created = relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            self.assertEqual(201, created.status)
            self.assertEqual(405, relay.dav("MKCOL", "assistant/", basic=CREDENTIALS).status)
            # A collection whose parent does not exist is a conflict, which the client reads as
            # "empty conversation" instead of an error.
            self.assertEqual(409, relay.dav("MKCOL", "missing/child/", basic=CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS).status)
            self.assertEqual(201, relay.dav("MKCOL", item_root(), basic=CREDENTIALS).status)
            self.assertEqual(405, relay.dav("MKCOL", item_root(), basic=CREDENTIALS).status)

    def test_propfind_root_and_missing_collection(self) -> None:
        with RelayHarness() as relay:
            root = relay.dav("PROPFIND", "", basic=CREDENTIALS, headers={"Depth": "0"})
            self.assertEqual(207, root.status)
            self.assertIn(DAV_ROOT, root.text())
            self.assertIn("multistatus", root.text())

            missing = relay.dav("PROPFIND", f"assistant/{CONVERSATION}/", basic=CREDENTIALS, headers={"Depth": "1"})
            self.assertEqual(404, missing.status)

            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            empty = relay.dav("PROPFIND", f"assistant/{CONVERSATION}/", basic=CREDENTIALS, headers={"Depth": "1"})
            self.assertEqual(207, empty.status)
            self.assertEqual([f"{DAV_ROOT}assistant/{CONVERSATION}/"], hrefs(empty.body))

    def test_put_requires_parent_and_publishes_hrefs_with_public_prefix(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(
                409,
                relay.dav("PUT", f"assistant/{CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS, body=b"x").status,
            )
            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            relay.dav("MKCOL", item_root(), basic=CREDENTIALS)
            self.assertEqual(201, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"hello").status)
            self.assertEqual(
                201,
                relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b'{"version":1}').status,
            )
            # Overwriting an existing file is 204, like the reference WebDAV test server.
            self.assertEqual(204, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"hello2").status)

            listing = relay.dav("PROPFIND", f"assistant/{CONVERSATION}/", basic=CREDENTIALS, headers={"Depth": "1"})
            self.assertEqual(207, listing.status)
            self.assertEqual([f"{DAV_ROOT}assistant/{CONVERSATION}/", f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/"], hrefs(listing.body))
            self.assertIn("getlastmodified", listing.text())
            self.assertIn("collection", listing.text())

    def test_get_head_delete(self) -> None:
        with RelayHarness() as relay:
            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            relay.dav("MKCOL", item_root(), basic=CREDENTIALS)
            relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"0123456789")

            fetched = relay.dav("GET", item_root() + "payload", basic=CREDENTIALS)
            self.assertEqual(200, fetched.status)
            self.assertEqual(b"0123456789", fetched.body)
            self.assertEqual("10", fetched.header("content-length"))
            self.assertEqual("application/octet-stream", fetched.header("content-type"))

            head = relay.dav("HEAD", item_root() + "payload", basic=CREDENTIALS)
            self.assertEqual(200, head.status)
            self.assertEqual(b"", head.body)
            self.assertEqual("10", head.header("content-length"))

            self.assertEqual(404, relay.dav("GET", item_root() + "missing.json", basic=CREDENTIALS).status)
            self.assertEqual(405, relay.dav("GET", item_root(), basic=CREDENTIALS).status)
            self.assertEqual(404, relay.dav("DELETE", item_root() + "missing.json", basic=CREDENTIALS).status)
            self.assertEqual(204, relay.dav("DELETE", item_root() + "payload", basic=CREDENTIALS).status)
            self.assertEqual(404, relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).status)

    def test_unsupported_methods_answer_405(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(405, relay.dav("PROPPATCH", "assistant/", basic=CREDENTIALS).status)
            self.assertEqual(405, relay.dav("LOCK", "assistant/", basic=CREDENTIALS).status)
            self.assertEqual(405, relay.dav("MOVE", "assistant/", basic=CREDENTIALS).status)
            options = relay.dav("OPTIONS", "", basic=CREDENTIALS)
            self.assertEqual(200, options.status)
            self.assertIn("PROPFIND", options.header("allow"))
            self.assertEqual("1", options.header("dav"))

    def test_propfind_depth_infinity_is_refused(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(403, relay.dav("PROPFIND", "", basic=CREDENTIALS, headers={"Depth": "infinity"}).status)

    def test_mkcol_rejects_body(self) -> None:
        with RelayHarness() as relay:
            response = relay.dav("MKCOL", "assistant/", basic=CREDENTIALS, body=b"<xml/>")
            self.assertEqual(415, response.status)


class AuthenticationTests(unittest.TestCase):
    def test_every_dav_method_requires_auth(self) -> None:
        with RelayHarness() as relay:
            for method in ("PROPFIND", "MKCOL", "PUT", "GET", "HEAD", "DELETE", "OPTIONS"):
                response = relay.dav(method, f"assistant/{CONVERSATION}/{ITEM}/payload", body=b"x")
                self.assertEqual(401, response.status, method)
                self.assertIn("basic", response.header("www-authenticate").lower())

    def test_foreign_key_cannot_touch_a_registered_namespace(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(201, relay.conversations(CREDENTIALS).status)
            intruder = (CONVERSATION, hashlib.sha256(b"intruder").hexdigest())
            self.assertEqual(401, relay.dav("PROPFIND", "", basic=intruder, headers={"Depth": "0"}).status)
            self.assertEqual(401, relay.dav("MKCOL", "assistant/", basic=intruder).status)
            self.assertEqual(401, relay.dav("PUT", item_root() + "payload", basic=intruder, body=b"x").status)


class IsolationTests(unittest.TestCase):
    """Two conversations share one service and must never observe each other."""

    OTHER = "self-bbbb2222"
    OTHER_KEY = hashlib.sha256(b"conversation-b").hexdigest()

    def setUp(self) -> None:
        self.relay = RelayHarness()
        self.addCleanup(self.relay.stop)
        self.assertEqual(201, self.relay.conversations(CREDENTIALS).status)
        self.assertEqual(201, self.relay.conversations((self.OTHER, self.OTHER_KEY)).status)
        for credentials in (CREDENTIALS, (self.OTHER, self.OTHER_KEY)):
            self.relay.dav("MKCOL", "assistant/", basic=credentials)
            self.relay.dav("MKCOL", f"assistant/{credentials[0]}/", basic=credentials)

    def test_other_conversation_is_invisible(self) -> None:
        self.assertEqual(201, self.relay.dav("MKCOL", item_root(), basic=CREDENTIALS).status)
        self.assertEqual(201, self.relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"private-a").status)

        own = self.relay.dav("PROPFIND", "assistant/", basic=(self.OTHER, self.OTHER_KEY), headers={"Depth": "1"})
        self.assertEqual(207, own.status)
        self.assertEqual(
            [f"{DAV_ROOT}assistant/", f"{DAV_ROOT}assistant/{self.OTHER}/"],
            hrefs(own.body),
        )
        self.assertNotIn(CONVERSATION, own.text())

        other_root = self.relay.dav("PROPFIND", "assistant/", basic=CREDENTIALS, headers={"Depth": "1"})
        self.assertEqual([f"{DAV_ROOT}assistant/", f"{DAV_ROOT}assistant/{CONVERSATION}/"], hrefs(other_root.body))

    def test_other_conversation_paths_are_refused(self) -> None:
        self.relay.dav("MKCOL", item_root(), basic=CREDENTIALS)
        self.relay.dav("PUT", item_root() + "manifest.json", basic=CREDENTIALS, body=b'{"version":1}')
        foreign = (self.OTHER, self.OTHER_KEY)
        for method, path in (
            ("PROPFIND", f"assistant/{CONVERSATION}/"),
            ("GET", item_root() + "manifest.json"),
            ("GET", item_root() + "payload"),
            ("PUT", item_root() + "payload"),
            ("MKCOL", f"assistant/{CONVERSATION}/new/"),
            ("DELETE", item_root() + "manifest.json"),
            ("PROPFIND", f"assistant/{CONVERSATION}/{ITEM}/receipts/"),
        ):
            response = self.relay.dav(method, path, basic=foreign, headers={"Depth": "1"}, body=b"x" if method == "PUT" else None)
            self.assertEqual(403, response.status, f"{method} {path}")
        # The owner's data is untouched by the refused attempts.
        self.assertEqual(200, self.relay.dav("GET", item_root() + "manifest.json", basic=CREDENTIALS).status)

    def test_conversation_id_in_assistant_path_must_match_exactly(self) -> None:
        foreign = (self.OTHER, self.OTHER_KEY)
        for path in (
            f"assistant/{CONVERSATION.upper()}/",
            f"assistant/{CONVERSATION}-x/",
            f"assistant/%20{CONVERSATION}/",
            f"assistant/{CONVERSATION}%20/",
        ):
            self.assertIn(self.relay.dav("PROPFIND", path, basic=foreign, headers={"Depth": "1"}).status, (400, 401, 403, 404))


class TraversalTests(unittest.TestCase):
    """No encoded path may escape the conversation root or the data directory."""

    def setUp(self) -> None:
        self.relay = RelayHarness()
        self.addCleanup(self.relay.stop)
        self.assertEqual(201, self.relay.conversations(CREDENTIALS).status)

    def test_traversal_paths_are_rejected(self) -> None:
        attacks = [
            "assistant/../../etc/passwd",
            "assistant/%2e%2e/%2e%2e/etc/passwd",
            "assistant/..%2f..%2fetc/passwd",
            "assistant/%2e%2e%2f%2e%2e%2fetc%2fpasswd",
            "..%5c..%5cwindows",
            "assistant//etc/passwd",
            "assistant/./payload",
            "assistant/%00/payload",
            "assistant/" + "a" * 200 + "/payload",
            "/".join(["deep"] * 12) + "/payload",
        ]
        for path in attacks:
            response = self.relay.dav("PUT", path, basic=CREDENTIALS, body=b"attack")
            self.assertIn(response.status, (400, 403, 404), path)
            self.assertNotEqual(201, response.status, path)

    def test_nothing_is_written_outside_the_conversation_root(self) -> None:
        self.relay.dav("PUT", "assistant/../../escaped", basic=CREDENTIALS, body=b"escape")
        self.relay.dav("MKCOL", "assistant/../../escaped-dir/", basic=CREDENTIALS)
        conversations = self.relay.data_dir / "conversations"
        for entry in conversations.rglob("*"):
            if entry.name.startswith(".mpt-relay-upload-"):
                continue
            self.assertTrue(
                str(entry).startswith(str(conversations / CONVERSATION)),
                f"{entry} escaped the conversation root",
            )
        self.assertFalse((self.relay.data_dir / "escaped").exists())
        self.assertFalse((self.relay.data_dir / "escaped-dir").exists())


class AtomicUploadTests(unittest.TestCase):
    def setUp(self) -> None:
        self.relay = RelayHarness()
        self.addCleanup(self.relay.stop)
        self.assertEqual(201, self.relay.conversations(CREDENTIALS).status)
        self.relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
        self.relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
        self.relay.dav("MKCOL", item_root(), basic=CREDENTIALS)

    def test_cancelled_upload_leaves_no_visible_file(self) -> None:
        declared = 4 * 1024 * 1024
        self.relay.partial_put(f"{DAV_ROOT}{item_root()}payload", CREDENTIALS, declared, b"A" * 4096)

        def settled() -> bool:
            return not list(self.relay.data_dir.rglob(".mpt-relay-upload-*"))

        self.assertTrue(self.relay.wait_for(settled), "upload temp file was never cleaned up")

        self.assertEqual(404, self.relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).status)
        listing = self.relay.dav("PROPFIND", item_root(), basic=CREDENTIALS, headers={"Depth": "1"})
        self.assertEqual([f"{DAV_ROOT}assistant/{CONVERSATION}/{ITEM}/"], hrefs(listing.body))
        self.assertNotIn("payload", listing.text())
        # The namespace is still perfectly usable afterwards.
        self.assertEqual(201, self.relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"complete").status)
        self.assertEqual(b"complete", self.relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).body)

    def test_cancelled_overwrite_keeps_the_previous_bytes(self) -> None:
        self.assertEqual(201, self.relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"original").status)
        self.relay.partial_put(f"{DAV_ROOT}{item_root()}payload", CREDENTIALS, 1024 * 1024, b"B" * 1024)
        self.assertTrue(self.relay.wait_for(lambda: not list(self.relay.data_dir.rglob(".mpt-relay-upload-*"))))
        self.assertEqual(b"original", self.relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).body)

    def test_chunked_upload_without_content_length(self) -> None:
        connection = self.relay.raw_connection()
        try:
            head = (
                f"PUT {DAV_ROOT}{item_root()}payload HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{self.relay.port}\r\n"
                f"Authorization: {self._basic()}\r\n"
                f"Transfer-Encoding: chunked\r\n"
                f"Connection: close\r\n\r\n"
            ).encode("ascii")
            connection.sendall(head)
            for chunk in (b"hello ", b"chunked ", b"world"):
                connection.sendall(f"{len(chunk):x}\r\n".encode("ascii") + chunk + b"\r\n")
            connection.sendall(b"0\r\n\r\n")
            response = connection.recv(4096)
            self.assertIn(b"201", response.split(b"\r\n", 1)[0])
        finally:
            connection.close()
        self.assertEqual(b"hello chunked world", self.relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).body)

    def test_large_streamed_file_round_trips(self) -> None:
        data = os.urandom(3 * 1024 * 1024)
        self.assertEqual(201, self.relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=data).status)
        self.assertEqual(hashlib.sha256(data).hexdigest(), hashlib.sha256(self.relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).body).hexdigest())

    def _basic(self) -> str:
        import base64

        return "Basic " + base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()


class QuotaTests(unittest.TestCase):
    def test_per_conversation_quota_and_single_file_limit(self) -> None:
        with RelayHarness(per_conversation_bytes=1500, max_file_bytes=1024) as relay:
            relay.conversations(CREDENTIALS)
            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            relay.dav("MKCOL", item_root(), basic=CREDENTIALS)
            relay.dav("MKCOL", item_root(ITEM_TWO), basic=CREDENTIALS)

            first = b"a" * 900
            self.assertEqual(201, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=first).status)
            over_quota = relay.dav("PUT", item_root(ITEM_TWO) + "payload", basic=CREDENTIALS, body=b"b" * 900)
            self.assertEqual(507, over_quota.status)
            too_large = relay.dav("PUT", item_root(ITEM_TWO) + "payload", basic=CREDENTIALS, body=b"b" * 1100)
            self.assertEqual(413, too_large.status)

            # A failed upload never deletes or damages what is already stored.
            self.assertEqual(first, relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).body)
            self.assertEqual(404, relay.dav("GET", item_root(ITEM_TWO) + "payload", basic=CREDENTIALS).status)
            # Replacing a file counts only the delta, so the same slot stays writable...
            self.assertEqual(204, relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"a" * 950).status)
            # ...but a second file still has to fit next to it.
            self.assertEqual(507, relay.dav("PUT", item_root(ITEM_TWO) + "payload", basic=CREDENTIALS, body=b"b" * 600).status)

    def test_global_quota_is_enforced_across_conversations(self) -> None:
        other = "self-bbbb2222"
        other_key = hashlib.sha256(b"conversation-b").hexdigest()
        other_credentials = (other, other_key)
        with RelayHarness(per_conversation_bytes=4096, max_file_bytes=4096, global_bytes=2000) as relay:
            for conversation, conversation_key in ((CONVERSATION, CONVERSATION_KEY), other_credentials):
                relay.conversations((conversation, conversation_key))
                relay.dav("MKCOL", "assistant/", basic=(conversation, conversation_key))
                relay.dav("MKCOL", f"assistant/{conversation}/", basic=(conversation, conversation_key))
                for item in (ITEM, ITEM_TWO):
                    relay.dav("MKCOL", f"assistant/{conversation}/{item}/", basic=(conversation, conversation_key))

            self.assertEqual(
                201,
                relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"x" * 1500).status,
            )
            self.assertEqual(
                201,
                relay.dav("PUT", f"assistant/{other}/{ITEM}/payload", basic=other_credentials, body=b"y" * 400).status,
            )
            blocked = relay.dav(
                "PUT", f"assistant/{other}/{ITEM_TWO}/payload", basic=other_credentials, body=b"y" * 400
            )
            self.assertEqual(507, blocked.status)
            self.assertNotEqual(b"", blocked.body)
            self.assertEqual(b"x" * 1500, relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).body)
            self.assertEqual(b"y" * 400, relay.dav("GET", f"assistant/{other}/{ITEM}/payload", basic=other_credentials).body)

    def test_declared_oversize_is_refused_before_the_body_is_read(self) -> None:
        with RelayHarness(max_file_bytes=1024, per_conversation_bytes=1024) as relay:
            relay.conversations(CREDENTIALS)
            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            relay.dav("MKCOL", item_root(), basic=CREDENTIALS)
            connection = self.relay_put(relay, item_root() + "payload", 10 * 1024 * 1024)
            try:
                # The server answers 413 without waiting for the 10 MiB body.
                response = connection.recv(4096)
                self.assertIn(b"413", response.split(b"\r\n", 1)[0])
            finally:
                connection.close()
            self.assertEqual(404, relay.dav("GET", item_root() + "payload", basic=CREDENTIALS).status)

    @staticmethod
    def relay_put(relay: RelayHarness, path: str, declared: int) -> socket.socket:
        import base64

        connection = relay.raw_connection()
        token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
        connection.sendall(
            (
                f"PUT {DAV_ROOT}{path} HTTP/1.1\r\nHost: 127.0.0.1:{relay.port}\r\n"
                f"Authorization: Basic {token}\r\nContent-Length: {declared}\r\nConnection: close\r\n\r\n"
            ).encode("ascii")
        )
        return connection


class MaintenanceTests(unittest.TestCase):
    def test_stale_temp_files_are_swept_on_start(self) -> None:
        with RelayHarness() as relay:
            relay.conversations(CREDENTIALS)
            leftover = relay.data_dir / "conversations" / CONVERSATION / ".mpt-relay-upload-deadbeef"
            leftover.write_bytes(b"crash leftover")
            self.assertTrue(leftover.exists())
            relay.stop()
            restarted = RelayHarness(keep_dir=relay.root)
            try:
                self.assertFalse(leftover.exists())
                self.assertTrue(restarted.wait_for(lambda: not leftover.exists()))
            finally:
                restarted.stop()

    def test_usage_is_rebuilt_from_disk_after_restart(self) -> None:
        with RelayHarness(per_conversation_bytes=1000, max_file_bytes=1000) as relay:
            relay.conversations(CREDENTIALS)
            relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
            relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
            relay.dav("MKCOL", item_root(), basic=CREDENTIALS)
            relay.dav("PUT", item_root() + "payload", basic=CREDENTIALS, body=b"z" * 700)
            # Simulate a crash between the file write and the usage commit.
            relay.store.apply_usage(CONVERSATION, 0, 700)
            relay.stop()
            restarted = RelayHarness(keep_dir=relay.root, per_conversation_bytes=1000, max_file_bytes=1000)
            try:
                self.assertEqual(700, restarted.store.usage(CONVERSATION))
                restarted.dav("MKCOL", "assistant/", basic=CREDENTIALS)
                restarted.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
                restarted.dav("MKCOL", item_root(ITEM_TWO), basic=CREDENTIALS)
                self.assertEqual(
                    507,
                    restarted.dav("PUT", item_root(ITEM_TWO) + "payload", basic=CREDENTIALS, body=b"z" * 400).status,
                )
                self.assertEqual(700, len(restarted.dav("GET", item_root() + "payload", basic=CREDENTIALS).body))
            finally:
                restarted.stop()


if __name__ == "__main__":
    unittest.main()

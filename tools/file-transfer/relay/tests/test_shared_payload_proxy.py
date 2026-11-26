"""Shared payload proxy: an old V1 DAV client reads a Tail-only payload through the public relay.

Every test talks HTTP/1.1 over real loopback sockets. The transparent path uses two fully
assembled relays (``RelayHarness``); the fault paths use a controllable HTTP origin that can
redirect, misdeclare lengths, truncate a body or be abandoned mid-stream. Nothing inside the
relay is stubbed.
"""

from __future__ import annotations

import base64
import hashlib
import http.client
import json
import socket
import struct
import threading
import time
import unittest
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Dict, List, Optional, Tuple

from relay_testkit import DAV_ROOT, RelayHarness, free_port

from mpt_relay.auth import encode_basic
from mpt_relay.config import TRUSTED_TAIL_HOST, Config, ConfigError
from mpt_relay.shared_payload import (
    MAX_LOCATOR_BYTES,
    MAX_REQUEST_BYTES,
    REQUEST_DEVICE,
    REQUEST_REASON,
    TAIL_AUTH_REJECTED,
    UPSTREAM_ERROR_TEXT,
    UPSTREAM_HOP_HEADER,
)

CONVERSATION = "self-aaaa1111"
CONVERSATION_KEY = hashlib.sha256(b"shared-conversation").hexdigest()
CREDENTIALS = (CONVERSATION, CONVERSATION_KEY)
OTHER_CONVERSATION = "self-bbbb2222"
OTHER_KEY = hashlib.sha256(b"other-conversation").hexdigest()
WRONG_KEY = hashlib.sha256(b"wrong-key").hexdigest()
ITEM = "0123456789abcdef0123456789abcdef"
OTHER_ITEM = "fedcba9876543210fedcba9876543210"
PAYLOAD = b"tail-only-" + bytes(range(256)) * 48


def item_id_for(name: str) -> str:
    return hashlib.sha256(name.encode("utf-8")).hexdigest()[:32]


def payload_path(item: str = ITEM) -> str:
    """DAV-relative path, for ``RelayHarness.dav``."""
    return f"assistant/{CONVERSATION}/{item}/payload"


def payload_url(item: str = ITEM) -> str:
    """Absolute URL path, for raw sockets and ``http.client``."""
    return DAV_ROOT + payload_path(item)


def locator_relative(item: str = ITEM, owner: str = CONVERSATION) -> str:
    return f"assistant-locator/{owner}/{item}/manifest.json"


def manifest(message_id: str = ITEM, *, size: int = len(PAYLOAD), **overrides: object) -> dict:
    record = {
        "version": 1,
        "id": message_id,
        "kind": "file",
        "text": None,
        "name": "shared.bin",
        "size": size,
        "createdAt": "2026-09-29T04:00:00Z",
        "senderDeviceId": "sender-device",
        "senderName": "发送端",
    }
    record.update(overrides)
    return record


def locator(
    message_id: str = ITEM,
    *,
    message: Optional[dict] = None,
    route: str = "mpt-tail-relay-v1",
    version: int = 1,
    **overrides: object,
) -> dict:
    record = {
        "version": version,
        "message": manifest(message_id) if message is None else message,
        "payloadRoute": route,
    }
    record.update(overrides)
    return record


def locator_bytes(message_id: str = ITEM, *, size: Optional[int] = None, **kwargs: object) -> bytes:
    message = kwargs.pop("message", None)
    if message is None and size is not None:
        message = manifest(message_id, size=size)
    return json.dumps(locator(message_id, message=message, **kwargs)).encode("utf-8")


def seed_tail(relay: RelayHarness, payload: bytes = PAYLOAD, credentials=CREDENTIALS, item: str = ITEM) -> None:
    relay.conversations(credentials)
    relay.dav("MKCOL", "assistant/", basic=credentials)
    relay.dav("MKCOL", f"assistant/{credentials[0]}/", basic=credentials)
    relay.dav("MKCOL", f"assistant/{credentials[0]}/{item}/", basic=credentials)
    put = relay.dav("PUT", f"assistant/{credentials[0]}/{item}/payload", basic=credentials, body=payload)
    if put.status != 201:
        raise AssertionError(f"tail payload PUT failed: {put.status}")
    record = json.dumps(manifest(item, size=len(payload))).encode("utf-8")
    ready = relay.dav("PUT", f"assistant/{credentials[0]}/{item}/manifest.json", basic=credentials, body=record)
    if ready.status != 201:
        raise AssertionError(f"tail manifest PUT failed: {ready.status}")


def put_locator(
    relay: RelayHarness,
    credentials,
    raw: bytes,
    item: str = ITEM,
    owner: Optional[str] = None,
) -> int:
    owner = credentials[0] if owner is None else owner
    relay.dav("MKCOL", "assistant-locator/", basic=credentials)
    relay.dav("MKCOL", f"assistant-locator/{owner}/", basic=credentials)
    relay.dav("MKCOL", f"assistant-locator/{owner}/{item}/", basic=credentials)
    return relay.dav(
        "PUT", f"assistant-locator/{owner}/{item}/manifest.json", basic=credentials, body=raw
    ).status


def public_payload_file(relay: RelayHarness, item: str = ITEM):
    return relay.data_dir / "conversations" / CONVERSATION / "assistant" / CONVERSATION / item / "payload"


def fallback_request_file(relay: RelayHarness, item: str = ITEM, conversation: str = CONVERSATION):
    return (
        relay.data_dir
        / "conversations"
        / conversation
        / "assistant-locator"
        / conversation
        / item
        / "requests"
        / f"{REQUEST_DEVICE}.json"
    )


def put_request(relay: RelayHarness, raw: bytes, item: str = ITEM, credentials=CREDENTIALS) -> int:
    base = f"assistant-locator/{credentials[0]}/{item}/requests"
    relay.dav("MKCOL", f"assistant-locator/{credentials[0]}/{item}/", basic=credentials)
    relay.dav("MKCOL", base + "/", basic=credentials)
    return relay.dav("PUT", f"{base}/{REQUEST_DEVICE}.json", basic=credentials, body=raw).status


def payload_gets(relay: RelayHarness) -> List[str]:
    return [line for line in relay.logs() if " GET " in line and "/payload" in line]


def raw_get(relay: RelayHarness, path: str, credentials=CREDENTIALS, headers: Optional[Dict[str, str]] = None):
    connection = relay.raw_connection(timeout=20)
    extra = "".join(f"{name}: {value}\r\n" for name, value in (headers or {}).items())
    connection.sendall(
        (
            f"GET {path} HTTP/1.1\r\n"
            f"Host: 127.0.0.1:{relay.port}\r\n"
            f"Authorization: {encode_basic(*credentials)}\r\n"
            f"{extra}"
            f"Connection: close\r\n\r\n"
        ).encode("ascii")
    )
    return connection


class FaultyUpstream:
    """A controllable HTTP origin: happy path plus redirects, wrong lengths and aborts.

    Every request is recorded (method, path, headers) so a test can prove a rejected request
    never reached the upstream, or that only the expected headers travelled. ``continue_event``
    pauses the body after the first chunk, which is what proves the proxy streams instead of
    buffering a whole file.
    """

    def __init__(self) -> None:
        self.requests: List[Tuple[str, str, Dict[str, str]]] = []
        self.lock = threading.Lock()
        self.status = 200
        self.chunks: List[bytes] = [PAYLOAD]
        self.declared_length: Optional[int] = None
        self.omit_length = False
        self.reset_on_short = False
        self.location: Optional[str] = None
        self.chunk_delay = 0.0
        self.continue_event: Optional[threading.Event] = None
        self.completed = threading.Event()
        self.aborted = threading.Event()
        self.sent_bytes = 0
        self._server = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self._server.daemon_threads = True
        self._thread = threading.Thread(target=self._server.serve_forever, name="faulty-upstream", daemon=True)
        self._thread.start()

    @property
    def port(self) -> int:
        return int(self._server.server_address[1])

    def stop(self) -> None:
        try:
            self._server.shutdown()
        except Exception:  # noqa: BLE001 - already stopped
            pass
        try:
            self._server.server_close()
        except Exception:  # noqa: BLE001
            pass

    def _handler(self):
        upstream = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, format: str, *args: object) -> None:  # noqa: A002 - stdlib signature
                pass

            def do_GET(self) -> None:  # noqa: N802 - stdlib naming
                self._serve(with_body=True)

            def do_HEAD(self) -> None:  # noqa: N802
                self._serve(with_body=False)

            def _serve(self, with_body: bool) -> None:
                headers = {key.lower(): value for key, value in self.headers.items()}
                with upstream.lock:
                    upstream.requests.append((self.command, self.path, headers))
                    upstream.sent_bytes = 0
                if upstream.location is not None:
                    self.send_response(302)
                    self.send_header("Location", upstream.location)
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                if upstream.status != 200:
                    body = b"upstream-error-body"
                    self.send_response(upstream.status)
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    if with_body:
                        self.wfile.write(body)
                    return
                chunks = list(upstream.chunks)
                body_length = sum(len(chunk) for chunk in chunks)
                length = upstream.declared_length if upstream.declared_length is not None else body_length
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                if not upstream.omit_length:
                    self.send_header("Content-Length", str(length))
                else:
                    self.close_connection = True
                self.end_headers()
                if not with_body:
                    upstream.completed.set()
                    return
                short = not upstream.omit_length and length != body_length
                if short:
                    # Announce more than will be sent, then close, so a proxy that trusts the
                    # header has to notice the short body.
                    self.close_connection = True
                    if upstream.reset_on_short:
                        try:
                            self.connection.setsockopt(
                                socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0)
                            )
                        except OSError:
                            pass
                try:
                    for index, chunk in enumerate(chunks):
                        if index and upstream.continue_event is not None:
                            upstream.continue_event.wait(timeout=15)
                        if upstream.chunk_delay:
                            time.sleep(upstream.chunk_delay)
                        self.wfile.write(chunk)
                        self.wfile.flush()
                        with upstream.lock:
                            upstream.sent_bytes += len(chunk)
                    if not short:
                        upstream.completed.set()
                except (BrokenPipeError, ConnectionResetError, OSError):
                    upstream.aborted.set()

        return Handler


class ProxyTestCase(unittest.TestCase):
    """Harness helpers shared by the proxy tests."""

    def start_relay(self, **options: object) -> RelayHarness:
        relay = RelayHarness(**options)
        self.addCleanup(relay.stop)
        return relay

    def start_origin(self) -> FaultyUpstream:
        origin = FaultyUpstream()
        self.addCleanup(origin.stop)
        return origin

    def proxy_relay(self, origin: Optional[FaultyUpstream] = None, *, proxy: bool = True, **options: object):
        if origin is not None:
            options["tail_payload_origin"] = f"http://127.0.0.1:{origin.port}"
        if proxy:
            options["tail_payload_proxy"] = 1
        return self.start_relay(**options)

    def proxy_relay_for(self, origin: FaultyUpstream, size: int, item: str = ITEM, **options: object):
        relay = self.proxy_relay(origin, **options)
        relay.conversations(CREDENTIALS)
        self.assertEqual(201, put_locator(relay, CREDENTIALS, locator_bytes(item, size=size), item=item))
        return relay

    def tail_and_public(self, payload: bytes = PAYLOAD, **public_options: object):
        tail = self.start_relay()
        seed_tail(tail, payload)
        public = self.start_relay(
            tail_payload_proxy=1,
            tail_payload_origin=f"http://127.0.0.1:{tail.port}",
            **public_options,
        )
        public.conversations(CREDENTIALS)
        self.assertEqual(201, put_locator(public, CREDENTIALS, locator_bytes(size=len(payload))))
        return tail, public


class OldClientTransparencyTests(ProxyTestCase):
    """The V1 request sequence must keep working with the payload only on the Tail relay."""

    def test_old_style_get_returns_tail_bytes_without_a_public_payload_copy(self) -> None:
        tail, public = self.tail_and_public()
        before = len(payload_gets(tail))

        response = public.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(200, response.status)
        self.assertEqual(PAYLOAD, response.body)
        self.assertEqual(str(len(PAYLOAD)), response.header("content-length"))
        self.assertEqual("application/octet-stream", response.header("content-type"))
        self.assertTrue(
            tail.wait_for(lambda: len(payload_gets(tail)) == before + 1, timeout=5.0),
            "the Tail relay must have served the bytes",
        )
        self.assertFalse(public_payload_file(public).exists(), "the proxy must not copy the payload to public disk")
        self.assertLess(public.store.global_usage(), len(PAYLOAD), "only the small locator may be stored public")

    def test_head_reports_the_tail_size_without_bytes_or_a_public_copy(self) -> None:
        _tail, public = self.tail_and_public()

        response = public.dav("HEAD", payload_path(), basic=CREDENTIALS)

        self.assertEqual(200, response.status)
        self.assertEqual(b"", response.body)
        self.assertEqual(str(len(PAYLOAD)), response.header("content-length"))
        self.assertEqual("application/octet-stream", response.header("content-type"))
        self.assertFalse(public_payload_file(public).exists())

    def test_multimegabyte_payload_round_trips_without_a_public_copy(self) -> None:
        payload = bytes(range(256)) * (12 << 10)  # 3 MiB
        _tail, public = self.tail_and_public(payload=payload)

        response = public.dav("GET", payload_path(), basic=CREDENTIALS, timeout=60)

        self.assertEqual(200, response.status)
        self.assertEqual(len(payload), len(response.body))
        self.assertEqual(hashlib.sha256(payload).hexdigest(), hashlib.sha256(response.body).hexdigest())
        self.assertFalse(public_payload_file(public).exists())

    def test_committed_local_payload_wins_and_tail_is_never_contacted(self) -> None:
        tail, public = self.tail_and_public()
        public.dav("MKCOL", "assistant/", basic=CREDENTIALS)
        public.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
        public.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS)
        local = b"public-fallback-copy"
        self.assertEqual(201, public.dav("PUT", payload_path(), basic=CREDENTIALS, body=local).status)

        before = len(payload_gets(tail))
        served = public.dav("GET", payload_path(), basic=CREDENTIALS)
        self.assertEqual(200, served.status)
        self.assertEqual(local, served.body)
        self.assertEqual(before, len(payload_gets(tail)), "a local payload must not trigger an upstream fetch")

        # With the Tail relay gone the same GET must still succeed from the local copy.
        tail.stop()
        after_stop = public.dav("GET", payload_path(), basic=CREDENTIALS)
        self.assertEqual(200, after_stop.status)
        self.assertEqual(local, after_stop.body)

    def test_old_client_discovers_the_public_v1_manifest_and_gets_tail_bytes(self) -> None:
        _tail, public = self.tail_and_public()
        public.dav("MKCOL", "assistant/", basic=CREDENTIALS)
        public.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
        public.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS)
        published = public.dav(
            "PUT",
            f"assistant/{CONVERSATION}/{ITEM}/manifest.json",
            basic=CREDENTIALS,
            body=json.dumps(manifest()).encode("utf-8"),
        )
        self.assertEqual(201, published.status)

        listing = public.dav("PROPFIND", f"assistant/{CONVERSATION}/", basic=CREDENTIALS, headers={"Depth": "1"})
        self.assertEqual(207, listing.status)
        self.assertIn(f"assistant/{CONVERSATION}/{ITEM}/", listing.text())

        fetched = public.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(200, fetched.status)
        self.assertEqual(PAYLOAD, fetched.body)
        self.assertFalse(public_payload_file(public).exists())

    def test_old_client_fetches_a_tail_only_image_payload(self) -> None:
        _tail, public = self.tail_and_public()
        image_locator = locator_bytes(message=manifest(kind="image", name="shared.png"))
        self.assertIn(put_locator(public, CREDENTIALS, image_locator), (201, 204))

        response = public.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(200, response.status)
        self.assertEqual(PAYLOAD, response.body)
        self.assertFalse(public_payload_file(public).exists())

    def test_locator_file_itself_stays_a_plain_dav_resource(self) -> None:
        _tail, public = self.tail_and_public()

        response = public.dav("GET", locator_relative(), basic=CREDENTIALS)

        self.assertEqual(200, response.status)
        self.assertEqual(locator_bytes(size=len(PAYLOAD)), response.body)

    def test_locator_publication_drives_changes_and_a_proxy_get_does_not(self) -> None:
        _tail, public = self.tail_and_public()

        revision = public.store.revision(CONVERSATION)
        self.assertGreaterEqual(revision, 1, "publishing the locator is an existing small-signal revision")
        self.assertEqual(revision, public.changes(CREDENTIALS, since=0).json()["revision"])

        self.assertEqual(200, public.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertEqual(revision, public.store.revision(CONVERSATION), "a proxied payload read is silent")
        self.assertEqual(revision, public.changes(CREDENTIALS).json()["revision"])


class LocatorValidationTests(ProxyTestCase):
    """Only a well-formed locator inside the authenticated namespace may open an upstream fetch."""

    def test_invalid_locator_records_never_open_the_upstream(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay(origin)
        relay.conversations(CREDENTIALS)

        def builders(item: str) -> Dict[str, bytes]:
            return {
                "wrong-route": locator_bytes(item, route="mpt-tail-relay-v2"),
                "locator-version": locator_bytes(item, version=2),
                "message-version": locator_bytes(item, message=manifest(item, version=2)),
                "id-mismatch": locator_bytes(item, message=manifest(OTHER_ITEM)),
                "kind-text": locator_bytes(item, message=manifest(item, kind="text")),
                "kind-other": locator_bytes(item, message=manifest(item, kind="video")),
                "private-target": locator_bytes(item, message=manifest(item, targetDeviceId="device-9")),
                "private-empty": locator_bytes(item, message=manifest(item, targetDeviceId="")),
                "negative-size": locator_bytes(item, message=manifest(item, size=-1)),
                "string-size": locator_bytes(item, message=manifest(item, size="big")),
                "bool-size": locator_bytes(item, message=manifest(item, size=True)),
                "missing-message": json.dumps({"version": 1, "payloadRoute": "mpt-tail-relay-v1"}).encode(),
                "not-object": b"[1, 2, 3]",
                "not-json": b"{not json",
                "empty": b"",
                "oversize": b"x" * (MAX_LOCATOR_BYTES + 1),
            }

        for name in builders("placeholder"):
            with self.subTest(case=name):
                item = item_id_for(name)
                self.assertEqual(201, put_locator(relay, CREDENTIALS, builders(item)[name], item=item), name)
                response = relay.dav("GET", payload_path(item), basic=CREDENTIALS)
                self.assertEqual(404, response.status, name)

        self.assertEqual([], origin.requests, "no invalid locator may reach the upstream")

    def test_locator_size_over_the_service_limit_is_rejected(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay(
            origin, max_file_bytes=2048, per_conversation_bytes=8192, global_bytes=16384
        )
        relay.conversations(CREDENTIALS)

        self.assertEqual(201, put_locator(relay, CREDENTIALS, locator_bytes(size=4096)))

        self.assertEqual(404, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual([], origin.requests)

    def test_locator_under_another_conversation_is_ignored(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay(origin)
        relay.conversations(CREDENTIALS)

        # A valid-looking record parked under another conversation's locator root is never read.
        self.assertEqual(
            201,
            put_locator(relay, CREDENTIALS, locator_bytes(), owner=OTHER_CONVERSATION),
        )

        self.assertEqual(404, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual([], origin.requests)

    def test_missing_locator_keeps_the_plain_404(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay(origin)
        relay.conversations(CREDENTIALS)

        self.assertEqual(404, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual(404, relay.dav("HEAD", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual([], origin.requests)


class DenialTests(ProxyTestCase):
    """The proxy must not widen the existing auth, namespace or path rules."""

    def test_authentication_failures_never_reach_the_upstream(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(401, relay.dav("GET", payload_path()).status)
        self.assertEqual(401, relay.dav("GET", payload_path(), basic=(CONVERSATION, WRONG_KEY)).status)
        self.assertEqual(401, relay.dav("HEAD", payload_path(), basic=(CONVERSATION, WRONG_KEY)).status)
        self.assertEqual([], origin.requests)

    def test_cross_namespace_and_path_confusion_stay_denied(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay_for(origin, len(PAYLOAD))
        relay.conversations((OTHER_CONVERSATION, OTHER_KEY))

        self.assertEqual(403, relay.dav("GET", f"assistant/{OTHER_CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)
        self.assertEqual(403, relay.dav("HEAD", f"assistant/{OTHER_CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status)
        # The same rule holds in the other direction: the other namespace cannot reach this payload.
        self.assertEqual(
            403,
            relay.dav("GET", payload_path(), basic=(OTHER_CONVERSATION, OTHER_KEY)).status,
        )
        # A locator lives inside one conversation root only: the other namespace sees neither this
        # conversation's locator nor a proxied payload of its own with the same item id.
        self.assertEqual(
            404,
            relay.dav("GET", locator_relative(), basic=(OTHER_CONVERSATION, OTHER_KEY)).status,
        )
        self.assertEqual(
            404,
            relay.dav("GET", f"assistant/{OTHER_CONVERSATION}/{ITEM}/payload", basic=(OTHER_CONVERSATION, OTHER_KEY)).status,
        )
        self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{OTHER_ITEM}/payload", basic=CREDENTIALS).status)
        self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/not-a-guid/payload", basic=CREDENTIALS).status)
        self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM.upper()}/payload", basic=CREDENTIALS).status)
        self.assertEqual(404, relay.dav("GET", f"{payload_path()}/", basic=CREDENTIALS).status)
        self.assertEqual(404, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/manifest.json", basic=CREDENTIALS).status)
        self.assertEqual(400, relay.dav("GET", f"assistant/{CONVERSATION}/{ITEM}/payload%2F..%2Fmanifest.json", basic=CREDENTIALS).status)
        self.assertEqual(400, relay.dav("GET", f"assistant/{CONVERSATION}/..%2F{ITEM}/payload", basic=CREDENTIALS).status)

        self.assertEqual([], origin.requests, "a denied request must never reach the upstream")

    def test_hop_marker_prevents_a_proxy_loop(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        response = relay.dav("GET", payload_path(), basic=CREDENTIALS, headers={UPSTREAM_HOP_HEADER: "1"})

        self.assertEqual(404, response.status)
        self.assertEqual([], origin.requests)


class UpstreamFaultTests(ProxyTestCase):
    """Upstream behaviour is validated, bounded and never reflected beyond a safe status."""

    def test_redirect_is_rejected_and_the_foreign_host_never_receives_credentials(self) -> None:
        origin = self.start_origin()
        foreign = self.start_origin()
        origin.location = f"http://127.0.0.1:{foreign.port}/mpt/relay/dav/assistant/{CONVERSATION}/{ITEM}/payload"
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        response = relay.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(502, response.status)
        self.assertEqual(UPSTREAM_ERROR_TEXT.encode(), response.body)
        self.assertEqual([], foreign.requests, "a redirect target must never be contacted")
        self.assertEqual(1, len(origin.requests))
        self.assertEqual(encode_basic(*CREDENTIALS), origin.requests[0][2].get("authorization"))
        self.assertNotIn(CONVERSATION_KEY, response.text())

    def test_upstream_statuses_are_mapped_and_redacted(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        for status in (301, 302, 500, 503):
            with self.subTest(status=status):
                origin.status = status
                response = relay.dav("GET", payload_path(), basic=CREDENTIALS)
                self.assertEqual(502, response.status)
                self.assertEqual(UPSTREAM_ERROR_TEXT.encode(), response.body)
                self.assertNotIn(CONVERSATION_KEY, response.text())

        origin.status = 404
        missing = relay.dav("GET", payload_path(), basic=CREDENTIALS)
        self.assertEqual(404, missing.status)
        self.assertNotEqual(UPSTREAM_ERROR_TEXT.encode(), missing.body, "an upstream 404 is a normal missing payload")

    def test_tail_auth_rejection_is_403_with_a_fixed_marker_and_no_echo(self) -> None:
        for status in (401, 403):
            for method in ("GET", "HEAD"):
                with self.subTest(status=status, method=method):
                    origin = self.start_origin()
                    origin.status = status
                    relay = self.proxy_relay_for(origin, len(PAYLOAD))

                    response = relay.dav(method, payload_path(), basic=CREDENTIALS)

                    self.assertEqual(403, response.status)
                    self.assertEqual(TAIL_AUTH_REJECTED, response.header("x-mpt-relay-error"))
                    self.assertNotIn(CONVERSATION_KEY, response.text())
                    # The upstream error body is never echoed, and HEAD sends no body at all.
                    self.assertNotIn("upstream-error-body", response.text())
                    if method == "HEAD":
                        self.assertEqual(b"", response.body)
                    else:
                        self.assertEqual(TAIL_AUTH_REJECTED.encode() + b"\n", response.body)

    def test_wrong_or_missing_upstream_length_is_rejected_before_any_body(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD]
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        origin.declared_length = len(PAYLOAD) + 1
        response = relay.dav("GET", payload_path(), basic=CREDENTIALS)
        self.assertEqual(502, response.status)
        self.assertEqual(UPSTREAM_ERROR_TEXT.encode(), response.body)

        origin.omit_length = True
        missing = relay.dav("GET", payload_path(), basic=CREDENTIALS)
        self.assertEqual(502, missing.status)

        origin.omit_length = False
        origin.declared_length = None
        head = relay.dav("HEAD", payload_path(), basic=CREDENTIALS)
        self.assertEqual(200, head.status)
        self.assertEqual(str(len(PAYLOAD)), head.header("content-length"))
        self.assertEqual(b"", head.body)

    def test_short_upstream_body_never_reports_success(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD[:32]]
        origin.declared_length = len(PAYLOAD)
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        with self.assertRaises(http.client.IncompleteRead):
            relay.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertFalse(public_payload_file(relay).exists())

    def test_unreachable_upstream_is_a_safe_502(self) -> None:
        relay = self.proxy_relay(None, tail_payload_origin=f"http://127.0.0.1:{free_port()}")
        relay.conversations(CREDENTIALS)
        self.assertEqual(201, put_locator(relay, CREDENTIALS, locator_bytes(size=len(PAYLOAD))))

        response = relay.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(502, response.status)
        self.assertEqual(UPSTREAM_ERROR_TEXT.encode(), response.body)
        self.assertNotIn(CONVERSATION_KEY, response.text())

    def test_client_disconnect_closes_the_upstream(self) -> None:
        origin = self.start_origin()
        chunk = b"z" * (64 << 10)
        origin.chunks = [chunk] * 256  # 16 MiB, far more than any socket buffer
        origin.declared_length = 256 * len(chunk)
        origin.chunk_delay = 0.005
        relay = self.proxy_relay_for(origin, 256 * len(chunk))

        connection = raw_get(relay, payload_url())
        try:
            self.assertTrue(connection.recv(65536), "no response arrived")
        finally:
            connection.close()

        deadline = time.monotonic() + 10.0
        while time.monotonic() < deadline and not origin.aborted.is_set() and not origin.completed.is_set():
            time.sleep(0.02)
        self.assertTrue(origin.aborted.is_set(), "the upstream connection was not closed with the client")
        self.assertFalse(
            fallback_request_file(relay).exists(),
            "a client disconnect is not a Tail failure and must not record a request",
        )


class FallbackRequestTests(ProxyTestCase):
    """A Tail-unavailable GET records exactly one small request for a public copy."""

    def test_upstream_failure_records_one_bounded_request_and_wakes_changes(self) -> None:
        origin = self.start_origin()
        origin.status = 500
        relay = self.proxy_relay_for(origin, len(PAYLOAD))
        before = relay.store.revision(CONVERSATION)
        waited: List[object] = []

        def poll() -> None:
            waited.append(relay.changes(CREDENTIALS, since=before))

        poller = threading.Thread(target=poll, daemon=True)
        poller.start()
        time.sleep(0.3)  # let the long poll register on the old revision
        response = relay.dav("GET", payload_path(), basic=CREDENTIALS)
        poller.join(timeout=5.0)

        self.assertEqual(502, response.status)
        self.assertTrue(waited, "the long poll returned no result")
        self.assertFalse(poller.is_alive(), "the recorded request must wake the existing change cursor")
        self.assertEqual(before + 1, waited[0].json()["revision"])

        path = fallback_request_file(relay)
        self.assertTrue(path.is_file())
        raw = path.read_bytes()
        self.assertLessEqual(len(raw), MAX_REQUEST_BYTES, "the request record must stay within 2 KiB")
        record = json.loads(raw)
        self.assertEqual(1, record["version"])
        self.assertEqual(ITEM, record["itemId"])
        self.assertEqual(REQUEST_DEVICE, record["deviceId"])
        self.assertEqual(REQUEST_REASON, record["reason"])
        requested_at = datetime.fromisoformat(record["requestedAt"].replace("Z", "+00:00"))
        self.assertIsNotNone(requested_at.tzinfo)
        self.assertEqual(before + 1, relay.store.revision(CONVERSATION))
        # A request is not a receipt, and neither it nor the log may carry the conversation key.
        self.assertFalse((path.parent.parent / "receipts").exists())
        self.assertNotIn(CONVERSATION_KEY, raw.decode("utf-8"))
        self.assertNotIn(CONVERSATION_KEY, "\n".join(relay.logs()))

    def test_repeated_failures_do_not_rewrite_or_bump_revision(self) -> None:
        origin = self.start_origin()
        origin.status = 503
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(502, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)
        path = fallback_request_file(relay)
        first = path.read_bytes()
        revision = relay.store.revision(CONVERSATION)

        for _ in range(3):
            self.assertEqual(502, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertEqual(first, path.read_bytes(), "an existing valid request must not be rewritten")
        self.assertEqual(revision, relay.store.revision(CONVERSATION), "a repeat must not churn revisions")

    def test_an_existing_valid_request_written_elsewhere_is_left_untouched(self) -> None:
        origin = self.start_origin()
        origin.status = 500
        relay = self.proxy_relay_for(origin, len(PAYLOAD))
        existing = json.dumps(
            {
                "version": 1,
                "itemId": ITEM,
                "deviceId": REQUEST_DEVICE,
                "requestedAt": "2026-09-29T04:00:03Z",
                "reason": "tail-unavailable",
            }
        ).encode("utf-8")
        self.assertEqual(201, put_request(relay, existing))
        revision = relay.store.revision(CONVERSATION)

        self.assertEqual(502, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertEqual(existing, fallback_request_file(relay).read_bytes())
        self.assertEqual(revision, relay.store.revision(CONVERSATION))

    def test_a_local_payload_wins_and_records_nothing(self) -> None:
        origin = self.start_origin()
        origin.status = 500
        relay = self.proxy_relay_for(origin, len(PAYLOAD))
        relay.dav("MKCOL", "assistant/", basic=CREDENTIALS)
        relay.dav("MKCOL", f"assistant/{CONVERSATION}/", basic=CREDENTIALS)
        relay.dav("MKCOL", f"assistant/{CONVERSATION}/{ITEM}/", basic=CREDENTIALS)
        local = b"public-copy"
        self.assertEqual(201, relay.dav("PUT", payload_path(), basic=CREDENTIALS, body=local).status)
        revision = relay.store.revision(CONVERSATION)

        response = relay.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(200, response.status)
        self.assertEqual(local, response.body)
        self.assertEqual([], origin.requests, "a local payload must never consult the upstream")
        self.assertFalse(fallback_request_file(relay).exists())
        self.assertEqual(revision, relay.store.revision(CONVERSATION))

    def test_auth_and_locator_denials_never_record_requests(self) -> None:
        origin = self.start_origin()
        origin.status = 500
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(401, relay.dav("GET", payload_path()).status)
        self.assertEqual(401, relay.dav("GET", payload_path(), basic=(CONVERSATION, WRONG_KEY)).status)
        self.assertEqual(
            403,
            relay.dav("GET", f"assistant/{OTHER_CONVERSATION}/{ITEM}/payload", basic=CREDENTIALS).status,
        )
        # A malformed locator never opens the upstream, so it never records anything either.
        self.assertIn(put_locator(relay, CREDENTIALS, locator_bytes(route="mpt-tail-relay-v2")), (201, 204))
        revision = relay.store.revision(CONVERSATION)
        self.assertEqual(404, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertFalse(fallback_request_file(relay).exists())
        self.assertEqual(revision, relay.store.revision(CONVERSATION))

    def test_head_never_records_a_request(self) -> None:
        origin = self.start_origin()
        origin.status = 500
        relay = self.proxy_relay_for(origin, len(PAYLOAD))
        revision = relay.store.revision(CONVERSATION)

        self.assertEqual(502, relay.dav("HEAD", payload_path(), basic=CREDENTIALS).status)

        self.assertFalse(fallback_request_file(relay).exists(), "HEAD is a probe and must not mutate")
        self.assertEqual(revision, relay.store.revision(CONVERSATION))

    def test_non_unavailable_upstream_statuses_record_nothing(self) -> None:
        for status, expected in ((301, 502), (302, 502), (400, 502), (401, 403), (403, 403), (409, 502), (429, 502)):
            with self.subTest(status=status):
                origin = self.start_origin()
                origin.status = status
                relay = self.proxy_relay_for(origin, len(PAYLOAD))
                revision = relay.store.revision(CONVERSATION)

                response = relay.dav("GET", payload_path(), basic=CREDENTIALS)

                self.assertEqual(expected, response.status)
                self.assertFalse(fallback_request_file(relay).exists(), status)
                self.assertEqual(revision, relay.store.revision(CONVERSATION))

    def test_tail_auth_rejection_records_nothing_for_get_or_head(self) -> None:
        for status in (401, 403):
            for method in ("GET", "HEAD"):
                with self.subTest(status=status, method=method):
                    origin = self.start_origin()
                    origin.status = status
                    relay = self.proxy_relay_for(origin, len(PAYLOAD))
                    request_path = fallback_request_file(relay)
                    revision = relay.store.revision(CONVERSATION)

                    response = relay.dav(method, payload_path(), basic=CREDENTIALS)

                    self.assertEqual(403, response.status)
                    self.assertEqual(TAIL_AUTH_REJECTED, response.header("x-mpt-relay-error"))
                    # A Tail auth rejection is not "network unavailable": no copy request, no
                    # receipt, no revision churn, and nothing secret on disk or in the log.
                    self.assertFalse(request_path.exists())
                    self.assertFalse((request_path.parent.parent / "receipts").exists())
                    self.assertEqual(revision, relay.store.revision(CONVERSATION))
                    self.assertNotIn(CONVERSATION_KEY, "".join(relay.logs()))

    def test_redirect_to_a_foreign_host_records_nothing(self) -> None:
        origin = self.start_origin()
        foreign = self.start_origin()
        origin.location = f"http://127.0.0.1:{foreign.port}/mpt/relay/dav/assistant/{CONVERSATION}/{ITEM}/payload"
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(502, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertFalse(fallback_request_file(relay).exists())
        self.assertEqual([], foreign.requests)

    def test_connect_failure_records_a_request(self) -> None:
        relay = self.proxy_relay(None, tail_payload_origin=f"http://127.0.0.1:{free_port()}")
        relay.conversations(CREDENTIALS)
        self.assertEqual(201, put_locator(relay, CREDENTIALS, locator_bytes(size=len(PAYLOAD))))

        self.assertEqual(502, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertTrue(fallback_request_file(relay).is_file())

    def test_missing_tail_payload_records_a_request(self) -> None:
        origin = self.start_origin()
        origin.status = 404
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(404, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertTrue(fallback_request_file(relay).is_file())

    def test_invalid_upstream_length_records_a_request(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD]
        origin.declared_length = len(PAYLOAD) + 1
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(502, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        self.assertTrue(fallback_request_file(relay).is_file())

    def test_short_stream_records_a_request_before_the_stream_closes(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD[:32]]
        origin.declared_length = len(PAYLOAD)
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        with self.assertRaises(http.client.IncompleteRead):
            relay.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertTrue(
            relay.wait_for(lambda: fallback_request_file(relay).is_file(), timeout=5.0),
            "the stream-failure callback must record the request before closing",
        )
        record = json.loads(fallback_request_file(relay).read_bytes())
        self.assertEqual(REQUEST_REASON, record["reason"])
        self.assertEqual(ITEM, record["itemId"])

    def test_a_reset_upstream_records_a_request(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD[:64]]
        origin.declared_length = len(PAYLOAD)
        origin.reset_on_short = True
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        try:
            response = relay.dav("GET", payload_path(), basic=CREDENTIALS)
        except (http.client.HTTPException, OSError):
            pass  # a reset connection is exactly what the truncation path must surface
        else:
            self.assertNotEqual(len(PAYLOAD), len(response.body), "a reset upstream must not report success")

        self.assertTrue(
            relay.wait_for(lambda: fallback_request_file(relay).is_file(), timeout=5.0),
            "a read failure must record the request",
        )


class StreamingTests(ProxyTestCase):
    """Bytes move in bounded chunks: a paused upstream does not hold the client's first byte."""

    def test_large_payload_streams_before_the_upstream_finishes(self) -> None:
        origin = self.start_origin()
        first_chunk = b"a" * (64 << 10)
        rest = b"b" * (64 << 10)
        total = len(first_chunk) + 63 * len(rest)
        origin.chunks = [first_chunk] + [rest] * 63
        origin.declared_length = total
        origin.continue_event = threading.Event()
        relay = self.proxy_relay_for(origin, total)

        connection = http.client.HTTPConnection("127.0.0.1", relay.port, timeout=15)
        try:
            connection.request("GET", payload_url(), headers={"Authorization": encode_basic(*CREDENTIALS)})
            response = connection.getresponse()
            self.assertEqual(200, response.status)
            self.assertEqual(str(total), response.getheader("Content-Length"))
            head = response.read(1024)
            self.assertTrue(head, "no bytes arrived while the upstream was still paused")
            self.assertFalse(origin.continue_event.is_set(), "the whole body cannot have been buffered")
            origin.continue_event.set()
            remainder = response.read()
        finally:
            origin.continue_event.set()
            connection.close()

        self.assertEqual(first_chunk[:1024], head)
        self.assertEqual(total, len(head) + len(remainder))
        expected = hashlib.sha256(first_chunk + rest * 63).hexdigest()
        self.assertEqual(expected, hashlib.sha256(head + remainder).hexdigest())
        self.assertTrue(origin.completed.wait(5.0))
        self.assertFalse(origin.aborted.is_set())

    def test_only_the_authenticated_header_and_fixed_marker_travel_upstream(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD]
        origin.declared_length = len(PAYLOAD)
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        response = relay.dav(
            "GET",
            payload_path(),
            basic=CREDENTIALS,
            headers={"X-Client-Note": "drop-me", "Cookie": "session=1", "Range": "bytes=0-1"},
        )

        self.assertEqual(200, response.status)
        self.assertEqual(PAYLOAD, response.body)
        self.assertEqual(1, len(origin.requests))
        method, path, headers = origin.requests[0]
        self.assertEqual("GET", method)
        self.assertEqual(f"/mpt/relay/dav/assistant/{CONVERSATION}/{ITEM}/payload", path)
        self.assertEqual(encode_basic(*CREDENTIALS), headers.get("authorization"))
        # The loopback connector only supplies TCP reachability; nginx selects the fixed Tail
        # virtualhost by this exact Host, whatever the configured origin is.
        self.assertEqual(TRUSTED_TAIL_HOST, headers.get("host"))
        self.assertEqual("1", headers.get(UPSTREAM_HOP_HEADER))
        self.assertNotIn("cookie", headers)
        self.assertNotIn("x-client-note", headers)
        self.assertNotIn("range", headers)

    def test_the_upstream_host_header_is_fixed_for_every_method_and_origin(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD]
        origin.declared_length = len(PAYLOAD)
        relay = self.proxy_relay_for(origin, len(PAYLOAD))

        self.assertEqual(200, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual(200, relay.dav("HEAD", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual(200, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)

        hosts = [headers.get("host") for _method, _path, headers in origin.requests]
        methods = [method for method, _path, _headers in origin.requests]
        self.assertEqual(["GET", "HEAD", "GET"], methods)
        self.assertEqual([TRUSTED_TAIL_HOST] * 3, hosts)

    def test_locator_fields_cannot_supply_a_host_or_a_credential(self) -> None:
        origin = self.start_origin()
        origin.chunks = [PAYLOAD]
        origin.declared_length = len(PAYLOAD)
        relay = self.proxy_relay(origin)
        relay.conversations(CREDENTIALS)
        forged = locator(size=len(PAYLOAD))
        forged["url"] = f"http://127.0.0.1:{origin.port}/elsewhere"
        forged["host"] = "evil.example"
        forged["authorization"] = "Basic " + base64.b64encode(b"attacker:stolen").decode("ascii")
        self.assertEqual(201, put_locator(relay, CREDENTIALS, json.dumps(forged).encode("utf-8")))

        response = relay.dav("GET", payload_path(), basic=CREDENTIALS)

        self.assertEqual(200, response.status)
        self.assertEqual(PAYLOAD, response.body)
        self.assertEqual(1, len(origin.requests))
        _method, path, headers = origin.requests[0]
        self.assertEqual(f"/mpt/relay/dav/assistant/{CONVERSATION}/{ITEM}/payload", path)
        self.assertEqual(encode_basic(*CREDENTIALS), headers.get("authorization"))


class HealthAndDefaultOffTests(ProxyTestCase):
    def test_capability_is_advertised_only_when_enabled(self) -> None:
        origin = self.start_origin()

        disabled = self.proxy_relay(origin, proxy=False)
        health = disabled.health().json()
        self.assertNotIn("capabilities", health)
        self.assertNotIn("sharedPayloadLocator", health)

        enabled = self.proxy_relay(origin)
        advertised = enabled.health().json()
        self.assertEqual(1, advertised["capabilities"]["sharedPayloadLocator"])
        self.assertNotIn("sharedPayloadLocator", advertised, "only the nested capability shape is contract")
        self.assertLessEqual(len(enabled.health().body), 256)

    def test_disabled_server_keeps_the_plain_404_and_never_opens_the_upstream(self) -> None:
        origin = self.start_origin()
        relay = self.proxy_relay(origin, proxy=False)
        relay.conversations(CREDENTIALS)
        self.assertEqual(201, put_locator(relay, CREDENTIALS, locator_bytes(size=len(PAYLOAD))))

        self.assertEqual(404, relay.dav("GET", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual(404, relay.dav("HEAD", payload_path(), basic=CREDENTIALS).status)
        self.assertEqual([], origin.requests)
        self.assertFalse(fallback_request_file(relay).exists(), "a disabled proxy must not mutate the namespace")


class ConfigGuardTests(unittest.TestCase):
    """The origin is a fixed trusted domain; no request or environment value may widen it."""

    def test_proxy_is_off_by_default(self) -> None:
        config = Config.load([], {})
        self.assertFalse(config.tail_payload_proxy)
        self.assertEqual(30, config.tail_payload_timeout_seconds)
        self.assertEqual(("http", "mpt-relay.tail.lixinrui000.cn", 80), config.tail_origin())

    def test_origin_must_be_the_fixed_tail_domain_or_loopback(self) -> None:
        for origin in (
            "http://evil.example",
            "http://127.0.0.1.evil.example",
            "http://100.64.0.1:18765",
            "http://mpt-relay.tail.lixinrui000.cn.evil.example",
            "https://user:secret@mpt-relay.tail.lixinrui000.cn",
            "http://mpt-relay.tail.lixinrui000.cn/other",
            "http://mpt-relay.tail.lixinrui000.cn/?next=evil",
            "ftp://mpt-relay.tail.lixinrui000.cn",
            "not-a-url",
        ):
            with self.subTest(origin=origin), self.assertRaises(ConfigError):
                Config.load([], {"MPT_RELAY_TAIL_PAYLOAD_ORIGIN": origin})

        allowed = Config.load(
            [],
            {
                "MPT_RELAY_TAIL_PAYLOAD_PROXY": "1",
                "MPT_RELAY_TAIL_PAYLOAD_ORIGIN": "http://127.0.0.1:18766",
            },
        )
        self.assertTrue(allowed.tail_payload_proxy)
        self.assertEqual(("http", "127.0.0.1", 18766), allowed.tail_origin())

    def test_timeout_bounds_are_enforced(self) -> None:
        for value in ("0", "601"):
            with self.subTest(value=value), self.assertRaises(ConfigError):
                Config.load([], {"MPT_RELAY_TAIL_PAYLOAD_TIMEOUT_SECONDS": value})


if __name__ == "__main__":
    unittest.main()

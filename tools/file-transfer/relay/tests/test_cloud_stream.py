"""Cloud reverse streams over actual HTTP sockets; no provider credential or payload staging."""
import concurrent.futures
import builtins
import http.client
import io
import json
import re
import shutil
import socket
import struct
import threading
import time
import unittest
from datetime import datetime, timedelta, timezone
from dataclasses import replace
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock

from relay_testkit import BASE_PATH, DAV_ROOT, RelayHarness, call
from mpt_relay.auth import encode_basic
from mpt_relay.cloud_stream import CHUNK_BYTES, QUEUE_CHUNKS, Signal
from mpt_relay.config import Config, ConfigError

C = "shared-cloud-owner"
KEY = "a" * 64
AUTH = (C, KEY)
OTHER = ("shared-cloud-other", "b" * 64)
M = "0123456789abcdef0123456789abcdef"
CAP = "c" * 64
SENDER = "phone-owner"
BYTES = bytes(range(256)) * 8192


def date(seconds=0):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")


def manifest(size=len(BYTES)):
    return {"version": 1, "id": M, "kind": "image", "text": None, "name": "cloud-test.png", "size": size,
            "createdAt": date(), "senderDeviceId": SENDER, "senderName": "Cloud test sender", "targetDeviceId": None}


def offer(size=len(BYTES)):
    return {"version": 1, "conversationId": C, "message": manifest(size), "capability": CAP,
            "senderDeviceId": SENDER, "expiresAt": date(3600), "route": "mpt-cloud-stream-v1"}


def payload():
    return DAV_ROOT + f"assistant/{C}/{M}/payload"


def request_url(request_id):
    return BASE_PATH + f"/v1/cloud/requests/{request_id}/body"


def wait_until(predicate, seconds=3):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        if predicate():
            return True
        time.sleep(.01)
    return bool(predicate())


class CloudStreamTests(unittest.TestCase):
    def setUp(self):
        self.relay = RelayHarness(cloud_payload_stream=1, cloud_wait_seconds=2, cloud_stream_seconds=6)
        self.pool = concurrent.futures.ThreadPoolExecutor(max_workers=8)
        self.relay.conversations(AUTH)
        self.relay.conversations(OTHER)

    def tearDown(self):
        self.relay.stop()
        self.pool.shutdown(wait=True, cancel_futures=True)
        shutil.rmtree(self.relay.root)

    def seed(self, value=None):
        value = value or offer()
        for top in ("cloud-locator", "assistant"):
            for path in (top, f"{top}/{C}", f"{top}/{C}/{M}"):
                self.relay.dav("MKCOL", path + "/", basic=AUTH)
        response = self.relay.dav("PUT", f"cloud-locator/{C}/{M}/manifest.json", basic=AUTH, body=json.dumps(value).encode())
        self.assertIn(response.status, (201, 204), response.body)
        response = self.relay.dav("PUT", f"assistant/{C}/{M}/manifest.json", basic=AUTH, body=json.dumps(value["message"]).encode())
        self.assertIn(response.status, (201, 204), response.body)
        return value

    def poll(self, basic=AUTH, sender=SENDER, wait=1):
        return self.relay.request("GET", BASE_PATH + f"/v1/cloud/requests?deviceId={sender}&wait={wait}", basic=basic)

    def test_dotnet_timestamp_is_accepted_on_python310(self):
        value = offer()
        value["message"]["createdAt"] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "7Z"
        value["expiresAt"] = (datetime.now(timezone.utc) + timedelta(hours=1)).strftime("%Y-%m-%dT%H:%M:%S.%f") + "7+00:00"

        def legacy_parse(text):
            if re.search(r"\.\d{7}", text):
                raise ValueError("Python 3.10 fractional precision")
            return datetime.fromisoformat(text)

        with mock.patch("mpt_relay.cloud_stream.datetime", wraps=datetime) as legacy:
            legacy.fromisoformat.side_effect = legacy_parse
            self.seed(value)
            self.assertEqual(200, self.relay.request("HEAD", payload(), basic=AUTH).status)

    def test_sender_poll_includes_authoritative_relay_clock(self):
        before = datetime.now(timezone.utc)
        result = self.poll(wait=0).json()
        after = datetime.now(timezone.utc)
        stamp = datetime.fromisoformat(result["serverTime"])
        self.assertLessEqual(before, stamp)
        self.assertLessEqual(stamp, after)
        self.assertEqual(timedelta(0), stamp.utcoffset())

    def start_receive(self):
        future = self.pool.submit(self.relay.request, "GET", payload(), basic=AUTH)
        rows = self.poll().json()["requests"]
        self.assertEqual(1, len(rows))
        return future, rows[0]

    def supply(self, row, body=BYTES, cap=CAP, basic=AUTH):
        return self.relay.request("PUT", request_url(row["requestId"]), basic=basic, body=body,
                                  headers={"X-MPT-Cloud-Capability": cap})

    def rejected_supply(self, row, cap=CAP, basic=AUTH, length=len(BYTES), extra=""):
        # Send headers only: a rejected capability must not make the client upload its file.
        # http.client.request would eagerly send 2 MiB and race the deliberate early close.
        with self.relay.raw_connection(timeout=4) as sock:
            framing = "" if length is None else f"Content-Length: {length}\r\n"
            sock.sendall((f"PUT {request_url(row['requestId'])} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*basic)}\r\nX-MPT-Cloud-Capability: {cap}\r\n{framing}{extra}\r\n").encode("latin1"))
            response = http.client.HTTPResponse(sock); response.begin(); response.read()
            return response.status

    def test_online_sender_to_legacy_receiver_streams_exact_bytes_without_payload_on_disk(self):
        self.seed()
        before = self.relay.store.global_usage()
        real_open, real_io_open = builtins.open, io.open
        writes = []
        def readonly(opener):
            def checked(path, mode="r", *args, **kwargs):
                if any(flag in mode for flag in "wa+"):
                    writes.append(str(path))
                    raise AssertionError("A reverse payload must never open a disk writer")
                return opener(path, mode, *args, **kwargs)
            return checked
        # Catch even transient payload staging that could be deleted before an end-state scan.
        # SQLite's native credential bookkeeping is outside this file-body write fence.
        with mock.patch("builtins.open", readonly(real_open)), mock.patch("io.open", readonly(real_io_open)):
            future, row = self.start_receive()
            response = self.supply(row)
            received = future.result(timeout=4)
        self.assertEqual([], writes)
        self.assertEqual(200, response.status, response.body)
        self.assertEqual(len(BYTES), response.json()["transferred"])
        self.assertEqual(200, received.status)
        self.assertEqual(BYTES, received.body)
        self.assertEqual(before, self.relay.store.global_usage())
        files = list(self.relay.data_dir.rglob("*"))
        self.assertFalse(any(p.name == "payload" for p in files))
        self.assertFalse(any(p.name == "relay-public.json" for p in files))
        self.assertEqual({}, self.relay.app.cloud._pending)
        self.assertEqual([], self.poll(wait=0).json()["requests"])
        self.assertNotIn(CAP, "\n".join(self.relay.logs()))

    def test_three_party_sender_reads_local_cloud_source_then_streams_to_recipient(self):
        source_calls = []
        class LocalCloud(BaseHTTPRequestHandler):
            def do_GET(inner):
                source_calls.append(inner.headers.get("Authorization"))
                inner.send_response(200); inner.send_header("Content-Length", str(len(BYTES))); inner.end_headers()
                for index in range(0, len(BYTES), CHUNK_BYTES):
                    inner.wfile.write(BYTES[index:index + CHUNK_BYTES])
            def log_message(inner, *_):
                pass
        source = ThreadingHTTPServer(("127.0.0.1", 0), LocalCloud)
        thread = threading.Thread(target=source.serve_forever, daemon=True); thread.start()
        self.seed()
        try:
            receive, row = self.start_receive()
            local = http.client.HTTPConnection("127.0.0.1", source.server_port, timeout=6)
            sender = http.client.HTTPConnection("127.0.0.1", self.relay.port, timeout=6)
            try:
                local.request("GET", "/sender-private-account/file", headers={"Authorization": "provider-secret-local-only"})
                body = local.getresponse()
                sender.request("PUT", request_url(row["requestId"]), body=body,
                    headers={"Authorization": encode_basic(*AUTH), "X-MPT-Cloud-Capability": CAP, "Content-Length": str(len(BYTES))})
                sent = sender.getresponse(); self.assertEqual(200, sent.status); sent.read()
                self.assertEqual(BYTES, receive.result(timeout=4).body)
                self.assertEqual(["provider-secret-local-only"], source_calls)
                for path in self.relay.data_dir.rglob("*"):
                    if path.is_file():
                        self.assertNotIn(b"provider-secret-local-only", path.read_bytes())
            finally:
                local.close(); sender.close()
        finally:
            source.shutdown(); source.server_close(); thread.join()

    def test_head_has_length_but_creates_no_job_and_local_payload_wins(self):
        self.seed()
        response = self.relay.request("HEAD", payload(), basic=AUTH)
        self.assertEqual(200, response.status)
        self.assertEqual(str(len(BYTES)), response.header("content-length"))
        self.assertEqual({}, self.relay.app.cloud._pending)
        self.assertEqual([], self.poll(wait=0).json()["requests"])
        self.relay.dav("PUT", f"assistant/{C}/{M}/payload", basic=AUTH, body=b"committed-public-copy")
        self.assertEqual(b"committed-public-copy", self.relay.request("GET", payload(), basic=AUTH).body)
        self.assertEqual({}, self.relay.app.cloud._pending)

    def test_offline_sender_times_out_and_releases_request_without_public_copy(self):
        self.seed()
        start = time.monotonic()
        response = self.relay.request("GET", payload(), basic=AUTH)
        self.assertEqual(503, response.status)
        self.assertGreater(time.monotonic() - start, 1.5)
        self.assertLess(time.monotonic() - start, 4)
        self.assertEqual({}, self.relay.app.cloud._pending)
        self.assertFalse(any(self.relay.data_dir.rglob("payload")))
        self.assertFalse(any(self.relay.data_dir.rglob("relay-public.json")))

    def test_wrong_capability_cross_namespace_and_wrong_length_cannot_claim(self):
        self.seed()
        future, row = self.start_receive()
        self.assertEqual(403, self.rejected_supply(row, cap="d" * 64))
        self.assertEqual(403, self.rejected_supply(row, cap="bad"))
        self.assertEqual(403, self.rejected_supply(row, cap="é" * 64))
        self.assertEqual(404, self.rejected_supply(row, basic=OTHER))
        self.assertEqual(400, self.rejected_supply(row, length=5))
        self.assertEqual(401, self.rejected_supply(row, basic=(C, "e" * 64)))
        self.assertEqual(200, self.supply(row).status)
        self.assertEqual(BYTES, future.result(timeout=3).body)
        self.assertEqual(404, self.rejected_supply(row))

    def test_claim_once_while_streaming_and_subsequent_members_can_download_same_offer(self):
        self.seed()
        future, row = self.start_receive()
        sock = self.relay.raw_connection(timeout=6)
        self.addCleanup(sock.close)
        headers = f"PUT {request_url(row['requestId'])} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\nX-MPT-Cloud-Capability: {CAP}\r\nContent-Length: {len(BYTES)}\r\nConnection: close\r\n\r\n"
        sock.sendall(headers.encode() + BYTES[:CHUNK_BYTES])
        self.assertTrue(wait_until(lambda: self.relay.app.cloud._pending[row["requestId"]].claimed))
        self.assertEqual(409, self.rejected_supply(row))
        sock.sendall(BYTES[CHUNK_BYTES:])
        response = http.client.HTTPResponse(sock); response.begin(); self.assertEqual(200, response.status); response.read()
        self.assertEqual(BYTES, future.result(timeout=4).body)
        # Existing per-device receipts must not remove the locator or terminate later members.
        self.relay.dav("MKCOL", f"assistant/{C}/{M}/receipts/", basic=AUTH)
        self.relay.dav("PUT", f"assistant/{C}/{M}/receipts/first.json", basic=AUTH, body=b'{"deviceId":"first"}')
        again, next_row = self.start_receive()
        self.assertNotEqual(row["requestId"], next_row["requestId"])
        self.assertEqual(200, self.supply(next_row).status)
        self.assertEqual(BYTES, again.result(timeout=3).body)

    def test_truncated_sender_never_acknowledges_success_and_receiver_is_truncated(self):
        self.seed()
        future, row = self.start_receive()
        sock = self.relay.raw_connection(timeout=4)
        sock.sendall((f"PUT {request_url(row['requestId'])} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\nX-MPT-Cloud-Capability: {CAP}\r\nContent-Length: {len(BYTES)}\r\n\r\n").encode() + BYTES[:100])
        sock.shutdown(socket.SHUT_WR)
        response = http.client.HTTPResponse(sock); response.begin(); self.assertNotEqual(200, response.status); response.read(); sock.close()
        try:
            received = future.result(timeout=4)
            self.assertNotEqual(200, received.status)
        except (http.client.IncompleteRead, http.client.RemoteDisconnected, ConnectionResetError):
            pass
        self.assertTrue(wait_until(lambda: not self.relay.app.cloud._pending))
        self.assertFalse(any(self.relay.data_dir.rglob("payload")))

    def test_receiver_disconnect_before_claim_removes_job_promptly(self):
        self.seed()
        receiver = self.relay.raw_connection(timeout=4)
        receiver.sendall((f"GET {payload()} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\n\r\n").encode())
        rows = self.poll().json()["requests"]; self.assertEqual(1, len(rows))
        receiver.close()
        self.assertTrue(wait_until(lambda: not self.relay.app.cloud._pending, .8))
        self.assertEqual(404, self.rejected_supply(rows[0]))

    def test_backpressure_is_bounded_and_receiver_disconnect_interrupts_sender(self):
        size = 16 << 20
        self.seed(offer(size))
        receiver = self.relay.raw_connection(timeout=4)
        receiver.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
        receiver.sendall((f"GET {payload()} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\n\r\n").encode())
        row = self.poll().json()["requests"][0]
        job = self.relay.app.cloud._pending[row["requestId"]]
        send = self.pool.submit(self.supply, row, b"x" * size)
        response = http.client.HTTPResponse(receiver); response.begin(); self.assertEqual(200, response.status)
        self.assertTrue(wait_until(lambda: len(job.queue) == QUEUE_CHUNKS))
        self.assertFalse(send.done())
        self.assertLessEqual(job.max_buffered_bytes, CHUNK_BYTES * QUEUE_CHUNKS)
        receiver.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
        response.close(); receiver.close()
        try:
            sent = send.result(timeout=4)
            self.assertNotEqual(200, sent.status)
        except (BrokenPipeError, ConnectionResetError, http.client.RemoteDisconnected):
            pass
        self.assertTrue(wait_until(lambda: not self.relay.app.cloud._pending))
        self.assertEqual([], list(self.relay.data_dir.rglob("payload")))

    def test_poll_filters_sender_and_namespace_waits_for_event_and_cleans_disconnect(self):
        self.seed()
        waiting = self.pool.submit(self.poll, AUTH, SENDER, 2)
        self.assertTrue(wait_until(lambda: bool(self.relay.app.cloud._waiters)))
        self.assertFalse(waiting.done())
        receive = self.pool.submit(self.relay.request, "GET", payload(), basic=AUTH)
        rows = waiting.result(timeout=2).json()["requests"]; self.assertEqual(1, len(rows))
        self.assertEqual([], self.poll(sender="someone-else", wait=0).json()["requests"])
        self.assertEqual([], self.poll(basic=OTHER, wait=0).json()["requests"])
        self.supply(rows[0]); self.assertEqual(BYTES, receive.result(timeout=3).body)
        sock = self.relay.raw_connection(timeout=4)
        sock.sendall((f"GET {BASE_PATH}/v1/cloud/requests?deviceId={SENDER}&wait=25 HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\n\r\n").encode())
        self.assertTrue(wait_until(lambda: bool(self.relay.app.cloud._waiters)))
        sock.close()
        self.assertTrue(wait_until(lambda: not self.relay.app.cloud._waiters, .8))

    def test_pending_limit_does_not_grow_unbounded(self):
        self.seed()
        jobs = [self.pool.submit(self.relay.request, "GET", payload(), basic=AUTH) for _ in range(4)]
        self.assertTrue(wait_until(lambda: len(self.relay.app.cloud._pending) == 4))
        fifth = self.relay.request("GET", payload(), basic=AUTH)
        self.assertEqual(503, fifth.status)
        self.assertEqual("cloud_stream_busy", fifth.json()["error"])
        self.assertEqual(4, len(self.relay.app.cloud._pending))
        for future in jobs:
            self.assertEqual(503, future.result(timeout=3).status)
        self.assertEqual({}, self.relay.app.cloud._pending)

    def test_expired_offer_head_and_get_do_not_create_requests(self):
        value = offer(); value["expiresAt"] = date(1)
        self.seed(value)
        time.sleep(1.1)
        self.assertEqual(410, self.relay.request("HEAD", payload(), basic=AUTH).status)
        self.assertEqual(410, self.relay.request("GET", payload(), basic=AUTH).status)
        self.assertEqual({}, self.relay.app.cloud._pending)

    def test_manifest_conflict_or_missing_never_serves_a_locator(self):
        value = self.seed()
        value["message"]["name"] = "different.bin"
        self.relay.dav("PUT", f"assistant/{C}/{M}/manifest.json", basic=AUTH, body=json.dumps(value["message"]).encode())
        self.assertEqual(409, self.relay.request("HEAD", payload(), basic=AUTH).status)
        self.relay.dav("DELETE", f"assistant/{C}/{M}/manifest.json", basic=AUTH)
        self.assertEqual(404, self.relay.request("GET", payload(), basic=AUTH).status)
        self.assertEqual({}, self.relay.app.cloud._pending)

    def test_invalid_metadata_is_rejected_before_dav_commit(self):
        self.seed()
        path = f"cloud-locator/{C}/{M}/manifest.json"
        original = self.relay.dav("GET", path, basic=AUTH).body
        for mutate, status in [
            (lambda v: v.update(capability="A" * 64), 400),
            (lambda v: v.update(conversationId=OTHER[0]), 403),
            (lambda v: v["message"].update(targetDeviceId="private-recipient"), 403),
            (lambda v: v["message"].update(size=True), 413),
            (lambda v: v["message"].update(kind="text"), 400),
            (lambda v: v.update(expiresAt=date(8 * 86400)), 400),
            (lambda v: v.update(expiresAt=date(-1)), 410),
            (lambda v: v.update(cookie="provider-secret"), 400),
            (lambda v: v.update(route="https://untrusted.test"), 403),
            (lambda v: v["message"].update(senderDeviceId="not-owner"), 400),
            (lambda v: v.update(capability="x" * 20000), 413),
        ]:
            value = offer(); mutate(value)
            with self.subTest(value=str(mutate)):
                result = self.relay.dav("PUT", path, basic=AUTH, body=json.dumps(value).encode())
                self.assertEqual(status, result.status, result.body)
                self.assertEqual(original, self.relay.dav("GET", path, basic=AUTH).body)
        self.assertEqual(403, self.relay.dav("PUT", path, basic=OTHER, body=json.dumps(offer()).encode()).status)

    def test_feature_flag_health_auth_and_length_requirements(self):
        health = self.relay.health().json()
        self.assertEqual(1, health["capabilities"]["cloudPayloadStream"])
        self.assertEqual(401, self.relay.request("GET", BASE_PATH + "/v1/cloud/requests?deviceId=x&wait=0").status)
        self.seed(); future, row = self.start_receive()
        self.assertEqual(411, self.rejected_supply(row, length=None))
        self.assertEqual(411, self.rejected_supply(row, length=None, extra="Transfer-Encoding: chunked\r\n"))
        self.assertEqual(400, self.rejected_supply(row, extra=f"Content-Range: bytes 0-{len(BYTES)-1}/{len(BYTES)}\r\n"))
        self.supply(row); self.assertEqual(BYTES, future.result(timeout=4).body)
        config = Config.load([], {})
        self.assertFalse(config.cloud_payload_stream)
        with self.assertRaises(ConfigError):
            Config.load([], {"MPT_RELAY_CLOUD_WAIT_SECONDS": "0"})
        with RelayHarness() as old:
            self.assertNotIn("cloudPayloadStream", old.health().json().get("capabilities", {}))
            self.assertEqual(404, old.request("GET", BASE_PATH + "/v1/cloud/requests?deviceId=x&wait=0").status)
        shutil.rmtree(old.root)

    def test_zero_length_file_still_has_once_claim_and_exact_response(self):
        self.seed(offer(0)); receive, row = self.start_receive()
        self.assertEqual(200, self.supply(row, body=b"").status)
        response = receive.result(timeout=3)
        self.assertEqual(200, response.status); self.assertEqual(b"", response.body)
        self.assertEqual({}, self.relay.app.cloud._pending)

    def test_stalled_sender_hits_stream_deadline_and_releases_both_connections(self):
        self.relay.app.cloud.config = replace(self.relay.config, cloud_stream_seconds=1)
        self.seed(); receive, row = self.start_receive()
        with self.relay.raw_connection(timeout=3) as sock:
            sock.sendall((f"PUT {request_url(row['requestId'])} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\nX-MPT-Cloud-Capability: {CAP}\r\nContent-Length: {len(BYTES)}\r\n\r\n").encode() + BYTES[:100])
            start = time.monotonic()
            response = http.client.HTTPResponse(sock); response.begin()
            self.assertNotEqual(200, response.status); response.read()
            self.assertLess(time.monotonic() - start, 2.5)
        with self.assertRaises((http.client.IncompleteRead, http.client.RemoteDisconnected, ConnectionResetError)):
            receive.result(timeout=2)
        self.assertTrue(wait_until(lambda: not self.relay.app.cloud._pending))

    def test_stalled_receiver_write_cannot_outlive_stream_deadline(self):
        self.relay.app.cloud.config = replace(self.relay.config, cloud_stream_seconds=1)
        size = 16 << 20
        self.seed(offer(size))
        with self.relay.raw_connection(timeout=4) as receiver:
            receiver.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
            receiver.sendall((f"GET {payload()} HTTP/1.1\r\nHost: localhost\r\nAuthorization: {encode_basic(*AUTH)}\r\n\r\n").encode())
            row = self.poll().json()["requests"][0]
            send = self.pool.submit(self.supply, row, b"x" * size)
            headers = http.client.HTTPResponse(receiver); headers.begin()
            self.assertEqual(200, headers.status)
            self.assertTrue(wait_until(lambda: not self.relay.app.cloud._pending, 2.5))
            try:
                result = send.result(timeout=2)
                self.assertNotEqual(200, result.status)
            except (BrokenPipeError, ConnectionResetError, http.client.RemoteDisconnected):
                pass
            headers.close()

    def test_offer_replacement_or_deletion_revokes_unclaimed_request(self):
        value = self.seed(); receive, row = self.start_receive()
        changed = dict(value, capability="d" * 64)
        path = f"cloud-locator/{C}/{M}/manifest.json"
        self.assertEqual(204, self.relay.dav("PUT", path, basic=AUTH, body=json.dumps(changed).encode()).status)
        self.assertEqual(403, self.rejected_supply(row))
        self.assertEqual(403, self.rejected_supply(row, cap="d" * 64))
        self.assertEqual(204, self.relay.dav("DELETE", path, basic=AUTH).status)
        self.assertEqual(403, self.rejected_supply(row))
        self.assertEqual(503, receive.result(timeout=3).status)
        self.assertEqual({}, self.relay.app.cloud._pending)

    def test_private_inbox_credentials_cannot_poll_or_claim_shared_payload(self):
        inbox = "private-inbox"
        owner, deposit = "1" * 64, "2" * 64
        register = self.relay.request("POST", BASE_PATH + "/v1/inboxes", basic=(inbox, owner),
                                      body=json.dumps({"depositKey": deposit}).encode())
        self.assertEqual(201, register.status)
        self.seed(); receive, row = self.start_receive()
        for key in (owner, deposit):
            self.assertEqual(401, self.poll(basic=(inbox, key), wait=0).status)
            self.assertEqual(401, self.rejected_supply(row, basic=(inbox, key)))
            self.assertEqual(401, self.relay.request("GET", payload(), basic=(inbox, key)).status)
        self.assertEqual(200, self.supply(row).status)
        self.assertEqual(BYTES, receive.result(timeout=3).body)

    def test_idle_poll_waits_once_and_shutdown_releases_poll_and_receiver(self):
        self.seed()
        original = Signal.wait
        calls = []
        def counted(signal, request, seconds):
            calls.append(seconds)
            return original(signal, request, seconds)
        with mock.patch.object(Signal, "wait", counted):
            poll = self.pool.submit(self.poll, AUTH, "idle-device", 25)
            self.assertTrue(wait_until(lambda: len(calls) == 1))
            time.sleep(.3)
            self.assertEqual(1, len(calls))
            receive = self.pool.submit(self.relay.request, "GET", payload(), basic=AUTH)
            self.assertTrue(wait_until(lambda: len(self.relay.app.cloud._pending) == 1))
            self.relay.app.cloud.close()
            self.assertEqual([], poll.result(timeout=1).json()["requests"])
            self.assertEqual(503, receive.result(timeout=1).status)
        self.assertEqual({}, self.relay.app.cloud._pending)
        self.assertEqual({}, self.relay.app.cloud._waiters)

    def test_global_request_and_poll_limits_are_enforced(self):
        self.relay.app.cloud.limit = 1
        self.seed(); receive, row = self.start_receive()
        self.assertEqual(503, self.relay.request("GET", payload(), basic=AUTH).status)
        poll = self.pool.submit(self.poll, AUTH, "idle-device", 25)
        self.assertTrue(wait_until(lambda: len(self.relay.app.cloud._waiters) == 1))
        self.assertEqual(503, self.poll(sender="another-device", wait=0).status)
        self.assertEqual(200, self.supply(row).status)
        self.assertEqual(BYTES, receive.result(timeout=3).body)
        self.relay.app.cloud.close()
        self.assertEqual([], poll.result(timeout=1).json()["requests"])

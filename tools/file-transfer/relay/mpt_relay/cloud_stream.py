"""Authenticated reverse cloud payload streams, without persistent payload staging.

A locator grants one file in one shared namespace. Each receiver gets its own one-use request;
no receipt removes the offer. The sender holds all provider credentials and initiates the body
connection, so the receiver never needs a sender IP or a provider login. Only small locator and
V1 manifest metadata are persisted by DAV; chunks live in a bounded producer/consumer queue.
"""
from __future__ import annotations

import hmac
import json
import re
import select
import socket
import threading
import time
import uuid
from collections import deque
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Optional

from .auth import CONVERSATION_ID_PATTERN
from .messages import BodyReader, BodyStream, Request, Response, json_response
from .shared_payload import ITEM_ID_PATTERN, MAX_LOCATOR_BYTES

ROOT = "cloud-locator"
ROUTE = "mpt-cloud-stream-v1"
CAPABILITY_HEADER = "x-mpt-cloud-capability"
CAPABILITY = re.compile(r"^[0-9a-f]{64}$")
CHUNK_BYTES = 64 << 10
QUEUE_CHUNKS = 2
OUTER_FIELDS = {"version", "conversationId", "message", "capability", "senderDeviceId", "expiresAt", "route"}
MESSAGE_FIELDS = {"version", "id", "kind", "text", "name", "size", "createdAt", "senderDeviceId", "senderName", "targetDeviceId", "conversationId"}


class CloudError(Exception):
    def __init__(self, status: int, code: str):
        super().__init__(code)
        self.status, self.code = status, code


def error_response(status: int, code: str) -> Response:
    response = json_response(status, {"error": code}, ("Retry-After", "3"))
    response.close = True
    return response


def _utc(value) -> datetime:
    if not isinstance(value, str) or len(value) > 64:
        raise CloudError(400, "invalid_cloud_expiry")
    try:
        # .NET emits seven fractional digits; Python 3.10 accepts only three or six.
        # Truncate to Python's microsecond precision before parsing, retaining the UTC check.
        normalized = re.sub(r"(\.\d{6})\d+(?=Z$|[+-]\d{2}:\d{2}$)", r"\1", value)
        date = datetime.fromisoformat(normalized.replace("Z", "+00:00"))
        if date.utcoffset() != timedelta(0):
            raise ValueError()
        return date
    except ValueError as exc:
        raise CloudError(400, "invalid_cloud_expiry") from exc


@dataclass(frozen=True)
class Locator:
    conversation: str
    item: str
    sender: str
    capability: str
    size: int
    expires: datetime
    message: dict


def parse_locator(raw: bytes, conversation: str, item: str, max_size: int) -> Locator:
    if len(raw) > MAX_LOCATOR_BYTES:
        raise CloudError(413, "cloud_locator_too_large")
    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as exc:
        raise CloudError(400, "invalid_cloud_locator") from exc
    if not isinstance(data, dict) or set(data) - OUTER_FIELDS or type(data.get("version")) is not int or data["version"] != 1:
        raise CloudError(400, "invalid_cloud_locator")
    if data.get("route") != ROUTE or data.get("conversationId", conversation) != conversation:
        raise CloudError(403, "cloud_scope_mismatch")
    message = data.get("message")
    if not isinstance(message, dict) or set(message) - MESSAGE_FIELDS:
        raise CloudError(400, "invalid_cloud_manifest")
    if type(message.get("version")) is not int or message["version"] != 1 or message.get("id") != item:
        raise CloudError(400, "invalid_cloud_manifest")
    if message.get("conversationId", conversation) != conversation or message.get("targetDeviceId") is not None:
        raise CloudError(403, "cloud_shared_only")
    if message.get("kind") not in ("file", "image") or message.get("text") not in (None, ""):
        raise CloudError(400, "cloud_attachment_required")
    size = message.get("size")
    if type(size) is not int or size < 0 or size > max_size:
        raise CloudError(413, "invalid_cloud_size")
    sender, capability = data.get("senderDeviceId"), data.get("capability")
    if not isinstance(sender, str) or not CONVERSATION_ID_PATTERN.fullmatch(sender) or message.get("senderDeviceId") != sender:
        raise CloudError(400, "invalid_cloud_sender")
    if not isinstance(capability, str) or not CAPABILITY.fullmatch(capability):
        raise CloudError(400, "invalid_cloud_capability")
    if not isinstance(message.get("name"), str) or not message["name"] or len(message["name"]) > 1024:
        raise CloudError(400, "invalid_cloud_manifest")
    if not isinstance(message.get("senderName"), str) or len(message["senderName"]) > 100:
        raise CloudError(400, "invalid_cloud_manifest")
    _utc(message.get("createdAt"))
    expires = _utc(data.get("expiresAt"))
    now = datetime.now(timezone.utc)
    if expires <= now:
        raise CloudError(410, "cloud_offer_expired")
    if expires > now + timedelta(days=7):
        raise CloudError(400, "cloud_expiry_too_far")
    return Locator(conversation, item, sender, capability, size, expires, message)


class MetadataBody(BodyReader):
    """Replay validated, at-most-16-KiB metadata through the ordinary atomic/quota DAV writer."""
    def __init__(self, raw: bytes):
        self.raw, self.declared_length, self.done = raw, len(raw), False

    def stream_to(self, target: Path, on_bytes) -> int:
        on_bytes(len(self.raw))
        target.write_bytes(self.raw)
        self.done = True
        return len(self.raw)

    def consumed(self) -> bool:
        return self.done


class Signal:
    """A socket notification pairs with the HTTP peer in select(), so idle never polls."""
    def __init__(self):
        self.reader, self.writer = socket.socketpair()
        self.reader.setblocking(False)
        self.writer.setblocking(False)

    def notify(self):
        try:
            self.writer.send(b"1")
        except (BlockingIOError, OSError):
            pass

    def wait(self, request: Request, seconds: float) -> str:
        if request.wait_for_signal is not None:
            result = request.wait_for_signal(self.reader, max(0, seconds))
        else:
            ready, _, _ = select.select([self.reader], [], [], max(0, seconds))
            result = "notified" if ready else "timeout"
        if result == "notified":
            try:
                while self.reader.recv(4096):
                    pass
            except (BlockingIOError, OSError):
                pass
        return result

    def close(self):
        self.reader.close()
        self.writer.close()


class Pending:
    def __init__(self, locator: Locator, receiver: Request, config):
        self.id = uuid.uuid4().hex
        self.locator, self.receiver = locator, receiver
        self.condition = threading.Condition()
        self.signal = Signal()
        self.queue = deque()
        self.max_buffered_bytes = 0
        self.claimed = self.producer_done = self.committed = self.completed = False
        self.failure = ""
        self.sender_body: Optional[BodyReader] = None
        now = time.monotonic()
        lifetime = max(0, (locator.expires - datetime.now(timezone.utc)).total_seconds())
        self.claim_deadline = now + min(config.cloud_wait_seconds, lifetime)
        self.stream_deadline = now + min(config.cloud_stream_seconds, lifetime)
        self.expires_at = (datetime.now(timezone.utc) + timedelta(seconds=min(config.cloud_wait_seconds, lifetime))).isoformat().replace("+00:00", "Z")

    def fail(self, reason: str):
        with self.condition:
            if self.completed:
                return
            self.failure = self.failure or reason
            self.queue.clear()
            body = self.sender_body
            committed = self.committed
            self.condition.notify_all()
            self.signal.notify()
        if body is not None:
            body.abort()
        if committed:
            self.receiver.abort_response()

    def push(self, chunk: bytes):
        with self.condition:
            while len(self.queue) >= QUEUE_CHUNKS and not self.failure:
                remaining = self.stream_deadline - time.monotonic()
                if remaining <= 0:
                    raise CloudError(503, "cloud_stream_timeout")
                self.condition.wait(remaining)
            if self.failure:
                raise CloudError(503, self.failure)
            if time.monotonic() >= self.stream_deadline:
                raise CloudError(503, "cloud_stream_timeout")
            self.queue.append(chunk)
            self.max_buffered_bytes = max(self.max_buffered_bytes, sum(map(len, self.queue)))
            self.signal.notify()


class ReverseBody(BodyStream):
    def __init__(self, owner: "CloudStreams", pending: Pending):
        self.owner, self.pending = owner, pending
        self.length = pending.locator.size
        self.deadline = pending.stream_deadline
        self._finished = self._closed = False

    @property
    def failed(self):
        return not self._finished

    def chunks(self):
        pending = self.pending
        written = 0
        while True:
            with pending.condition:
                if pending.failure:
                    return
                if pending.queue:
                    chunk = pending.queue.popleft()
                    pending.condition.notify_all()
                elif pending.producer_done:
                    self._finished = written == self.length
                    return
                else:
                    chunk = None
            if chunk is not None:
                yield chunk
                # Resumption means the HTTP layer finished this write. A failed write never
                # advances the count and close() cannot acknowledge it as delivered.
                written += len(chunk)
                continue
            result = pending.signal.wait(pending.receiver, pending.stream_deadline - time.monotonic())
            if result != "notified":
                pending.fail("cloud_receiver_closed" if result == "peer_closed" else "cloud_stream_timeout")
                return

    def close(self):
        if self._closed:
            return
        self._closed = True
        pending = self.pending
        if self._finished:
            with pending.condition:
                pending.completed = True
                pending.condition.notify_all()
        else:
            pending.fail("cloud_receiver_closed")
        self.owner.remove(pending)
        pending.signal.close()


class CloudStreams:
    def __init__(self, config, store):
        self.config, self.store = config, store
        self._lock = threading.RLock()
        self._pending = {}
        self._waiters = {}
        self._closed = False
        self.limit = min(config.cloud_max_requests, max(1, (config.max_connections - 4) // 3))

    def close(self):
        with self._lock:
            self._closed = True
            pending, waiters = list(self._pending.values()), list(self._waiters)
        for job in pending:
            job.fail("cloud_server_stopping")
        for signal in waiters:
            signal.notify()

    def remove(self, job: Pending):
        with self._lock:
            if self._pending.get(job.id) is job:
                del self._pending[job.id]

    def validate_put(self, conversation: str, segments, request: Request):
        if not segments or segments[0] != ROOT:
            return
        if not self.config.cloud_payload_stream:
            raise CloudError(404, "cloud_stream_disabled")
        if len(segments) != 4 or segments[1] != conversation or not ITEM_ID_PATTERN.fullmatch(segments[2]) or segments[3] != "manifest.json":
            raise CloudError(403, "cloud_scope_mismatch")
        if request.body.declared_length is not None and request.body.declared_length > MAX_LOCATOR_BYTES:
            raise CloudError(413, "cloud_locator_too_large")
        raw = request.body.read_all(MAX_LOCATOR_BYTES)
        parse_locator(raw, conversation, segments[2], self.config.max_file_bytes)
        request.body = MetadataBody(raw)

    def locator(self, conversation: str, item: str) -> Optional[Locator]:
        root = self.store.conversation_dir(conversation)
        path = root / ROOT / conversation / item / "manifest.json"
        try:
            with path.open("rb") as file:
                raw = file.read(MAX_LOCATOR_BYTES + 1)
        except FileNotFoundError:
            return None
        except IsADirectoryError:
            raise CloudError(400, "invalid_cloud_locator")
        offer = parse_locator(raw, conversation, item, self.config.max_file_bytes)
        # The V1 manifest is published last. A locator alone must not expose a half-published item.
        try:
            with (root / "assistant" / conversation / item / "manifest.json").open("rb") as file:
                committed = file.read(MAX_LOCATOR_BYTES + 1)
            if len(committed) > MAX_LOCATOR_BYTES or json.loads(committed) != offer.message:
                raise CloudError(409, "cloud_manifest_conflict")
        except (FileNotFoundError, IsADirectoryError, ValueError):
            raise CloudError(404, "cloud_manifest_missing")
        return offer

    def payload(self, conversation: str, segments, trailing: bool, request: Request) -> Optional[Response]:
        if not self.config.cloud_payload_stream or trailing or len(segments) != 4 or segments[0] != "assistant" or segments[1] != conversation or segments[3] != "payload" or not ITEM_ID_PATTERN.fullmatch(segments[2]):
            return None
        offer = self.locator(conversation, segments[2])
        if offer is None:
            return None
        if request.method == "HEAD":
            return Response(200, head_only=True, content_length=offer.size, content_type="application/octet-stream", extra_headers=(("Cache-Control", "no-store"),))
        with self._lock:
            if self._closed or len(self._pending) >= self.limit or sum(j.locator.conversation == conversation for j in self._pending.values()) >= self.config.cloud_requests_per_conversation:
                raise CloudError(503, "cloud_stream_busy")
            pending = Pending(offer, request, self.config)
            self._pending[pending.id] = pending
            for signal, scope in self._waiters.items():
                if scope == (conversation, offer.sender):
                    signal.notify()
        transferred = False
        try:
            while True:
                with pending.condition:
                    if pending.failure:
                        raise CloudError(503, pending.failure)
                    if pending.queue or pending.producer_done:
                        pending.committed = True
                        transferred = True
                        return Response(200, stream=ReverseBody(self, pending), content_type="application/octet-stream", close=True, extra_headers=(("Cache-Control", "no-store"),))
                result = pending.signal.wait(request, pending.claim_deadline - time.monotonic())
                if result != "notified":
                    raise CloudError(503, "cloud_receiver_closed" if result == "peer_closed" else "cloud_sender_unavailable")
        except CloudError as error:
            pending.fail(error.code)
            raise
        finally:
            if not transferred:
                self.remove(pending)
                pending.signal.close()

    def poll(self, conversation: str, request: Request) -> Response:
        sender = request.query_one("deviceId")
        if not isinstance(sender, str) or not CONVERSATION_ID_PATTERN.fullmatch(sender):
            raise CloudError(400, "invalid_cloud_sender")
        try:
            wait = int(request.query_one("wait") or "25")
        except ValueError:
            raise CloudError(400, "invalid_cloud_wait")
        if not 0 <= wait <= self.config.longpoll_max_seconds:
            raise CloudError(400, "invalid_cloud_wait")
        signal = Signal()
        with self._lock:
            if len(self._waiters) >= self.limit:
                signal.close()
                raise CloudError(503, "cloud_poll_busy")
            self._waiters[signal] = (conversation, sender)
        deadline = time.monotonic() + wait
        try:
            while True:
                with self._lock:
                    rows = [{"requestId": j.id, "itemId": j.locator.item, "capability": j.locator.capability,
                             "size": j.locator.size, "expiresAt": j.expires_at}
                            for j in self._pending.values() if not j.claimed and not j.failure and j.locator.conversation == conversation and j.locator.sender == sender and time.monotonic() < j.claim_deadline]
                    if rows or self._closed or time.monotonic() >= deadline:
                        return json_response(200, {"requests": rows, "serverTime": datetime.now(timezone.utc).isoformat()})
                if signal.wait(request, deadline - time.monotonic()) != "notified":
                    return json_response(200, {"requests": [], "serverTime": datetime.now(timezone.utc).isoformat()})
        finally:
            with self._lock:
                self._waiters.pop(signal, None)
            signal.close()

    def supply(self, conversation: str, request_id: str, request: Request) -> Response:
        if request.header("transfer-encoding") or request.header("content-length") is None:
            raise CloudError(411, "cloud_length_required")
        if request.header("content-range") is not None:
            raise CloudError(400, "cloud_full_body_required")
        with self._lock:
            job = self._pending.get(request_id)
            if job is None or job.locator.conversation != conversation:
                raise CloudError(404, "cloud_request_missing")
            offer = self.locator(conversation, job.locator.item)
            cap = request.header(CAPABILITY_HEADER) or ""
            if offer is None or offer != job.locator or not CAPABILITY.fullmatch(cap) or not hmac.compare_digest(cap, offer.capability):
                raise CloudError(403, "cloud_capability_rejected")
            if job.claimed:
                raise CloudError(409, "cloud_request_claimed")
            if job.failure or time.monotonic() >= job.claim_deadline:
                raise CloudError(410, "cloud_request_expired")
            if request.body.declared_length != offer.size:
                raise CloudError(400, "cloud_length_mismatch")
            job.claimed = True
            job.sender_body = request.body
        try:
            total = 0
            for chunk in request.body.chunks(deadline=job.stream_deadline):
                if len(chunk) > CHUNK_BYTES or total + len(chunk) > offer.size:
                    raise CloudError(400, "cloud_length_mismatch")
                total += len(chunk)
                job.push(chunk)
            if total != offer.size:
                raise CloudError(400, "cloud_length_mismatch")
            with job.condition:
                job.producer_done = True
                job.signal.notify()
                while not job.completed and not job.failure:
                    remaining = job.stream_deadline - time.monotonic()
                    if remaining <= 0:
                        raise CloudError(503, "cloud_stream_timeout")
                    job.condition.wait(remaining)
                if not job.completed:
                    raise CloudError(503, "cloud_receiver_closed")
            response = json_response(200, {"transferred": total})
            response.close = True
            return response
        except CloudError as error:
            job.fail(error.code)
            return error_response(error.status, error.code)
        except Exception:
            job.fail("cloud_sender_interrupted")
            return error_response(400, "cloud_sender_interrupted")

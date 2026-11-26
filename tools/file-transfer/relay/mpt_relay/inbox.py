"""Pairing inboxes: a device receives files from another device without sharing its identity.

Model
-----

An inbox is a namespace with **two independent credentials**:

* ``ownerKey``   - full control: list pending items, read payloads, write receipts, delete.
* ``depositKey`` - deposit only: upload one item, and read back the receipt of an item it
  already knows by its random id. It can never list, never read a payload and never write a
  receipt.

The pairing code a user shows or scans carries only ``inboxId`` + ``depositKey``. The owner
key and the user's own conversation key are never part of it, so handing out a pairing code
cannot leak the conversation. Both keys are 64 hex characters, generated offline and stored
only as salted PBKDF2 digests.

Wire format (fixed here, documented in ``docs/PROTOCOL.md``)
------------------------------------------------------------

    POST   /v1/inboxes                        owner   {"depositKey":"<64hex>"}
    GET    /v1/inboxes/items?since=N          owner   long poll + pending item descriptors
    GET    /v1/inboxes/items/{itemId}         owner   payload bytes + metadata headers
    DELETE /v1/inboxes/items/{itemId}         owner   drop one item (frees quota)
    POST   /v1/inboxes/items/{itemId}/receipt owner   {"savedAt":…,"bytes":…}
    PUT    /v1/inboxes/items/{itemId}         deposit payload bytes + metadata headers
    GET    /v1/inboxes/items/{itemId}/receipt deposit|owner  delivery confirmation

Metadata travels in bounded ``X-MPT-*`` headers; values that may contain non-ASCII text are
percent-encoded UTF-8. The payload is the request body and is streamed to disk through the
same reservation, body budget and atomic-rename path as WebDAV.
"""

from __future__ import annotations

import json
import logging
import os
import re
import urllib.parse
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional, Tuple

from .config import Config
from .fsutil import fsync_directory, fsync_file, temp_name, unlink_quietly, write_atomic
from .messages import Request, Response, empty, json_response
from .store import QuotaExceeded, Store, TooLarge

ITEM_ID_PATTERN = re.compile(r"^[0-9a-f]{32}$")
DEVICE_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
KINDS = ("text", "image", "file")

ITEM_METADATA_NAME = "item.json"
ITEM_PAYLOAD_NAME = "payload"
ITEM_RECEIPT_NAME = "receipt.json"

MAX_HEADER_BYTES = 512
MAX_METADATA_BYTES = 4096
MAX_RECEIPT_BYTES = 4096
MAX_NAME_CHARS = 180
MAX_SENDER_NAME_CHARS = 100
WINDOWS_RESERVED = {"CON", "PRN", "AUX", "NUL"} | {f"COM{index}" for index in range(1, 10)} | {
    f"LPT{index}" for index in range(1, 10)
}

ITEM_LIST_DEFAULT_LIMIT = 50
ITEM_LIST_MAX_LIMIT = 200


class InboxError(Exception):
    """A request that must be answered with a specific status and code."""

    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message


@dataclass(frozen=True)
class Item:
    """The immutable descriptor of one delivered item."""

    item_id: str
    kind: str
    name: Optional[str]
    size: int
    created_at: str
    sender_device_id: Optional[str]
    sender_name: Optional[str]
    target_device_id: Optional[str]

    def to_json(self) -> dict:
        return {
            "version": 1,
            "itemId": self.item_id,
            "kind": self.kind,
            "name": self.name,
            "size": self.size,
            "createdAt": self.created_at,
            "senderDeviceId": self.sender_device_id,
            "senderName": self.sender_name,
            "targetDeviceId": self.target_device_id,
        }

    def same_message(self, other: "Item", compare_size: bool = True) -> bool:
        """Identity for duplicate detection. Deliberately excludes the arrival time.

        ``compare_size`` is false for a chunked retry, where the client declared no length at
        all; the metadata still has to match exactly.
        """
        return (
            self.kind == other.kind
            and self.name == other.name
            and self.sender_device_id == other.sender_device_id
            and self.sender_name == other.sender_name
            and self.target_device_id == other.target_device_id
            and (not compare_size or self.size == other.size)
        )

    @staticmethod
    def from_json(payload: dict) -> "Item":
        item = Item(
            item_id=str(payload.get("itemId", "")),
            kind=str(payload.get("kind", "")),
            name=payload.get("name"),
            size=int(payload.get("size", -1)),
            created_at=str(payload.get("createdAt", "")),
            sender_device_id=payload.get("senderDeviceId"),
            sender_name=payload.get("senderName"),
            target_device_id=payload.get("targetDeviceId"),
        )
        validate_item(item)
        return item


def validate_item(item: Item) -> None:
    if not ITEM_ID_PATTERN.match(item.item_id):
        raise InboxError(400, "invalid_item_id", "条目 id 必须是 32 位小写十六进制。")
    if item.kind not in KINDS:
        raise InboxError(400, "invalid_kind", "条目类型必须是 text/image/file。")
    if item.size < 0:
        raise InboxError(400, "invalid_size", "条目长度无效。")
    if item.kind == "text":
        if item.name is not None:
            raise InboxError(400, "invalid_name", "文本条目不应带文件名。")
    else:
        item_name = item.name or ""
        validate_file_name(item_name)
        if item_name != item.name:
            raise InboxError(400, "invalid_name", "文件名无效。")
    if item.sender_device_id is not None and not DEVICE_ID_PATTERN.match(item.sender_device_id):
        raise InboxError(400, "invalid_sender", "发送设备 id 无效。")
    if item.target_device_id is not None and not DEVICE_ID_PATTERN.match(item.target_device_id):
        raise InboxError(400, "invalid_target", "目标设备 id 无效。")
    if item.sender_name is not None and not (1 <= len(item.sender_name) <= MAX_SENDER_NAME_CHARS):
        raise InboxError(400, "invalid_sender", "发送设备名称无效。")
    validate_timestamp(item.created_at, "invalid_created_at")


def validate_file_name(name: str) -> None:
    """The same rules the C# client applies to a received file name."""
    if not (1 <= len(name) <= MAX_NAME_CHARS) or name in (".", ".."):
        raise InboxError(400, "invalid_name", "文件名必须是 1–180 个字符。")
    if any(ord(character) < 32 or character in '<>:"/\\|?*' for character in name):
        raise InboxError(400, "invalid_name", "文件名含不支持的字符。")
    if name.endswith(".") or name.endswith(" "):
        raise InboxError(400, "invalid_name", "文件名不能以点或空格结尾。")
    if name.split(".")[0].upper() in WINDOWS_RESERVED:
        raise InboxError(400, "invalid_name", "文件名是系统保留名称。")


def validate_timestamp(value: str, code: str) -> str:
    text = (value or "").strip()
    if not (1 <= len(text) <= 64):
        raise InboxError(400, code, "时间戳无效。")
    candidate = text[:-1] + "+00:00" if text.endswith("Z") else text
    try:
        datetime.fromisoformat(candidate)
    except ValueError as error:
        raise InboxError(400, code, "时间戳必须是 ISO-8601。") from error
    return text


def decode_header(value: Optional[str]) -> Optional[str]:
    """Percent-decoded UTF-8 header value."""
    if value is None:
        return None
    if len(value) > MAX_HEADER_BYTES:
        raise InboxError(400, "header_too_long", "元信息头过长。")
    try:
        return urllib.parse.unquote(value, errors="strict")
    except UnicodeDecodeError as error:
        raise InboxError(400, "invalid_header", "元信息头不是合法的百分号编码 UTF-8。") from error


def encode_header(value: Optional[str]) -> Optional[str]:
    return None if value is None else urllib.parse.quote(value, safe="-_.~")


def _checked_metadata_size(request: Request) -> None:
    total = 0
    for name, value in request.headers.items():
        if name.startswith("x-mpt-"):
            total += len(name) + len(value)
    if total > MAX_METADATA_BYTES:
        raise InboxError(400, "metadata_too_large", "元信息过大。")


def parse_item_headers(request: Request, item_id: str) -> Item:
    """Builds the item descriptor from the bounded ``X-MPT-*`` headers."""
    _checked_metadata_size(request)
    kind = (request.header("x-mpt-kind") or "file").strip().lower()
    name = decode_header(request.header("x-mpt-name"))
    sender_id = decode_header(request.header("x-mpt-sender-id"))
    sender_name = decode_header(request.header("x-mpt-sender-name"))
    target_id = decode_header(request.header("x-mpt-target-device-id"))
    created_at = decode_header(request.header("x-mpt-created-at")) or datetime.now(timezone.utc).isoformat()
    for header in (sender_id, sender_name, target_id):
        if header is not None and any(ord(character) < 32 for character in header):
            raise InboxError(400, "invalid_header", "元信息头包含控制字符。")
    if sender_id is not None and not sender_id:
        sender_id = None
    if target_id is not None and not target_id:
        target_id = None
    declared = request.body.declared_length if request.body.declared_length is not None else 0
    item = Item(
        item_id=item_id,
        kind=kind,
        name=name,
        size=declared,
        created_at=created_at,
        sender_device_id=sender_id,
        sender_name=sender_name,
        target_device_id=target_id,
    )
    validate_item(item)
    return item


class InboxApi:
    """Pure ``Request -> Response`` handlers for everything under ``/v1/inboxes``."""

    def __init__(self, config: Config, store: Store, logger: Optional[logging.Logger] = None) -> None:
        self.config = config
        self.store = store
        self.log = logger or logging.getLogger("mpt_relay")

    # -- paths -------------------------------------------------------------------------

    def item_dir(self, inbox_id: str, item_id: str) -> Path:
        return self.store.inbox_dir(inbox_id) / "items" / item_id

    def read_item(self, inbox_id: str, item_id: str) -> Optional[Item]:
        """Reads a *published* item; an interrupted deposit has no metadata and is not visible."""
        metadata = self.item_dir(inbox_id, item_id) / ITEM_METADATA_NAME
        if not metadata.is_file():
            return None
        try:
            payload = json.loads(metadata.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        try:
            return Item.from_json(payload)
        except InboxError:
            return None

    def read_receipt(self, inbox_id: str, item_id: str) -> Optional[dict]:
        path = self.item_dir(inbox_id, item_id) / ITEM_RECEIPT_NAME
        if not path.is_file():
            return None
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        return payload if isinstance(payload, dict) else None

    def pending_items(self, inbox_id: str, limit: int) -> Tuple[List[Item], bool]:
        """Published items without an owner receipt, newest first."""
        items_dir = self.store.inbox_dir(inbox_id) / "items"
        if not items_dir.is_dir():
            return [], False
        candidates: List[Tuple[float, Item]] = []
        for entry in os.scandir(items_dir):
            if not entry.is_dir(follow_symlinks=False) or not ITEM_ID_PATTERN.match(entry.name):
                continue
            if not (Path(entry.path) / ITEM_METADATA_NAME).is_file():
                continue
            if (Path(entry.path) / ITEM_RECEIPT_NAME).is_file():
                continue
            item = self.read_item(inbox_id, entry.name)
            if item is None:
                continue
            try:
                modified = os.stat(entry.path).st_mtime
            except OSError:
                modified = 0.0
            candidates.append((modified, item))
        candidates.sort(key=lambda entry: (entry[0], entry[1].item_id), reverse=True)
        has_more = len(candidates) > limit
        return [item for _modified, item in candidates[:limit]], has_more

    # -- owner routes ------------------------------------------------------------------

    def list_items(self, request: Request, inbox_id: str) -> Response:
        raw = request.query_one("since")
        since: Optional[int]
        if raw is None:
            since = None
        else:
            try:
                since = int(raw)
            except ValueError:
                return json_response(400, {"error": "invalid_since", "detail": "since 必须是整数 revision。"})
            if since < 0:
                return json_response(400, {"error": "invalid_since", "detail": "since 不能为负数。"})
        limit = ITEM_LIST_DEFAULT_LIMIT
        raw_limit = request.query_one("limit")
        if raw_limit is not None:
            try:
                limit = max(1, min(int(raw_limit), ITEM_LIST_MAX_LIMIT))
            except ValueError:
                return json_response(400, {"error": "invalid_limit", "detail": "limit 必须是整数。"})

        space = self.store.inbox_space(inbox_id)
        current = self.store.revision_space(space)
        if since is not None and since == current:
            waited = self.store.wait_for_revision_space(
                space, since, float(self.config.longpoll_max_seconds), abort=request.peer_closed
            )
            if waited is None:
                response = empty(204)
                response.close = True
                return response
            current = waited
        items, has_more = self.pending_items(inbox_id, limit)
        return json_response(
            200,
            {
                "revision": current,
                "items": [item.to_json() for item in items],
                "hasMore": has_more,
            },
        )

    def get_payload(self, request: Request, inbox_id: str, item_id: str) -> Response:
        item = self.read_item(inbox_id, item_id)
        if item is None:
            return json_response(404, {"error": "unknown_item"})
        payload = self.item_dir(inbox_id, item_id) / ITEM_PAYLOAD_NAME
        if not payload.is_file():
            return json_response(409, {"error": "item_incomplete", "detail": "该条目没有可用内容。"})
        headers = self._item_headers(item)
        return Response(
            status=200,
            content_type="application/octet-stream",
            file_path=payload,
            head_only=request.method == "HEAD",
            extra_headers=headers + (("Cache-Control", "no-store"),),
        )

    def delete_item(self, request: Request, inbox_id: str, item_id: str) -> Response:
        """Owner cleanup: drops one item (payload, metadata and receipt) and frees its quota."""
        space = self.store.inbox_space(inbox_id)
        directory = self.item_dir(inbox_id, item_id)
        with self.store.space_lock(space):
            if self.read_item(inbox_id, item_id) is None and not directory.is_dir():
                return json_response(404, {"error": "unknown_item"})
            freed = self.store.remove_tree(directory)
        self.store.apply_usage_space(space, 0, freed)
        return empty(204)

    def put_receipt(self, request: Request, inbox_id: str, item_id: str) -> Response:
        """Records that the owner saved one item.

        The item is read, the receipt is validated against it and the receipt is written under
        one namespace lock, so a concurrent DELETE either removes a complete item+receipt pair
        or leaves the failed writer with a 404 - it can never recreate an orphan receipt.
        """
        raw = request.body.read_all(MAX_RECEIPT_BYTES)
        try:
            payload = json.loads(raw.decode("utf-8")) if raw.strip() else {}
        except (ValueError, UnicodeDecodeError):
            return json_response(400, {"error": "invalid_receipt", "detail": "回执必须是 JSON。"})
        if not isinstance(payload, dict):
            return json_response(400, {"error": "invalid_receipt", "detail": "回执必须是 JSON 对象。"})
        body_item_id = payload.get("itemId")
        if body_item_id is not None and body_item_id != item_id:
            # A receipt may only describe the item in the path.
            return json_response(400, {"error": "receipt_item_mismatch", "detail": "回执与条目不一致。"})

        space = self.store.inbox_space(inbox_id)
        with self.store.space_lock(space):
            item = self.read_item(inbox_id, item_id)
            if item is None:
                return json_response(404, {"error": "unknown_item"})
            try:
                saved_at = validate_timestamp(str(payload.get("savedAt", "")), "invalid_saved_at")
                raw_bytes = payload.get("bytes", item.size)
                if isinstance(raw_bytes, bool) or not isinstance(raw_bytes, int):
                    raise InboxError(400, "invalid_receipt", "bytes 必须是整数。")
                if raw_bytes < 0 or raw_bytes > item.size:
                    raise InboxError(400, "invalid_receipt", "回执长度与条目不符。")
                device_id = payload.get("deviceId")
                device_name = payload.get("deviceName")
                if device_id is not None:
                    device_id = str(device_id)
                    if not DEVICE_ID_PATTERN.match(device_id):
                        raise InboxError(400, "invalid_receipt", "回执设备 id 无效。")
                if device_name is not None:
                    device_name = str(device_name)
                    if not (1 <= len(device_name) <= MAX_SENDER_NAME_CHARS) or any(
                        ord(character) < 32 for character in device_name
                    ):
                        raise InboxError(400, "invalid_receipt", "回执设备名称无效。")
            except InboxError as error:
                return json_response(error.status, {"error": error.code, "detail": error.message})

            receipt = {
                "version": 1,
                "itemId": item_id,
                "savedAt": saved_at,
                "bytes": raw_bytes,
                "deviceId": device_id,
                "deviceName": device_name,
            }
            existing = self.read_receipt(inbox_id, item_id)
            if existing is not None and all(existing.get(key) == value for key, value in receipt.items()):
                return json_response(200, {"itemId": item_id, "saved": True, "duplicate": True, **receipt})
            write_atomic(self.item_dir(inbox_id, item_id) / ITEM_RECEIPT_NAME, _encode_json(receipt))
        # A receipt is the owner's own bookkeeping: it never wakes the owner's long poll and
        # never advances the revision, because it only removes work from the pending list.
        return json_response(200, {"itemId": item_id, "saved": True, "duplicate": False, **receipt})

    # -- deposit routes ----------------------------------------------------------------

    def put_item(self, request: Request, inbox_id: str, item_id: str) -> Response:
        """Deposits one item.

        The existence check, the idempotency/conflict decision, the quota reservation and the
        publish all happen **inside one namespace lock**. Two concurrent retries of the same
        item id therefore serialize: the first creates and charges once, the second observes
        the published metadata and answers 200/409 without touching the stored copy. Deciding
        outside the lock let both see "not present", overwrite each other's payload and charge
        the namespace twice.
        """
        if not ITEM_ID_PATTERN.match(item_id):
            return json_response(400, {"error": "invalid_item_id", "detail": "条目 id 必须是 32 位小写十六进制。"})
        item = parse_item_headers(request, item_id)
        declared = request.body.declared_length
        space = self.store.inbox_space(inbox_id)
        directory = self.item_dir(inbox_id, item_id)

        with self.store.space_lock(space):
            existing = self.read_item(inbox_id, item_id)
            if existing is not None:
                if not existing.same_message(item, compare_size=declared is not None):
                    return json_response(
                        409,
                        {"error": "item_conflict", "detail": "同 id 条目已存在且内容不同，请使用新的 itemId。"},
                    )
                # Idempotent retry: keep the stored copy, do not bump the revision, do not charge.
                response = json_response(
                    200, {"itemId": item_id, "duplicate": True, "revision": self.store.revision_space(space)}
                )
                response.close = True  # the retried body is not read
                return response

            try:
                reservation = self.store.reserve_inbox(inbox_id, replacing=0, declared=declared)
            except (QuotaExceeded, TooLarge) as error:
                return json_response(
                    413 if isinstance(error, TooLarge) else 507, {"error": "quota_exceeded", "detail": str(error)}
                )
            directory.mkdir(parents=True, exist_ok=True)
            try:
                self.store.ensure_entry_budget_space(space, adding=2)
            except QuotaExceeded as error:
                reservation.release()
                return json_response(507, {"error": "quota_exceeded", "detail": str(error)})

            temporary = directory / temp_name()
            written = 0

            def on_bytes(count: int) -> None:
                reservation.grow(count)

            try:
                written = request.body.stream_to(temporary, on_bytes)
                if declared is not None and written != declared:
                    raise InboxError(400, "incomplete_body", "请求体长度与 Content-Length 不符。")
                if item.kind == "text" and written == 0:
                    raise InboxError(400, "empty_text", "文本条目不能为空。")
                fsync_file(temporary)
                os.replace(temporary, directory / ITEM_PAYLOAD_NAME)
                fsync_directory(directory)
                finalized = replace(item, size=written)
                write_atomic(directory / ITEM_METADATA_NAME, _encode_json(finalized.to_json()))
                item = finalized
                reservation.commit(written, 0)
            except InboxError:
                _drop_incomplete_item(directory, temporary)
                raise
            except (QuotaExceeded, TooLarge) as error:
                _drop_incomplete_item(directory, temporary)
                return json_response(
                    413 if isinstance(error, TooLarge) else 507, {"error": "quota_exceeded", "detail": str(error)}
                )
            except BaseException:
                _drop_incomplete_item(directory, temporary)
                raise
            finally:
                reservation.release()

        revision = self.store.bump_revision_space(space)
        return json_response(201, {"itemId": item.item_id, "size": item.size, "revision": revision})

    def get_receipt(self, inbox_id: str, item_id: str) -> Response:
        """Deposit readable: an item id is 128 random bits, so it is the capability."""
        if not ITEM_ID_PATTERN.match(item_id):
            return json_response(400, {"error": "invalid_item_id"})
        item = self.read_item(inbox_id, item_id)
        if item is None:
            return json_response(404, {"error": "unknown_item"})
        receipt = self.read_receipt(inbox_id, item_id)
        if receipt is None:
            return json_response(200, {"itemId": item_id, "saved": False, "size": item.size})
        return json_response(
            200,
            {
                "itemId": item_id,
                "saved": True,
                "size": item.size,
                "savedAt": receipt.get("savedAt"),
                "bytes": receipt.get("bytes"),
                "deviceId": receipt.get("deviceId"),
                "deviceName": receipt.get("deviceName"),
            },
        )

    # -- helpers -----------------------------------------------------------------------

    def _item_headers(self, item: Item) -> Tuple[Tuple[str, str], ...]:
        headers = [
            ("X-MPT-Item-Id", item.item_id),
            ("X-MPT-Kind", item.kind),
            ("X-MPT-Size", str(item.size)),
            ("X-MPT-Created-At", item.created_at),
        ]
        for name, value in (
            ("X-MPT-Name", item.name),
            ("X-MPT-Sender-Id", item.sender_device_id),
            ("X-MPT-Sender-Name", item.sender_name),
            ("X-MPT-Target-Device-Id", item.target_device_id),
        ):
            encoded = encode_header(value)
            if encoded is not None:
                headers.append((name, encoded))
        return tuple(headers)


def _encode_json(payload: dict) -> bytes:
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"


def _drop_incomplete_item(directory: Path, temporary: Path) -> None:
    """Removes the temp file and, when nothing was published, the empty item directory."""
    unlink_quietly(temporary)
    if (directory / ITEM_METADATA_NAME).exists():
        return
    unlink_quietly(directory / ITEM_PAYLOAD_NAME)
    try:
        os.rmdir(directory)
    except OSError:
        pass


__all__ = ["InboxApi", "InboxError", "Item"]

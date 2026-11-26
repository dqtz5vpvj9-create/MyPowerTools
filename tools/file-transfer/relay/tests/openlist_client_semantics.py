"""A line-for-line mirror of the .NET client's wire behaviour.

The relay must be compatible with ``FileTransfer.Core.OpenListClient`` and its assistant
partial (``Assistant/OpenListAssistantClient.cs``). Those are C# and the .NET toolchain is
not part of this service, so the next best evidence is a faithful re-implementation of the
exact request sequence, header set, status-code tolerance and href resolution the C# code
performs — and running it against a real relay over real HTTP.

Every method below cites the C# member it mirrors. If the C# changes, this mirror is what
must change with it; the tests then fail loudly instead of silently drifting.
"""

from __future__ import annotations

import json
import re
import urllib.parse
from dataclasses import dataclass, field
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from typing import Dict, Iterable, List, Optional, Tuple
from xml.etree import ElementTree

from relay_testkit import DAV_ROOT, HttpResult, RelayHarness

DAV_NAMESPACE = "DAV:"
MAX_PAYLOAD_BYTES = 16 << 30
MAX_TEXT_CHARACTERS = 8192
MAX_SENDER_NAME = 100
MAX_RECEIPTS_PER_ITEM = 64

CONVERSATION_ID = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
ITEM_ID = re.compile(r"^[0-9a-f]{32}$")


class OpenListError(IOError):
    """The C# ``Check`` throws ``IOException``; the mirror keeps the same failure class."""


class InvalidData(ValueError):
    """Mirrors ``InvalidDataException`` for protocol violations from the relay."""


def escape_segment(value: str) -> str:
    """``Uri.EscapeDataString``."""
    return urllib.parse.quote(value, safe="~()*!.'")


def validate_conversation_id(value: str) -> str:
    if not CONVERSATION_ID.match(value):
        raise InvalidData(f"非法会话 id：{value!r}")
    return value


def validate_item_id(value: str) -> str:
    if not ITEM_ID.match(value):
        raise InvalidData(f"非法条目 id：{value!r}")
    return value


def validate_name(value: Optional[str]) -> str:
    name = value or ""
    if len(name) < 1 or len(name) > 180 or name in (".", ".."):
        raise InvalidData("文件名无效。")
    if any(ord(character) < 32 or character in '<>:"/\\|?*' for character in name):
        raise InvalidData("文件名含不支持的字符。")
    return name


@dataclass
class AssistantManifest:
    id: str
    kind: str
    size: int
    created_at: str
    sender_device_id: str
    sender_name: str
    text: Optional[str] = None
    name: Optional[str] = None
    target_device_id: Optional[str] = None
    version: int = 1

    def to_json(self) -> dict:
        return {
            "version": self.version,
            "id": self.id,
            "kind": self.kind,
            "text": self.text,
            "name": self.name,
            "size": self.size,
            "createdAt": self.created_at,
            "senderDeviceId": self.sender_device_id,
            "senderName": self.sender_name,
            "targetDeviceId": self.target_device_id,
        }

    @staticmethod
    def from_json(payload: dict, expected_id: Optional[str] = None) -> "AssistantManifest":
        manifest = AssistantManifest(
            id=str(payload.get("id", "")),
            kind=str(payload.get("kind", "")),
            size=int(payload.get("size", 0)),
            created_at=str(payload.get("createdAt", "")),
            sender_device_id=str(payload.get("senderDeviceId", "")),
            sender_name=str(payload.get("senderName", "")),
            text=payload.get("text"),
            name=payload.get("name"),
            target_device_id=payload.get("targetDeviceId"),
            version=int(payload.get("version", 0)),
        )
        validate_manifest(manifest, expected_id)
        return manifest

    def same_message(self, other: "AssistantManifest") -> bool:
        """``AssistantValidation.SameMessage``."""
        return (
            self.id == other.id
            and self.version == other.version
            and self.kind == other.kind
            and self.text == other.text
            and self.name == other.name
            and self.size == other.size
            and self.sender_device_id == other.sender_device_id
            and self.sender_name == other.sender_name
            and self.target_device_id == other.target_device_id
            and self.created_at == other.created_at
        )


def validate_manifest(manifest: AssistantManifest, expected_id: Optional[str] = None) -> None:
    validate_item_id(manifest.id)
    if expected_id is not None and manifest.id != expected_id:
        raise InvalidData("助手条目记录与目录不一致。")
    if manifest.version != 1:
        raise InvalidData("不支持的助手条目版本。")
    if manifest.kind not in ("text", "image", "file"):
        raise InvalidData("助手条目类型无效。")
    if not CONVERSATION_ID.match(manifest.sender_device_id):
        raise InvalidData("发送设备 id 无效。")
    if not (1 <= len(manifest.sender_name) <= MAX_SENDER_NAME):
        raise InvalidData("发送设备名称无效。")
    if manifest.target_device_id is not None and not CONVERSATION_ID.match(manifest.target_device_id):
        raise InvalidData("目标设备 id 无效。")
    if manifest.size < 0 or manifest.size > MAX_PAYLOAD_BYTES:
        raise InvalidData("助手条目长度无效。")
    if not manifest.created_at:
        raise InvalidData("助手条目时间无效。")
    if manifest.kind == "text":
        if manifest.name is not None:
            raise InvalidData("文本条目不应带文件名。")
        if not manifest.text or len(manifest.text) > MAX_TEXT_CHARACTERS:
            raise InvalidData("助手文本条目内容无效。")
        if manifest.size != len(manifest.text.encode("utf-8")):
            raise InvalidData("助手文本条目长度与内容不符。")
    else:
        if manifest.text is not None:
            raise InvalidData("非文本条目不应带正文。")
        validate_name(manifest.name)


@dataclass
class AssistantReceipt:
    item_id: str
    device_id: str
    device_name: str
    saved_at: str
    bytes: int

    def to_json(self) -> dict:
        return {
            "itemId": self.item_id,
            "deviceId": self.device_id,
            "deviceName": self.device_name,
            "savedAt": self.saved_at,
            "bytes": self.bytes,
        }

    @staticmethod
    def from_json(payload: dict, expected_item_id: Optional[str] = None) -> "AssistantReceipt":
        receipt = AssistantReceipt(
            item_id=str(payload.get("itemId", "")),
            device_id=str(payload.get("deviceId", "")),
            device_name=str(payload.get("deviceName", "")),
            saved_at=str(payload.get("savedAt", "")),
            bytes=int(payload.get("bytes", 0)),
        )
        validate_item_id(receipt.item_id)
        if expected_item_id is not None and receipt.item_id != expected_item_id:
            raise InvalidData("助手回执与条目不一致。")
        if not CONVERSATION_ID.match(receipt.device_id):
            raise InvalidData("回执设备 id 无效。")
        if not (1 <= len(receipt.device_name) <= MAX_SENDER_NAME):
            raise InvalidData("回执设备名称无效。")
        if not receipt.saved_at:
            raise InvalidData("助手回执时间无效。")
        return receipt


@dataclass
class AssistantPage:
    items: List[AssistantManifest] = field(default_factory=list)
    invalid_item_ids: List[str] = field(default_factory=list)
    has_more: bool = False
    discovered_count: int = 0
    next_cursor: Optional[str] = None


def last_modified(row: ElementTree.Element) -> datetime:
    for node in row.iter(f"{{{DAV_NAMESPACE}}}getlastmodified"):
        try:
            return parsedate_to_datetime(node.text or "")
        except (TypeError, ValueError):
            break
    return datetime.min.replace(tzinfo=timezone.utc)


class OpenListClientMirror:
    """Mirrors ``OpenListClient`` (generic transfer) and ``OpenListAssistantClient``."""

    def __init__(self, harness: RelayHarness, username: str, password: str, dav_directory: str = DAV_ROOT) -> None:
        self.harness = harness
        self.username = username
        self.password = password
        self.root_path = dav_directory  # always the public "/mpt/relay/dav/"
        self.root_url = harness.base_url + dav_directory
        self.requests: List[Tuple[str, str]] = []

    # -- URL helpers (mirror of Url/AssistantCollectionUrl/AssistantFileUrl) ----------

    def url(self, *segments: str) -> str:
        return self.root_path + "/".join(escape_segment(segment) for segment in segments)

    def collection_url(self, *segments: str) -> str:
        return self.url(*segments) + "/"

    def absolute_path(self, path: str) -> str:
        """Mirror of ``new Uri(_root, href).AbsolutePath``."""
        return urllib.parse.urlparse(urllib.parse.urljoin(self.root_url, path)).path

    # -- transport (mirror of RequestAsync) -------------------------------------------

    def request(self, method: str, path: str, *, content: Optional[bytes] = None, depth: Optional[str] = None) -> HttpResult:
        headers: Dict[str, str] = {}
        if depth is not None:
            headers["Depth"] = depth
        self.requests.append((method, path))
        return self.harness.request(
            method,
            path,
            body=content,
            basic=(self.username, self.password),
            headers=headers,
        )

    @staticmethod
    def check(response: HttpResult) -> None:
        """Mirror of ``Check``: anything but 2xx is an IOException."""
        if not 200 <= response.status < 300:
            raise OpenListError(f"OpenList 返回 HTTP {response.status}。")

    def mkcol(self, path: str) -> HttpResult:
        """Mirror of MkcolAsync/AssistantMkcolAsync: 405 on an existing collection is success."""
        response = self.request("MKCOL", path)
        if response.status != 405:
            self.check(response)
        return response

    # -- generic transfer (OpenListClient) --------------------------------------------

    def check_root(self) -> None:
        self.check(self.request("PROPFIND", self.root_path, depth="0"))

    def upload(self, recipient: str, item_id: str, name: str, data: bytes, sender: str = "sender") -> dict:
        """Mirror of ``UploadAsync``: MKCOL recipient, MKCOL id, PUT payload, PUT ready.json."""
        validate_conversation_id(recipient)
        self.check_root()
        self.mkcol(self.collection_url(recipient))
        self.mkcol(self.collection_url(recipient, item_id))
        self.check(self.request("PUT", self.url(recipient, item_id, "payload"), content=data))
        record = {
            "version": 1,
            "id": item_id,
            "name": name,
            "size": len(data),
            "sender": sender,
            "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        }
        self.check(
            self.request(
                "PUT",
                self.url(recipient, item_id, "ready.json"),
                content=json.dumps(record).encode("utf-8"),
            )
        )
        return record

    def list_inbox(self, recipient: str) -> List[dict]:
        """Mirror of ``ListAsync``."""
        response = self.request("PROPFIND", self.collection_url(recipient), depth="1")
        if response.status == 404:
            return []
        self.check(response)
        rows = ElementTree.fromstring(response.body)
        found: List[dict] = []
        for row in rows.iter(f"{{{DAV_NAMESPACE}}}response"):
            href = row.find(f"{{{DAV_NAMESPACE}}}href")
            if href is None or href.text is None:
                continue
            identifier = urllib.parse.unquote(self.absolute_path(href.text).rstrip("/").split("/")[-1])
            if not ITEM_ID.match(identifier):
                continue
            initial = self.request("GET", self.url(recipient, identifier, "ready.json"))
            if initial.status == 404:
                continue
            self.check(initial)
            record = json.loads(initial.body.decode("utf-8"))
            if int(record.get("version", 0)) != 1 or record.get("id") != identifier:
                raise InvalidData("网盘收件记录无效。")
            found.append(record)
        return found

    def download_inbox(self, recipient: str, item_id: str) -> bytes:
        """Mirror of ``DownloadAsync`` (redirect following is not needed on this relay)."""
        response = self.request("GET", self.url(recipient, item_id, "payload"))
        self.check(response)
        return response.body

    # -- assistant (OpenListAssistantClient) -------------------------------------------

    def publish_assistant(self, conversation_id: str, manifest: AssistantManifest, payload: Optional[bytes], progress=None) -> AssistantManifest:
        """Mirror of ``PublishAssistantAsync``."""
        validate_conversation_id(conversation_id)
        validate_manifest(manifest)
        if manifest.kind == "text" and payload is not None:
            raise InvalidData("文本条目不需要 payload。")
        if manifest.kind != "text" and payload is None:
            raise InvalidData("附件条目发布时必须提供待发副本。")
        if payload is not None and len(payload) != manifest.size:
            raise InvalidData("待发副本长度与条目记录不符。")

        self.mkcol(self.collection_url("assistant"))
        self.mkcol(self.collection_url("assistant", conversation_id))
        self.mkcol(self.collection_url("assistant", conversation_id, manifest.id))

        existing = None
        try:
            existing = self.read_manifest(conversation_id, manifest.id)
        except (InvalidData, json.JSONDecodeError):
            existing = None
        if existing is not None:
            if not existing.same_message(manifest):
                raise InvalidData(f"中转上已存在同 id 但内容不同的条目：{manifest.id}。")
            return existing

        if payload is not None:
            response = self.request("PUT", self.url("assistant", conversation_id, manifest.id, "payload"), content=payload)
            self.check(response)
            if progress is not None:
                progress(len(payload), len(payload))
        self.check(
            self.request(
                "PUT",
                self.url("assistant", conversation_id, manifest.id, "manifest.json"),
                content=json.dumps(manifest.to_json()).encode("utf-8"),
            )
        )
        return manifest

    def read_manifest(self, conversation_id: str, item_id: str) -> Optional[AssistantManifest]:
        """Mirror of ``AssistantReadManifestAsync``: 404 means "not published yet"."""
        response = self.request("GET", self.url("assistant", conversation_id, item_id, "manifest.json"))
        if response.status == 404:
            return None
        self.check(response)
        payload = json.loads(response.body.decode("utf-8"))
        manifest = AssistantManifest.from_json(payload)
        if manifest.id != item_id:
            raise InvalidData("助手条目记录与目录不一致。")
        return manifest

    def list_assistant(self, conversation_id: str, known: Iterable[str] = (), limit: int = 50) -> AssistantPage:
        """Mirror of ``ListAssistantAsync``: one Depth-1 PROPFIND plus at most ``limit`` GETs."""
        validate_conversation_id(conversation_id)
        limit = max(1, min(int(limit), 200))
        known_ids = set(known)
        collection = self.collection_url("assistant", conversation_id)
        response = self.request("PROPFIND", collection, depth="1")
        if response.status in (404, 409):
            return AssistantPage()
        self.check(response)
        rows = ElementTree.fromstring(response.body)
        self_path = urllib.parse.urlparse(self.root_url + collection[len(self.root_path) :]).path.rstrip("/")
        candidates: List[Tuple[str, datetime]] = []
        for row in rows.iter(f"{{{DAV_NAMESPACE}}}response"):
            href = row.find(f"{{{DAV_NAMESPACE}}}href")
            if href is None or href.text is None:
                continue
            path = self.absolute_path(href.text).rstrip("/")
            if path == self_path:
                continue
            identifier = urllib.parse.unquote(path.split("/")[-1])
            if not ITEM_ID.match(identifier):
                continue
            candidates.append((identifier, last_modified(row)))
        candidates.sort(key=lambda entry: (entry[1], entry[0]), reverse=True)

        page = AssistantPage(discovered_count=len(candidates))
        scanned = 0
        last: Optional[str] = None
        for identifier, _modified in candidates:
            if identifier in known_ids:
                continue
            if scanned >= limit:
                page.has_more = True
                break
            scanned += 1
            last = identifier
            try:
                manifest = self.read_manifest(conversation_id, identifier)
            except InvalidData:
                page.invalid_item_ids.append(identifier)
                continue
            except json.JSONDecodeError:
                continue
            if manifest is not None:
                page.items.append(manifest)
        page.next_cursor = last if page.has_more else None
        return page

    def download_assistant(self, conversation_id: str, manifest: AssistantManifest) -> bytes:
        """Mirror of ``DownloadAssistantAsync`` length verification."""
        response = self.request("GET", self.url("assistant", conversation_id, manifest.id, "payload"))
        self.check(response)
        if len(response.body) != manifest.size:
            raise InvalidData("网盘文件长度与助手条目记录不符。")
        return response.body

    def write_receipt(self, conversation_id: str, receipt: AssistantReceipt) -> None:
        """Mirror of ``WriteAssistantReceiptAsync``."""
        self.mkcol(self.collection_url("assistant", conversation_id, receipt.item_id))
        self.mkcol(self.collection_url("assistant", conversation_id, receipt.item_id, "receipts"))
        self.check(
            self.request(
                "PUT",
                self.url("assistant", conversation_id, receipt.item_id, "receipts", receipt.device_id + ".json"),
                content=json.dumps(receipt.to_json()).encode("utf-8"),
            )
        )

    def list_receipts(self, conversation_id: str, item_id: str) -> List[AssistantReceipt]:
        """Mirror of ``ListAssistantReceiptsAsync``: Depth-1 PROPFIND, dedupe by device, newest wins."""
        response = self.request(
            "PROPFIND",
            self.collection_url("assistant", conversation_id, item_id, "receipts"),
            depth="1",
        )
        if response.status in (404, 409):
            return []
        self.check(response)
        rows = ElementTree.fromstring(response.body)
        names: List[str] = []
        for row in rows.iter(f"{{{DAV_NAMESPACE}}}response"):
            href = row.find(f"{{{DAV_NAMESPACE}}}href")
            if href is None or href.text is None:
                continue
            name = urllib.parse.unquote(self.absolute_path(href.text).rstrip("/").split("/")[-1])
            if not name.lower().endswith(".json"):
                continue
            device = name[: -len(".json")]
            if not (1 <= len(device) <= 64) or not re.match(r"^[A-Za-z0-9_-]+$", device):
                continue
            names.append(name)
        names.sort()

        receipts: Dict[str, AssistantReceipt] = {}
        for name in names[:MAX_RECEIPTS_PER_ITEM]:
            response = self.request("GET", self.url("assistant", conversation_id, item_id, "receipts", name))
            if response.status == 404:
                continue
            self.check(response)
            try:
                receipt = AssistantReceipt.from_json(json.loads(response.body.decode("utf-8")), item_id)
            except (InvalidData, json.JSONDecodeError, ValueError):
                continue
            previous = receipts.get(receipt.device_id)
            if previous is None or receipt.saved_at > previous.saved_at:
                receipts[receipt.device_id] = receipt
        return sorted(receipts.values(), key=lambda receipt: receipt.saved_at)

"""Bounded shared-file payload proxy for old V1 clients.

A shared conversation can publish a large payload only to the fixed Tailnet relay and a small
discovery record (``assistant-locator/<C>/<M>/manifest.json``) to the public relay. A client that
still speaks plain V1 asks the public relay for ``assistant/<C>/<M>/payload``; when the public
relay has no committed local payload but does have a valid locator, it streams the payload from
the fixed Tail origin using the *same* Basic credential the request already presented.

Invariants (see ``docs/FILE_TRANSFER_SHARED_PAYLOAD_PROXY.md``):

* the feature is off unless ``MPT_RELAY_TAIL_PAYLOAD_PROXY=1``; the Tail relay itself must stay
  off, and the upstream path is always the same ``/mpt/relay/dav/assistant/<C>/<M>/payload``,
  so the proxy cannot recurse,
* the origin is the fixed configured Tail domain (loopback is the test seam only); neither a
  request header nor a locator field can choose a host,
* the locator is read from the authenticated namespace, bounded to 16 KiB and fully validated
  before any upstream connection is opened,
* only the request's already-validated ``Authorization`` header plus fixed framing headers are
  sent upstream (``Host`` always names the trusted Tail virtualhost, even through the loopback
  connector) — the credential is never stored, logged, echoed into the locator or written to disk,
* a Tail auth rejection (upstream 401/403) is answered as a local ``403`` with the fixed
  ``tail_auth_rejected`` marker — never ``502`` — so clients do not mistake it for an unavailable
  network and ask for a public copy,
* the body is streamed in bounded chunks; a short, wrong-sized or failed upstream truncates the
  local response and closes the connection instead of reporting a false success,
* when a GET's validated upstream turns out to be unavailable, the relay records one small,
  idempotent ``requests/relay-public.json`` in the same authenticated namespace so an online
  sender can publish a public copy for old clients. The record carries no credential and is
  never a receipt.
"""

from __future__ import annotations

import http.client
import json
import logging
import os
import re
import stat
import urllib.parse
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterator, Optional, Tuple

from .config import TRUSTED_TAIL_HOST, Config
from .fsutil import write_atomic as _write_atomic
from .logutil import mask_path
from .messages import BodyStream, Response, text_response
from .store import QuotaExceeded, Store, TooLarge

#: DAV layout written by the new clients: public locator root, Tail payload root.
LOCATOR_ROOT = "assistant-locator"
PAYLOAD_ROOT = "assistant"
LOCATOR_NAME = "manifest.json"
PAYLOAD_NAME = "payload"

#: The only locator route this relay knows. It is a route *id*, never a URL.
PAYLOAD_ROUTE = "mpt-tail-relay-v1"

#: Non-text attachment kinds a locator may carry: whole files and images both have a payload.
PAYLOAD_KINDS = ("file", "image")

#: Recovery request layout (``SharedLocatorRecords.SharedPublicCopyRequest``).
REQUESTS_DIR = "requests"
REQUEST_DEVICE = "relay-public"
REQUEST_REASON = "tail-unreachable"
#: The client accepts either reason; both mean "please publish a public copy".
REQUEST_REASONS = ("tail-unreachable", "tail-unavailable")

#: The Tail relay serves the standard prefix; the proxy never derives this from a request.
TAIL_RELAY_BASE_PATH = "/mpt/relay"

#: ``SharedLocatorRules.LocatorBytes`` in the client is 16 KiB; the relay applies the same bound.
MAX_LOCATOR_BYTES = 16 << 10
#: ``SharedLocatorRules.RequestBytes`` in the client is 2 KiB; the relay applies the same bound.
MAX_REQUEST_BYTES = 2 << 10
#: One upstream read is at most this large, so a payload of any size streams in bounded memory.
PROXY_CHUNK_BYTES = 64 << 10
#: A fixed marker set on the upstream request. A request already carrying it came from a proxy
#: hop and is never proxied again, which keeps a misconfigured loop from recursing.
UPSTREAM_HOP_HEADER = "x-mpt-shared-payload-hop"
UPSTREAM_ERROR_TEXT = "共享附件暂时不可用。\n"
#: Machine-readable marker for "the Tail relay rejected the credential" (a 403, not a network
#: failure). It is fixed text: no upstream body, header or credential is ever echoed.
TAIL_AUTH_REJECTED = "tail_auth_rejected"

#: ``AssistantValidation.ItemId`` is the lowercase Guid "N" form, same rule as inbox items.
ITEM_ID_PATTERN = re.compile(r"^[0-9a-f]{32}$")

_REASONS = {
    "locator-missing": "locator 不存在",
    "locator-path": "locator 路径越界",
    "locator-oversize": "locator 超过 16 KiB",
    "locator-not-regular": "locator 不是普通文件",
    "locator-unreadable": "locator 无法读取",
    "not-json": "locator 不是 JSON",
    "not-object": "locator 不是 JSON 对象",
    "locator-version": "locator version 不受支持",
    "route": "payloadRoute 不受信任",
    "message": "locator 缺少 message",
    "message-version": "message version 不受支持",
    "message-id": "message.id 与路径不一致",
    "kind": "只有 file/image 共享附件可以代理",
    "private": "私聊条目不能进入共享代理",
    "size": "message.size 非法",
    "size-limit": "message.size 超过服务上限",
    "self-origin": "上游地址指向本服务",
    "upstream-connect": "无法连接 Tail 中转",
    "upstream-status": "Tail 中转返回异常状态",
    "upstream-auth": "Tail 中转拒绝了凭据",
    "upstream-size": "Tail 中转长度与 locator 不符",
    "upstream-read": "Tail 中转响应中断",
    "upstream-short": "Tail 中转响应提前结束",
    "fallback-request": "恢复请求写入失败",
}


class LocatorError(Exception):
    """A locator record that must not lead to an upstream fetch."""

    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


@dataclass(frozen=True)
class SharedLocator:
    """The validated subset of a locator record the proxy needs."""

    message_id: str
    size: int


def enabled(config: Config) -> bool:
    return bool(config.tail_payload_proxy)


# -- locator ---------------------------------------------------------------------------


def parse_locator(raw: bytes, message_id: str, max_file_bytes: int) -> SharedLocator:
    """Validates a bounded locator record; raises :class:`LocatorError` on any violation.

    Extra fields (the client writes ``legacyFallbackAt``) are tolerated for forward
    compatibility; every field the proxy relies on is checked strictly.
    """
    try:
        payload = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as error:
        raise LocatorError("not-json") from error
    if not isinstance(payload, dict):
        raise LocatorError("not-object")
    if type(payload.get("version")) is not int or payload["version"] != 1:
        raise LocatorError("locator-version")
    if payload.get("payloadRoute") != PAYLOAD_ROUTE:
        raise LocatorError("route")
    message = payload.get("message")
    if not isinstance(message, dict):
        raise LocatorError("message")
    if type(message.get("version")) is not int or message["version"] != 1:
        raise LocatorError("message-version")
    if message.get("id") != message_id:
        raise LocatorError("message-id")
    # Only non-text shared attachments are proxied: ``file`` and ``image`` both have a payload,
    # text does not, and any other kind is not part of the V1 manifest contract.
    if message.get("kind") not in PAYLOAD_KINDS:
        raise LocatorError("kind")
    if message.get("targetDeviceId") is not None:
        raise LocatorError("private")
    size = message.get("size")
    if type(size) is not int or size < 0:
        raise LocatorError("size")
    if size > max_file_bytes:
        raise LocatorError("size-limit")
    return SharedLocator(message_id=message_id, size=size)


def read_locator(store: Store, conversation_id: str, message_id: str) -> Optional[SharedLocator]:
    """Reads ``assistant-locator/<C>/<M>/manifest.json`` from the authenticated namespace.

    The path is built from the authenticated conversation id and a validated item id only, so it
    cannot escape the conversation root. ``None`` means "no usable locator"; the caller keeps the
    ordinary 404 behaviour.
    """
    root = store.conversation_dir(conversation_id)
    path = root / LOCATOR_ROOT / conversation_id / message_id / LOCATOR_NAME
    normalized = Path(os.path.normpath(str(path)))
    if normalized != path or root not in normalized.parents:
        raise LocatorError("locator-path")
    try:
        info = os.stat(path)
    except OSError:
        return None
    if stat.S_ISDIR(info.st_mode):
        return None
    if not stat.S_ISREG(info.st_mode):
        raise LocatorError("locator-not-regular")
    if info.st_size > MAX_LOCATOR_BYTES:
        raise LocatorError("locator-oversize")
    try:
        # Read one byte past the bound so a file that grew between stat and read is still caught.
        with open(path, "rb") as source:
            raw = source.read(MAX_LOCATOR_BYTES + 1)
    except OSError as error:
        raise LocatorError("locator-unreadable") from error
    if len(raw) > MAX_LOCATOR_BYTES:
        raise LocatorError("locator-oversize")
    return parse_locator(raw, message_id, store.config.max_file_bytes)


# -- recovery request ------------------------------------------------------------------


def ensure_fallback_request(
    store: Store,
    conversation_id: str,
    message_id: str,
    log: Optional[logging.Logger] = None,
    masked_path: str = "",
) -> bool:
    """Records at most one public-copy request for one Tail-unavailable shared payload.

    The record is ``assistant-locator/<C>/<M>/requests/relay-public.json``, shaped like the
    client's ``SharedPublicCopyRequest`` so the existing sender loop sees it through the normal
    change cursor. It is written with the same namespace lock, quota reservation and atomic
    replace as a DAV ``PUT``; a valid existing record is left untouched, so repeated failed
    reads cause no rewrite and no revision churn. It carries no credential and is not a receipt.

    Returns ``True`` only when this call committed a new record.
    """
    root = store.conversation_dir(conversation_id)
    item_dir = root / LOCATOR_ROOT / conversation_id / message_id
    requests_dir = item_dir / REQUESTS_DIR
    path = requests_dir / f"{REQUEST_DEVICE}.json"
    normalized = Path(os.path.normpath(str(path)))
    if normalized != path or root not in normalized.parents:
        return False
    if not item_dir.is_dir():
        # The locator was read from this directory, but never create a namespace tree here.
        return False

    body = _request_body(message_id)
    with store.conversation_lock(conversation_id):
        if _existing_request_is_valid(path, message_id):
            return False
        try:
            if not requests_dir.is_dir():
                store.ensure_entry_budget(conversation_id)
                requests_dir.mkdir(exist_ok=True)
            existed = path.is_file()
            existing_size = path.stat().st_size if existed else 0
            reservation = store.reserve(conversation_id, replacing=existing_size, declared=len(body))
            try:
                if not existed:
                    store.ensure_entry_budget(conversation_id)
                # Same atomic replace + directory fsync as an upload: no partial record is ever
                # visible, and a crash leaves only a swept temp file.
                _write_atomic(path, body)
                reservation.commit(len(body), existing_size)
            finally:
                # No-op after a commit; gives the claim back on every failure path.
                reservation.release()
        except (QuotaExceeded, TooLarge, OSError):
            if log is not None:
                log.warning(
                    "shared payload fallback request failed path=%s reason=%s",
                    masked_path,
                    _REASONS["fallback-request"],
                )
            return False
        # A non-payload write is a small signal: it advances the namespace revision, which is
        # what wakes the sender's existing long poll. Never bump when nothing changed.
        store.bump_revision(conversation_id)
    if log is not None:
        log.info("shared payload fallback request recorded path=%s", masked_path)
    return True


def _request_body(message_id: str) -> bytes:
    record = {
        "version": 1,
        "itemId": message_id,
        "deviceId": REQUEST_DEVICE,
        "requestedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "reason": REQUEST_REASON,
    }
    return (json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


def _existing_request_is_valid(path: Path, message_id: str) -> bool:
    """True when the on-disk record is one the client would accept, so it must not be rewritten."""
    try:
        info = os.stat(path)
    except OSError:
        return False
    if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_REQUEST_BYTES:
        return False
    try:
        with open(path, "rb") as source:
            raw = source.read(MAX_REQUEST_BYTES + 1)
    except OSError:
        return False
    if len(raw) > MAX_REQUEST_BYTES:
        return False
    try:
        record = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return False
    if not isinstance(record, dict):
        return False
    if type(record.get("version")) is not int or record["version"] != 1:
        return False
    if record.get("itemId") != message_id or record.get("deviceId") != REQUEST_DEVICE:
        return False
    if record.get("reason") not in REQUEST_REASONS:
        return False
    requested_at = record.get("requestedAt")
    if not isinstance(requested_at, str) or not requested_at.strip():
        return False
    try:
        datetime.fromisoformat(requested_at.replace("Z", "+00:00"))
    except ValueError:
        return False
    return True


# -- proxy -----------------------------------------------------------------------------


def proxy(
    store: Store,
    config: Config,
    conversation_id: str,
    segments: Tuple[str, ...],
    trailing_slash: bool,
    request,
    log: Optional[logging.Logger] = None,
) -> Optional[Response]:
    """Answers a GET/HEAD that has no local payload from the fixed Tail origin.

    Returns ``None`` when the request is not an eligible shared payload GET/HEAD or when the
    upstream itself answers 404 — in both cases the caller keeps its normal "no payload" 404.
    Any other outcome is an explicit :class:`Response`.
    """
    if not enabled(config):
        return None
    if trailing_slash or len(segments) != 4:
        return None
    if segments[0] != PAYLOAD_ROOT or segments[3] != PAYLOAD_NAME:
        return None
    owner, message_id = segments[1], segments[2]
    if owner != conversation_id or not ITEM_ID_PATTERN.match(message_id):
        return None
    # A request that already went through a proxy hop is never proxied again.
    if request.header(UPSTREAM_HOP_HEADER) is not None:
        return None
    authorization = request.header("authorization") or ""
    if authorization.split(None, 1)[0].lower() != "basic":
        # Authentication already happened; this is defence in depth only.
        return None

    masked_path = mask_path(request.url_path, config.mask_log_ids)
    try:
        locator = read_locator(store, conversation_id, message_id)
    except LocatorError as error:
        _reject(log, masked_path, error.reason)
        return None
    if locator is None:
        return None

    origin = config.tail_origin()
    if _is_self_origin(request, origin):
        _reject(log, masked_path, "self-origin")
        return _upstream_error()
    # Only a GET records a recovery request. A HEAD is a metadata probe (the client also probes
    # health and directories) and must never mutate the namespace.
    on_unavailable: Optional[Callable[[], None]] = None
    if request.method == "GET":
        on_unavailable = lambda: ensure_fallback_request(  # noqa: E731 - one small closure
            store, conversation_id, message_id, log, masked_path
        )
    return _fetch(
        config,
        origin,
        authorization,
        conversation_id,
        message_id,
        locator,
        head_only=request.method == "HEAD",
        log=log,
        masked_path=masked_path,
        on_unavailable=on_unavailable,
    )


def _fetch(
    config: Config,
    origin: Tuple[str, str, int],
    authorization: str,
    conversation_id: str,
    message_id: str,
    locator: SharedLocator,
    *,
    head_only: bool,
    log: Optional[logging.Logger],
    masked_path: str,
    on_unavailable: Optional[Callable[[], None]] = None,
) -> Optional[Response]:
    scheme, host, port = origin
    path = (
        f"{TAIL_RELAY_BASE_PATH}/dav/{PAYLOAD_ROOT}/"
        f"{urllib.parse.quote(conversation_id, safe='')}/{urllib.parse.quote(message_id, safe='')}/{PAYLOAD_NAME}"
    )
    timeout = float(config.tail_payload_timeout_seconds)
    connection = None
    try:
        connection = _connect(scheme, host, port, timeout)
        # Only the credential the client already presented travels; every other client header is
        # dropped, and no redirect is ever followed (http.client has no redirect handling).
        # ``Host`` is always the fixed trusted Tail name, even when the configured origin is the
        # loopback connector (``deploy/connector``): the connector only supplies TCP reachability
        # to nginx's shared port 80, and nginx selects the virtualhost by this exact name.
        connection.request(
            "HEAD" if head_only else "GET",
            path,
            headers={
                "Host": TRUSTED_TAIL_HOST,
                "Authorization": authorization,
                "Accept-Encoding": "identity",
                "Connection": "close",
                UPSTREAM_HOP_HEADER: "1",
            },
        )
        upstream = connection.getresponse()
    except (OSError, http.client.HTTPException):
        # The Tail copy could not even be reached: the sender may need to publish a public copy.
        _notify_unavailable(on_unavailable)
        _close(connection)
        _reject(log, masked_path, "upstream-connect")
        return _upstream_error()

    status = int(upstream.status)
    if status == 404:
        # The locator exists but the Tail copy is not committed yet: the honest answer is the
        # ordinary "no payload" 404, which is exactly what an old client already handles.
        _notify_unavailable(on_unavailable)
        _close(connection, upstream)
        return None
    if status in (401, 403):
        # Tail authenticated the same Basic credential and rejected it. This is an authorization
        # answer, not an unavailable network: mapping it to 502 would make clients infer "network
        # unavailable" and ask for a public copy. Answer 403 with a fixed marker, echo nothing,
        # and never record a recovery request (for GET as well as HEAD).
        _reject(log, masked_path, "upstream-auth")
        _close(connection, upstream)
        return _tail_auth_rejected()
    if status != 200:
        if 500 <= status <= 599:
            # A Tail-side failure is exactly the case a public copy recovers from. 3xx and other
            # 4xx never request a copy: a redirect is refused outright, and an unknown client-side
            # answer must not be papered over by moving content to the public relay.
            _notify_unavailable(on_unavailable)
        _reject(log, masked_path, "upstream-status")
        _close(connection, upstream)
        return _upstream_error()

    declared = upstream.getheader("Content-Length")
    try:
        length = int(declared) if declared is not None else -1
    except ValueError:
        length = -1
    if length != locator.size:
        _notify_unavailable(on_unavailable)
        _reject(log, masked_path, "upstream-size")
        _close(connection, upstream)
        return _upstream_error()

    if head_only:
        _close(connection, upstream)
        return Response(
            status=200,
            content_type="application/octet-stream",
            content_length=length,
            extra_headers=(("Accept-Ranges", "none"),),
        )
    return Response(
        status=200,
        content_type="application/octet-stream",
        stream=_UpstreamStream(connection, upstream, length, log, masked_path, on_unavailable),
        extra_headers=(("Accept-Ranges", "none"),),
    )


class _UpstreamStream(BodyStream):
    """Streams exactly ``length`` upstream bytes and releases the upstream socket in ``close``.

    A read failure or a premature end sets :attr:`failed`; the transport then closes the client
    connection so the announced ``Content-Length`` is visibly short instead of silently wrong.
    The recovery callback (when this was a GET) runs in the failure path *before* ``close``, so a
    truncated stream still records the request that lets a sender publish a public copy.
    """

    def __init__(
        self,
        connection: http.client.HTTPConnection,
        upstream: http.client.HTTPResponse,
        length: int,
        log: Optional[logging.Logger],
        masked_path: str,
        on_unavailable: Optional[Callable[[], None]] = None,
    ) -> None:
        self._connection = connection
        self._upstream = upstream
        self.length = length
        self._remaining = length
        self._failed = False
        self._closed = False
        self._log = log
        self._masked_path = masked_path
        self._on_unavailable = on_unavailable

    @property
    def failed(self) -> bool:
        return self._failed

    def chunks(self) -> Iterator[bytes]:
        try:
            while self._remaining > 0:
                block = self._upstream.read(min(PROXY_CHUNK_BYTES, self._remaining))
                if not block:
                    self._fail("upstream-short")
                    break
                self._remaining -= len(block)
                yield block
        except (OSError, ValueError, http.client.HTTPException):
            self._fail("upstream-read")
        finally:
            self.close()

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        _close(self._connection, self._upstream)

    def _fail(self, reason: str) -> None:
        self._failed = True
        _reject(self._log, self._masked_path, reason)
        # Before close(): the callback only writes a small record and never raises.
        _notify_unavailable(self._on_unavailable)


def _connect(scheme: str, host: str, port: int, timeout: float) -> http.client.HTTPConnection:
    if scheme == "https":
        return http.client.HTTPSConnection(host, port, timeout=timeout)
    return http.client.HTTPConnection(host, port, timeout=timeout)


def _close(connection: Optional[http.client.HTTPConnection], upstream=None) -> None:
    if upstream is not None:
        try:
            upstream.close()
        except Exception:  # noqa: BLE001 - releasing a broken upstream must never raise
            pass
    if connection is not None:
        try:
            connection.close()
        except Exception:  # noqa: BLE001
            pass


def _is_self_origin(request, origin: Tuple[str, str, int]) -> bool:
    """Refuses an origin that is the very authority this request arrived on (a loop guard)."""
    _, host, port = origin
    raw = (request.header("host") or "").strip()
    if not raw:
        return False
    try:
        parsed = urllib.parse.urlsplit("//" + raw)
        hostname = (parsed.hostname or "").lower()
        request_port = parsed.port if parsed.port is not None else 80
    except ValueError:
        return False
    return hostname == host and int(request_port) == port


def _upstream_error() -> Response:
    return text_response(502, UPSTREAM_ERROR_TEXT, "text/plain; charset=utf-8", ("Retry-After", "5"))


def _tail_auth_rejected() -> Response:
    """403 + fixed marker: Tail rejected the credential. No upstream body is ever echoed."""
    return text_response(
        403,
        TAIL_AUTH_REJECTED + "\n",
        "text/plain; charset=utf-8",
        ("X-MPT-Relay-Error", TAIL_AUTH_REJECTED),
    )


def _notify_unavailable(callback: Optional[Callable[[], None]]) -> None:
    """Runs the recovery callback; a callback bug must never break the response path."""
    if callback is None:
        return
    try:
        callback()
    except Exception:  # noqa: BLE001 - the HTTP answer must survive a record-write failure
        pass


def _reject(log: Optional[logging.Logger], masked_path: str, reason: str) -> None:
    if log is None:
        return
    log.warning("shared payload proxy rejected path=%s reason=%s", masked_path, _REASONS.get(reason, reason))


__all__ = [
    "LOCATOR_NAME",
    "LOCATOR_ROOT",
    "MAX_LOCATOR_BYTES",
    "MAX_REQUEST_BYTES",
    "PAYLOAD_ROUTE",
    "REQUEST_DEVICE",
    "REQUEST_REASON",
    "SharedLocator",
    "TAIL_AUTH_REJECTED",
    "enabled",
    "ensure_fallback_request",
    "parse_locator",
    "proxy",
    "read_locator",
]

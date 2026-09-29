"""WebDAV over one private conversation root.

The wire behaviour is pinned by ``FileTransfer.Core.OpenListClient`` and
``OpenListClient.Assistant`` in the desktop/mobile client:

* ``MKCOL`` on an existing collection answers 405 (the client treats 405 as success),
  a missing parent answers 409, a created collection answers 201.
* ``PROPFIND`` answers 207 with ``DAV:`` ``response`` elements whose ``href`` keeps the
  public ``/mpt/relay/dav/`` prefix, so the client can resolve it against its root and
  take the last path segment as the item id.
* ``GET`` of a missing file answers 404 (the client reads that as "no manifest yet"), and
  a payload whose manifest was never published is simply not listed as a ready entry.
* ``PUT`` is atomic: bytes land in a hidden temp file in the same directory, are fsynced
  and then renamed, so a cancelled or failed upload never becomes visible.
* ``assistant/<conversationId>/...`` only ever resolves inside the authenticated
  conversation; any other id in that position is refused with 403.
"""

from __future__ import annotations

import errno
import logging
import os
import urllib.parse
from dataclasses import dataclass
from email.utils import formatdate
from pathlib import Path
from typing import Iterable, List, Optional, Sequence, Tuple
from xml.sax.saxutils import escape

from . import shared_payload
from .config import Config
from .fsutil import fsync_directory as _fsync_directory
from .fsutil import fsync_file as _fsync_file
from .fsutil import temp_name as _temp_name
from .fsutil import unlink_quietly as _unlink_quietly
from .messages import Request, Response, empty
from .store import QuotaExceeded, Store, TooLarge, is_internal_name

DAV_NAMESPACE = "DAV:"
MAX_SEGMENT_CHARS = 128


class DavError(Exception):
    """A WebDAV request that must be answered with a specific status."""

    def __init__(self, status: int, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.message = message


@dataclass(frozen=True)
class DavTarget:
    segments: Tuple[str, ...]
    trailing_slash: bool


def href_for(config: Config, segments: Sequence[str], is_directory: bool) -> str:
    base = config.normalized_base_path
    if not segments:
        return f"{base}/dav/"
    quoted = "/".join(urllib.parse.quote(segment, safe="!$&'()*+,;=:@-._~") for segment in segments)
    return f"{base}/dav/{quoted}" + ("/" if is_directory else "")


def parse_dav_target(config: Config, url_path: str) -> DavTarget:
    """Splits and validates the URL path; every rejection is a client error, never a lookup."""
    prefix = f"{config.normalized_base_path}/dav"
    if url_path in (prefix, prefix + "/"):
        return DavTarget((), True)
    if not url_path.startswith(prefix + "/"):
        raise DavError(404, "不是 WebDAV 路径。")
    remainder = url_path[len(prefix) + 1 :]
    trailing_slash = remainder.endswith("/")
    raw = remainder[:-1] if trailing_slash else remainder
    if raw == "":
        return DavTarget((), True)
    segments: List[str] = []
    for part in raw.split("/"):
        if part == "":
            raise DavError(400, "路径包含空段。")
        try:
            decoded = urllib.parse.unquote(part, errors="strict")
        except UnicodeDecodeError as error:  # pragma: no cover - unquote is lenient in practice
            raise DavError(400, "路径编码非法。") from error
        if any(character in decoded for character in ("/", "\\", "\x00")):
            raise DavError(400, "路径段不得包含分隔符。")
        if any(ord(character) < 32 or ord(character) == 127 for character in decoded):
            raise DavError(400, "路径段包含控制字符。")
        if decoded in (".", ".."):
            raise DavError(400, "路径不得包含相对段。")
        if decoded != decoded.strip():
            raise DavError(400, "路径段不得以空白开头或结尾。")
        if len(decoded) > MAX_SEGMENT_CHARS:
            raise DavError(400, "路径段过长。")
        segments.append(decoded)
    if len(segments) > config.max_path_depth:
        raise DavError(403, "路径层级超过上限。")
    return DavTarget(tuple(segments), trailing_slash)


def resolve(store: Store, conversation_id: str, segments: Sequence[str]) -> Path:
    """Maps segments onto the conversation's private root, enforcing the assistant rule."""
    if len(segments) >= 2 and segments[0] == "assistant" and segments[1] != conversation_id:
        # The assistant layout is per conversation; another conversation's tree is not a
        # namespace this credential may address, not even for a 404 probe.
        raise DavError(403, "assistant 命名空间只能访问本会话。")
    root = store.conversation_dir(conversation_id)
    candidate = root.joinpath(*segments) if segments else root
    normalized = Path(os.path.normpath(str(candidate)))
    if normalized != root and root not in normalized.parents:
        raise DavError(403, "路径越界。")
    return normalized


def handle(
    store: Store,
    config: Config,
    conversation_id: str,
    request: Request,
    log: Optional[logging.Logger] = None,
) -> Response:
    """Dispatches one WebDAV method for an already authenticated conversation."""
    target = parse_dav_target(config, request.url_path)
    path = resolve(store, conversation_id, target.segments)
    method = request.method
    if method == "OPTIONS":
        return _options(path)
    if method in ("GET", "HEAD"):
        return _get_or_shared_proxy(
            store, config, conversation_id, target, path, request, log, head_only=(method == "HEAD")
        )
    if method == "PUT":
        return _put(store, config, conversation_id, target, path, request)
    if method == "MKCOL":
        return _mkcol(store, conversation_id, path, target, request)
    if method == "PROPFIND":
        return _propfind(store, config, conversation_id, path, target, request)
    if method == "DELETE":
        return _delete(store, conversation_id, target, path)
    if method in ("PROPPATCH", "LOCK", "UNLOCK", "MOVE", "COPY", "POST", "PATCH"):
        raise DavError(405, f"不支持的 WebDAV 方法：{method}。")
    raise DavError(405, f"不支持的方法：{method}。")


def _get_or_shared_proxy(
    store: Store,
    config: Config,
    conversation_id: str,
    target: DavTarget,
    path: Path,
    request: Request,
    log: Optional[logging.Logger],
    head_only: bool,
) -> Response:
    """A committed local payload always wins; a missing one may be proxied from the Tail relay.

    Only a plain 404 falls through to the proxy. A collection (405), an internal name or any
    other rejection keeps its existing answer, so the proxy cannot widen the DAV surface.
    """
    try:
        return _get(path, head_only=head_only)
    except DavError as error:
        if error.status != 404:
            raise
    proxied = shared_payload.proxy(
        store, config, conversation_id, target.segments, target.trailing_slash, request, log
    )
    if proxied is not None:
        return proxied
    raise DavError(404, "资源不存在。")


# -- methods ---------------------------------------------------------------------------


def _options(path: Path) -> Response:
    return Response(
        status=200,
        extra_headers=(
            ("Allow", "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, MKCOL"),
            ("DAV", "1"),
            ("MS-Author-Via", "DAV"),
        ),
    )


def _get(path: Path, head_only: bool) -> Response:
    if path.is_dir():
        raise DavError(405, "集合不支持 GET。")
    if is_internal_name(path.name) or not path.is_file():
        raise DavError(404, "资源不存在。")
    info = path.stat()
    return Response(
        status=200,
        content_type="application/octet-stream",
        file_path=path,
        head_only=head_only,
        extra_headers=(
            ("Last-Modified", formatdate(info.st_mtime, usegmt=True)),
            ("Accept-Ranges", "none"),
        ),
    )


def _put(store: Store, config: Config, conversation_id: str, target: DavTarget, path: Path, request: Request) -> Response:
    if target.trailing_slash:
        raise DavError(405, "PUT 的目标必须是文件。")
    if is_internal_name(path.name):
        raise DavError(400, "该名称由服务保留。")
    if request.header("content-range") is not None:
        raise DavError(400, "不支持分片 PUT。")

    # The whole write is serialized per conversation: quota accounting is only exact when two
    # uploads of one conversation cannot interleave between "check" and "commit".
    with store.conversation_lock(conversation_id):
        if path.is_dir():
            raise DavError(405, "PUT 的目标已是集合。")
        parent = path.parent
        if not parent.is_dir():
            raise DavError(409, "父集合不存在。")
        existed = path.is_file()
        existing_size = path.stat().st_size if existed else 0
        declared = request.body.declared_length
        try:
            reservation = store.reserve(conversation_id, replacing=existing_size, declared=declared)
        except (QuotaExceeded, TooLarge) as error:
            raise DavError(_quota_status(error), str(error)) from error
        if not existed:
            try:
                store.ensure_entry_budget(conversation_id)
            except (QuotaExceeded, TooLarge) as error:
                reservation.release()
                raise DavError(_quota_status(error), str(error)) from error

        temporary = parent / _temp_name()
        written = 0

        def on_bytes(count: int) -> None:
            # Reserves before the next piece is read, so an undeclared (chunked) upload is
            # accounted for piece by piece and can never overrun the global ceiling.
            reservation.grow(count)

        try:
            written = request.body.stream_to(temporary, on_bytes)
            if declared is not None and written != declared:
                raise DavError(400, "请求体长度与 Content-Length 不符。")
            _fsync_file(temporary)
            os.replace(temporary, path)
            _fsync_directory(parent)
            # Commit while the reservation is still held: releasing first would open a window
            # where another conversation could claim the same global bytes.
            reservation.commit(written, existing_size)
        except DavError:
            _unlink_quietly(temporary)
            raise
        except (QuotaExceeded, TooLarge) as error:
            _unlink_quietly(temporary)
            raise DavError(_quota_status(error), str(error)) from error
        except BaseException:
            _unlink_quietly(temporary)
            raise
        finally:
            # No-op after a successful commit; gives the claim back on every failure path
            # (cancelled upload, quota abort, disk error, client disconnect).
            reservation.release()
        if not _is_payload(path):
            store.bump_revision(conversation_id)
        etag = f'"{written:x}-{int(path.stat().st_mtime):x}"'
    return empty(204 if existed else 201, ("ETag", etag))


def _mkcol(store: Store, conversation_id: str, path: Path, target: DavTarget, request: Request) -> Response:
    if request.body.declared_length not in (None, 0):
        raise DavError(415, "MKCOL 不接受请求体。")
    request.body.discard()
    if path.exists() or path.is_symlink():
        raise DavError(405, "集合已存在。")
    if not path.parent.is_dir():
        raise DavError(409, "父集合不存在。")
    with store.conversation_lock(conversation_id):
        try:
            # Inside the guard: a full namespace must answer 507, never a generic 500.
            store.ensure_entry_budget(conversation_id)
            path.mkdir()
        except FileExistsError:
            raise DavError(405, "集合已存在。") from None
        except OSError as error:
            if error.errno in (errno.ENOSPC, errno.EDQUOT):
                raise DavError(507, "磁盘空间不足。") from error
            raise
    return empty(201)


def _propfind(store: Store, config: Config, conversation_id: str, path: Path, target: DavTarget, request: Request) -> Response:
    depth = (request.header("depth") or "1").strip().lower()
    if depth in ("infinity", "inf"):
        raise DavError(403, "不支持 Depth: infinity。")
    if depth not in ("0", "1"):
        raise DavError(400, "Depth 只支持 0 或 1。")
    request.body.discard()
    if not path.exists() and not path.is_symlink():
        raise DavError(404, "资源不存在。")
    if is_internal_name(path.name):
        raise DavError(404, "资源不存在。")

    entries: List[Tuple[Tuple[str, ...], Path, bool]] = [(target.segments, path, path.is_dir())]
    if depth == "1" and path.is_dir():
        for name in _list_directory(path):
            child = path / name
            entries.append((target.segments + (name,), child, child.is_dir()))
    return Response(
        status=207,
        body=_multistatus(config, entries),
        content_type='application/xml; charset="utf-8"',
        extra_headers=(("Cache-Control", "no-store"),),
    )


def _delete(store: Store, conversation_id: str, target: DavTarget, path: Path) -> Response:
    if not target.segments:
        raise DavError(403, "不能删除会话根目录。")
    if is_internal_name(path.name):
        raise DavError(404, "资源不存在。")
    if not path.exists() and not path.is_symlink():
        raise DavError(404, "资源不存在。")
    with store.conversation_lock(conversation_id):
        try:
            freed = store.remove_tree(path)
        except OSError as error:
            raise DavError(409, f"删除失败：{error.strerror or error}。") from error
    store.apply_usage(conversation_id, 0, freed)
    if not _is_payload(path):
        store.bump_revision(conversation_id)
    return empty(204)


# -- helpers ---------------------------------------------------------------------------


def _is_payload(path: Path) -> bool:
    """Payload uploads are silent: only publish markers wake a long poll."""
    return path.name == "payload"


def _quota_status(error: Exception) -> int:
    if isinstance(error, TooLarge):
        return 413
    return 507


def _list_directory(path: Path) -> List[str]:
    """Sorted children of a collection, with the service's own temp files hidden."""
    names = []
    with os.scandir(path) as entries:
        for entry in entries:
            if is_internal_name(entry.name):
                continue
            names.append(entry.name)
    names.sort()
    return names


def _multistatus(config: Config, entries: Iterable[Tuple[Tuple[str, ...], Path, bool]]) -> bytes:
    parts = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<D:multistatus xmlns:D="{DAV_NAMESPACE}">',
    ]
    for segments, path, is_directory in entries:
        try:
            info = path.stat()
        except OSError:
            continue
        href = href_for(config, segments, is_directory)
        size = 0 if is_directory else info.st_size
        resource_type = "<D:collection/>" if is_directory else ""
        display = escape(segments[-1]) if segments else ""
        parts.append(
            "<D:response>"
            f"<D:href>{escape(href)}</D:href>"
            "<D:propstat><D:prop>"
            f"<D:getlastmodified>{formatdate(info.st_mtime, usegmt=True)}</D:getlastmodified>"
            f"<D:getcontentlength>{size}</D:getcontentlength>"
            f"<D:resourcetype>{resource_type}</D:resourcetype>"
            f"<D:displayname>{display}</D:displayname>"
            "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>"
            "</D:response>"
        )
    parts.append("</D:multistatus>")
    return "".join(parts).encode("utf-8")


__all__ = [
    "DavError",
    "DavTarget",
    "handle",
    "href_for",
    "parse_dav_target",
    "resolve",
]

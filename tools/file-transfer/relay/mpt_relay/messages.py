"""Transport-neutral request/response objects.

The HTTP layer (``httpd``) adapts ``BaseHTTPRequestHandler`` onto :class:`Request` and
writes :class:`Response`. Everything else — routing, the API, WebDAV — is a pure function
of those two objects, which is what makes the integration tests able to exercise the real
service over a real socket without importing a test-only code path.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterator, List, Mapping, Optional, Tuple

JSON_CONTENT_TYPE = "application/json; charset=utf-8"
TEXT_CONTENT_TYPE = "text/plain; charset=utf-8"


class BodyReader:
    """Interface of the request body; implemented by the HTTP layer, faked by unit tests."""

    #: ``Content-Length`` when the client declared one, else ``None`` (chunked or no body).
    declared_length: Optional[int] = None

    def read_all(self, limit: int) -> bytes:
        raise NotImplementedError

    def stream_to(self, target: Path, on_bytes: Callable[[int], None]) -> int:
        raise NotImplementedError

    def discard(self) -> None:
        raise NotImplementedError

    def consumed(self) -> bool:
        raise NotImplementedError


class BodyStream:
    """A bounded chunk source for a streaming response body.

    ``length`` is the exact number of bytes :meth:`chunks` will yield; the transport announces it
    in ``Content-Length`` before the first chunk, so a stream must know its size up front. The
    shared payload proxy does: the locator record carries the payload size.

    :meth:`close` is called by the transport exactly once — after a completed body, after the
    client disconnected mid-body, or when the stream failed — and is where an upstream socket is
    released. Implementations must therefore make ``close`` idempotent.
    """

    #: Total bytes the stream will yield.
    length: int = 0

    @property
    def failed(self) -> bool:
        """True when the source ended before delivering the announced length."""
        return False

    def chunks(self) -> Iterator[bytes]:
        raise NotImplementedError

    def close(self) -> None:
        return None


@dataclass
class Request:
    method: str
    url_path: str
    query: Mapping[str, List[str]]
    headers: Mapping[str, str]
    remote_ip: str
    body: BodyReader
    #: Returns True when the peer closed the connection (used to end a long poll early).
    peer_closed: Callable[[], bool] = lambda: False

    def header(self, name: str) -> Optional[str]:
        return self.headers.get(name.lower())

    def query_one(self, name: str) -> Optional[str]:
        values = self.query.get(name)
        return values[0] if values else None


@dataclass
class Response:
    status: int
    body: bytes = b""
    content_type: Optional[str] = None
    extra_headers: Tuple[Tuple[str, str], ...] = ()
    file_path: Optional[Path] = None
    head_only: bool = False
    #: True when the connection must not be reused (an unconsumed request body is pending).
    close: bool = False
    #: Streaming body source, used instead of ``body``/``file_path``; closed by the transport.
    stream: Optional[BodyStream] = None
    #: Explicit ``Content-Length`` for a response without a local body (a proxied HEAD).
    content_length: Optional[int] = None

    def with_headers(self, *headers: Tuple[str, str]) -> "Response":
        return Response(
            status=self.status,
            body=self.body,
            content_type=self.content_type,
            extra_headers=tuple(self.extra_headers) + tuple(headers),
            file_path=self.file_path,
            head_only=self.head_only,
            close=self.close,
            stream=self.stream,
            content_length=self.content_length,
        )


def json_response(status: int, payload: object, *headers: Tuple[str, str]) -> Response:
    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
    return Response(
        status=status,
        body=body,
        content_type=JSON_CONTENT_TYPE,
        extra_headers=tuple(headers) + (("Cache-Control", "no-store"),),
    )


def text_response(status: int, text: str, content_type: str = TEXT_CONTENT_TYPE, *headers: Tuple[str, str]) -> Response:
    return Response(
        status=status,
        body=text.encode("utf-8"),
        content_type=content_type,
        extra_headers=tuple(headers),
    )


def empty(status: int, *headers: Tuple[str, str]) -> Response:
    return Response(status=status, extra_headers=tuple(headers))

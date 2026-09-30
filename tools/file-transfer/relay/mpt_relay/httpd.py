"""HTTP/1.1 transport: routing adapter, streaming bodies, long-poll disconnect detection.

Standard library only (``http.server``/``socketserver``). The service is expected to sit
behind nginx on loopback, so there is no TLS here: nginx terminates TLS and forwards the
``/mpt/relay/`` prefix unchanged, which is why every URL this module sees still carries the
public prefix.
"""

from __future__ import annotations

import logging
import os
import re
import select
import socket
import socketserver
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from .app import RelayApp
from .config import Config
from .logutil import mask_path
from .messages import BodyReader, Request, Response, text_response
from .store import Store

MAX_DISCARD_BYTES = 8 << 20
STREAM_CHUNK = 256 << 10
UPLOAD_CHUNK = 64 << 10
#: Framing lines (chunk size, trailers) are read in small pieces from the same bounded reader.
LINE_CHUNK = 1024
#: A chunk-size line is hex digits plus optional extensions; anything longer is a framing error.
MAX_CHUNK_LINE_BYTES = 64
#: Trailers are optional and never useful to this service, so they are bounded hard.
MAX_TRAILER_LINE_BYTES = 1024
MAX_TRAILER_BYTES = 8 << 10
_CHUNK_LENGTH = re.compile(rb"[0-9a-fA-F]+")


class BodyError(Exception):
    """The request body cannot be read as declared."""


class HttpBody(BodyReader):
    """Streams the request body straight to disk when a PUT asks for it.

    Request bodies are read in bounded pieces, including ``Transfer-Encoding: chunked``:
    a chunk length is a *client* claim, so it is parsed strictly (hex only, bounded digits,
    CRLF terminated) and a single chunk is then consumed through repeated reads of at most
    :data:`UPLOAD_CHUNK` bytes. A hostile ``ffffffff`` chunk therefore costs one 64 KiB
    buffer instead of a multi-gigabyte allocation, and every piece still passes the quota
    callback before the next one is read.
    """

    def __init__(self, handler: BaseHTTPRequestHandler, body_budget_seconds: float = 900.0) -> None:
        self._reader = handler.rfile
        self._connection = getattr(handler, "connection", None)
        # Restore exactly what the handler configured, so narrowing the timeout for the body
        # budget cannot leave the connection with a different keep-alive timeout afterwards.
        server_config = getattr(getattr(handler, "server", None), "config", None)
        self._socket_timeout = float(
            getattr(server_config, "socket_timeout_seconds", None)
            or getattr(handler, "timeout", None)
            or 120.0
        )
        self._chunked = "chunked" in (handler.headers.get("Transfer-Encoding") or "").lower()
        raw_length = handler.headers.get("Content-Length")
        declared: Optional[int] = None
        if raw_length is not None and not self._chunked:
            try:
                declared = int(raw_length)
            except ValueError as error:
                raise BodyError("Content-Length 不是整数。") from error
            if declared < 0:
                raise BodyError("Content-Length 不能为负数。")
        self.declared_length = declared if self._chunked else (declared if declared is not None else 0)
        self._remaining = None if self._chunked else int(self.declared_length or 0)
        self._chunk_remaining = 0
        self._chunked_done = False
        # Bytes already read from the socket but not consumed by the parser yet.
        self._pending = b""
        self._finished = self._remaining == 0
        # A wall-clock budget for the whole body, not just per read: without it a client that
        # drips one byte before every timeout could hold a connection slot open forever. The
        # budget only applies when there is a body, so a 25 second long poll is unaffected.
        has_body = self._chunked or bool(self._remaining)
        self._deadline = (time.monotonic() + max(1.0, float(body_budget_seconds))) if has_body else None

    # -- BodyReader ------------------------------------------------------------------

    def read_all(self, limit: int) -> bytes:
        if self.declared_length is not None and self.declared_length > limit:
            raise BodyError("请求体过大。")
        if self._finished:
            return b""
        chunks: List[bytes] = []
        total = 0
        while True:
            piece = self._read_piece(UPLOAD_CHUNK)
            if piece is None:
                break
            total += len(piece)
            if total > limit:
                raise BodyError("请求体过大。")
            chunks.append(piece)
        return b"".join(chunks)

    def stream_to(self, target: Path, on_bytes) -> int:
        written = 0
        try:
            with open(target, "wb") as output:
                while True:
                    piece = self._read_piece(UPLOAD_CHUNK)
                    if piece is None:
                        break
                    output.write(piece)
                    written += len(piece)
                    on_bytes(written)
                output.flush()
                os.fsync(output.fileno())
        finally:
            self._restore_timeout()
        self._finished = True
        return written

    def chunks(self, deadline=None):
        if deadline is not None:
            self._deadline = min(self._deadline, deadline) if self._deadline is not None else deadline
        try:
            while True:
                piece = self._read_piece(UPLOAD_CHUNK)
                if piece is None:
                    break
                yield piece
        finally:
            self._restore_timeout()

    def abort(self) -> None:
        if self._connection is not None:
            try:
                self._connection.shutdown(socket.SHUT_RD)
            except OSError:
                pass

    def discard(self) -> None:
        if self._finished:
            return
        total = 0
        try:
            while True:
                piece = self._read_piece(UPLOAD_CHUNK)
                if piece is None:
                    break
                total += len(piece)
                if total > MAX_DISCARD_BYTES:
                    # Too much to swallow: leave the connection marked unconsumed so it closes.
                    return
        finally:
            self._restore_timeout()

    def consumed(self) -> bool:
        return self._finished

    # -- internals -------------------------------------------------------------------

    def _read_piece(self, size: int) -> Optional[bytes]:
        """Returns at most ``size`` bytes, or ``None`` at a clean end of body."""
        if size <= 0:
            raise BodyError("读取长度非法。")
        if self._chunked:
            return self._read_chunked_piece(size)
        if self._remaining is None or self._remaining <= 0:
            self._finished = True
            self._restore_timeout()
            return None
        data = self._pull(min(size, UPLOAD_CHUNK, self._remaining))
        if not data:
            raise BodyError("请求体提前结束。")
        if len(data) > size:  # pragma: no cover - the reader is asked for at most size
            raise BodyError("读取长度超出请求。")
        self._remaining -= len(data)
        if self._remaining == 0:
            self._finished = True
            self._restore_timeout()
        return data

    def _pull(self, want: int) -> bytes:
        """At most one underlying socket read, buffered bytes first."""
        if self._pending:
            taken = self._pending[:want]
            self._pending = self._pending[len(taken) :]
            return bytes(taken)
        data = self._pull_socket(want)
        if data and len(data) > want:  # pragma: no cover - defensive
            self._pending = data[want:]
            return bytes(data[:want])
        return data

    def _pull_socket(self, want: int) -> bytes:
        """Exactly one socket read.

        ``BufferedReader.read()`` loops over ``recv`` internally and every ``recv`` restarts
        the socket timeout, so a client dripping one byte at a time could stretch a read
        forever and defeat the body budget. ``read1`` performs a single read of whatever is
        available (at most ``want`` bytes), which puts the deadline check between every real
        read and is what makes the wall-clock budget enforceable.
        """
        self._arm_deadline()
        read1 = getattr(self._reader, "read1", None)
        return read1(want) if read1 is not None else self._reader.read(want)

    def _arm_deadline(self) -> None:
        """Fails once the body budget is spent, and never blocks past it.

        The socket timeout is narrowed to the remaining budget, so a stalled read is
        terminated by the deadline instead of the (longer) idle timeout.
        """
        if self._deadline is None:
            return
        remaining = self._deadline - time.monotonic()
        if remaining <= 0:
            raise BodyError("请求体读取超出时间预算。")
        if self._connection is None:
            return
        try:
            self._connection.settimeout(min(float(self._socket_timeout), max(0.05, remaining)))
        except OSError:  # pragma: no cover - a closed socket is reported by the read itself
            pass

    def _restore_timeout(self) -> None:
        if self._deadline is None or self._connection is None:
            return
        try:
            self._connection.settimeout(float(self._socket_timeout))
        except OSError:  # pragma: no cover - connection already gone
            pass

    def _read_exact(self, count: int) -> bytes:
        """Reads exactly ``count`` buffered bytes, bounded by the deadline per underlying read."""
        while len(self._pending) < count:
            # Socket bytes only: _pull would hand back what is already buffered and the loop
            # would never make progress at EOF.
            chunk = self._pull_socket(count - len(self._pending))
            if not chunk:
                raise BodyError("分块请求体提前结束。")
            self._pending += chunk
        taken = self._pending[:count]
        self._pending = self._pending[count:]
        return bytes(taken)

    def _read_line(self, limit: int) -> bytes:
        """Reads one CRLF-terminated line without ever looping inside a socket read.

        Returns the bytes collected so far at EOF or once ``limit`` is exceeded; the caller
        rejects those, so a drip inside a length line or a trailer cannot stall the request.
        """
        while True:
            index = self._pending.find(b"\n")
            if index >= 0:
                line = bytes(self._pending[: index + 1])
                self._pending = self._pending[index + 1 :]
                return line
            if len(self._pending) > limit:
                return bytes(self._pending)
            # Socket bytes only (see _read_exact): a drip inside a framing line must make no
            # progress only until the budget expires, never loop on the same buffered bytes.
            chunk = self._pull_socket(LINE_CHUNK)
            if not chunk:
                return bytes(self._pending)
            self._pending += chunk

    def _read_chunked_piece(self, size: int) -> Optional[bytes]:
        if self._chunked_done:
            self._finished = True
            return None
        if self._chunk_remaining == 0:
            length = self._read_chunk_length()
            if length is None:
                self._chunked_done = True
                self._finished = True
                self._restore_timeout()
                return None
            self._chunk_remaining = length
        want = min(size, UPLOAD_CHUNK, self._chunk_remaining)
        data = self._pull(want)
        if not data:
            raise BodyError("分块请求体提前结束。")
        self._chunk_remaining -= len(data)
        if self._chunk_remaining == 0:
            if self._read_exact(2) != b"\r\n":
                raise BodyError("分块数据缺少结尾 CRLF。")
        return data

    def _read_chunk_length(self) -> Optional[int]:
        """Parses one chunk-size line; ``None`` means the terminating zero chunk was seen."""
        line = self._read_line(MAX_CHUNK_LINE_BYTES)
        if not line:
            raise BodyError("分块长度行缺失。")
        if len(line) > MAX_CHUNK_LINE_BYTES:
            raise BodyError("分块长度行过长。")
        if not line.endswith(b"\r\n"):
            # A length line must be CRLF terminated; a bare LF (or a truncated line) is a
            # framing error, not something to guess about.
            raise BodyError("分块长度行缺少 CRLF。")
        core = line[:-2]
        if b";" in core:
            core = core.split(b";", 1)[0]
        if not core or len(core) > 16 or not _CHUNK_LENGTH.match(core):
            # Rejects "", "+1", "-1", "0x10", "zz" and anything longer than 64 bits.
            raise BodyError("分块长度不是合法的十六进制数。")
        value = int(core, 16)
        if value == 0:
            self._read_trailers()
            return None
        return value

    def _read_trailers(self) -> None:
        total = 0
        while True:
            line = self._read_line(MAX_TRAILER_LINE_BYTES)
            if not line:
                raise BodyError("trailers 提前结束。")
            total += len(line)
            if len(line) > MAX_TRAILER_LINE_BYTES or total > MAX_TRAILER_BYTES:
                raise BodyError("trailers 过长。")
            if line == b"\r\n":
                return
            if not line.endswith(b"\r\n"):
                raise BodyError("trailer 行缺少 CRLF。")


def client_ip(peer_ip: str, headers: Dict[str, str], config: Config) -> str:
    """The real client address, but only from a proxy we are configured to believe."""
    if not config.trust_proxy_headers or peer_ip not in config.trusted_proxies:
        return peer_ip
    real = (headers.get("x-real-ip") or "").strip()
    if real:
        return real
    forwarded = headers.get("x-forwarded-for")
    if forwarded:
        return forwarded.split(",")[-1].strip()
    return peer_ip


class RelayHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "mpt-relay"
    sys_version = ""
    server: "RelayServer"

    # -- plumbing --------------------------------------------------------------------

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(self.server.config.socket_timeout_seconds)
        self._body: Optional[HttpBody] = None
        self._started = time.monotonic()
        self._log_path = ""
        self._client_ip = self.client_address[0]

    def log_request(self, code: object = "-", size: object = "-") -> None:
        pass  # the service logs its own single access line without credentials

    def log_error(self, format: str, *args: object) -> None:  # noqa: A002 - stdlib signature
        self.server.log.debug("http error: " + (format % args))

    def log_message(self, format: str, *args: object) -> None:  # noqa: A002 - stdlib signature
        self.server.log.debug("http: " + (format % args))

    def handle_one_request(self) -> None:
        try:
            super().handle_one_request()
        except (ConnectionResetError, BrokenPipeError, socket.timeout, TimeoutError):
            self.close_connection = True

    def _dispatch(self) -> None:
        self._log_path = urllib.parse.urlsplit(self.path).path
        started = self._started
        response: Optional[Response] = None
        try:
            body = HttpBody(self, self.server.config.body_budget_seconds)
            self._body = body
            parts = urllib.parse.urlsplit(self.path)
            headers = {key.lower(): value for key, value in self.headers.items()}
            self._client_ip = client_ip(self.client_address[0], headers, self.server.config)
            request = Request(
                method=self.command,
                url_path=parts.path,
                query=urllib.parse.parse_qs(parts.query, keep_blank_values=True),
                headers=headers,
                remote_ip=self._client_ip,
                body=body,
                peer_closed=self._peer_closed,
                wait_for_signal=self._wait_for_signal,
                abort_response=self._abort_response,
            )
            response = self.server.app.handle(request)
        except BodyError as error:
            response = text_response(400, f"{error}\n")
            response.close = True
        except (ConnectionResetError, BrokenPipeError, socket.timeout, TimeoutError):
            self.close_connection = True
            return
        except Exception:  # noqa: BLE001 - a handler bug must not kill the connection silently
            self.server.log.exception("unhandled relay error")
            response = text_response(500, "internal error\n")
            response.close = True

        if response is None:  # pragma: no cover - defensive
            response = text_response(500, "internal error\n")
            response.close = True
        if self._body is not None and not self._body.consumed():
            response.close = True
        self._write_response(response)
        self._access_log(response.status, started)

    def _peer_closed(self) -> bool:
        try:
            readable, _, _ = select.select([self.connection], [], [], 0)
            if not readable:
                return False
            return self.connection.recv(1, socket.MSG_PEEK) == b""
        except OSError:
            return True

    def _wait_for_signal(self, signal_reader, timeout: float) -> str:
        try:
            readable, _, _ = select.select([self.connection, signal_reader], [], [], max(0, timeout))
            if self.connection in readable:
                # A cloud long poll/GET has no unread input. Pipelined input ends this wait too,
                # rather than spinning on a readable socket or reading another request's bytes.
                return "peer_closed"
            return "notified" if signal_reader in readable else "timeout"
        except (OSError, ValueError):
            return "peer_closed"

    def _abort_response(self) -> None:
        try:
            self.connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass

    def _access_log(self, status: int, started: float) -> None:
        self.server.log.info(
            "%s %s %s %s %.3fs",
            self._client_ip,
            self.command,
            mask_path(self._log_path, self.server.config.mask_log_ids),
            status,
            time.monotonic() - started,
        )

    def _write_response(self, response: Response) -> None:
        body = response.body or b""
        file_path = response.file_path
        stream = response.stream
        if response.content_length is not None:
            length = int(response.content_length)
        elif stream is not None:
            length = int(stream.length)
        else:
            length = len(body)
        if file_path is not None and stream is None and response.content_length is None:
            try:
                length = os.stat(file_path).st_size
            except OSError:
                response = text_response(404, "资源不存在。\n")
                file_path = None
                body = response.body
                length = len(body)
        try:
            if stream is not None and stream.deadline is not None:
                self._arm_stream_write(stream.deadline)
            self.send_response(response.status)
            if response.content_type:
                self.send_header("Content-Type", response.content_type)
            self.send_header("Content-Length", str(length))
            for name, value in response.extra_headers:
                self.send_header(name, value)
            if response.close:
                self.close_connection = True
                self.send_header("Connection", "close")
            self.end_headers()
            if self.command == "HEAD" or response.head_only:
                return
            if stream is not None:
                # Bounded chunks straight from the source: nothing is buffered whole and nothing
                # is copied to disk, so a shared payload can stream at any size.
                for chunk in stream.chunks():
                    if chunk:
                        if stream.deadline is not None:
                            self._arm_stream_write(stream.deadline)
                        self.wfile.write(chunk)
                if stream.failed:
                    # The announced length was not delivered. Close so the peer sees the
                    # truncation immediately instead of keeping the connection (and a desynced
                    # keep-alive) open; a truncated body is never reported as success.
                    self.close_connection = True
            elif file_path is not None:
                with open(file_path, "rb") as source:
                    while True:
                        chunk = source.read(STREAM_CHUNK)
                        if not chunk:
                            break
                        self.wfile.write(chunk)
            elif body:
                self.wfile.write(body)
        except (ConnectionResetError, BrokenPipeError, socket.timeout, TimeoutError):
            # The peer went away: a cancelled upload or an abandoned long poll. There is nobody
            # left to answer, and any upload temp file was already removed by the handler that
            # owned it, so the conversation tree stays exactly as it was.
            self.close_connection = True
        finally:
            # The transport owns a streaming body: this runs after a completed response, after a
            # client disconnect (the write above raised) and on truncation, which is what closes
            # the upstream socket instead of leaking it until a timeout.
            if stream is not None:
                stream.close()

    def _arm_stream_write(self, deadline: float) -> None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError()
        self.connection.settimeout(min(float(self.server.config.socket_timeout_seconds), remaining))

    do_GET = _dispatch
    do_HEAD = _dispatch
    do_POST = _dispatch
    do_PUT = _dispatch
    do_DELETE = _dispatch
    do_OPTIONS = _dispatch
    do_PROPFIND = _dispatch
    do_MKCOL = _dispatch
    do_PROPPATCH = _dispatch
    do_LOCK = _dispatch
    do_UNLOCK = _dispatch
    do_MOVE = _dispatch
    do_COPY = _dispatch
    do_PATCH = _dispatch


def mask_for_log(config: Config, path: str) -> str:
    return mask_path(path, config.mask_log_ids)


class RelayServer(ThreadingHTTPServer):
    """A threaded loopback server with a hard connection ceiling."""

    daemon_threads = True
    allow_reuse_address = True
    request_queue_size = 64

    def __init__(self, config: Config, app: RelayApp, store: Store, logger: Optional[logging.Logger] = None) -> None:
        self.config = config
        self.app = app
        self.store = store
        self.log = logger or logging.getLogger("mpt_relay")
        self._slots = threading.BoundedSemaphore(max(1, config.max_connections))
        self._maintenance: Optional[threading.Thread] = None
        super().__init__((config.host, config.port), RelayHandler)

    @property
    def port(self) -> int:
        return int(self.server_address[1])

    def process_request(self, request: socketserver.BaseRequestHandler, client_address: Tuple[str, int]) -> None:
        if not self._slots.acquire(timeout=5.0):
            try:
                request.sendall(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            except OSError:
                pass
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self._slots.release()
            raise

    def shutdown_request(self, request: socketserver.BaseRequestHandler) -> None:
        try:
            super().shutdown_request(request)
        finally:
            self._slots.release()

    def start_maintenance(self) -> None:
        if self.config.empty_namespace_ttl_days <= 0 or self._maintenance is not None:
            return
        self._maintenance = threading.Thread(target=self._maintenance_loop, name="mpt-relay-maintenance", daemon=True)
        self._maintenance.start()

    def _maintenance_loop(self) -> None:
        while True:
            time.sleep(3600)
            try:
                self.store.sweep_temporary_files()
                removed = self.store.reap_empty_namespaces()
                if removed:
                    self.log.info("reaped %d empty namespaces", removed)
            except Exception:  # noqa: BLE001 - maintenance must never kill the service
                self.log.exception("maintenance pass failed")

    def stop(self) -> None:
        self.app.cloud.close()
        try:
            self.shutdown()
        except Exception:  # noqa: BLE001 - already stopped
            pass
        try:
            self.server_close()
        except Exception:  # noqa: BLE001 - already closed
            pass

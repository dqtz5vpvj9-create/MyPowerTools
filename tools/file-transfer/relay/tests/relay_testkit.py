"""Shared harness: a real relay process on a real loopback socket.

Every test in this directory talks HTTP/1.1 over a TCP socket to a fully assembled
service (``Config`` -> ``Store`` -> ``RelayApp`` -> ``RelayServer``). Nothing is stubbed:
the WebDAV parser, the SQLite credential store, the quota accounting and the long-poll
wakeups are the production code paths.
"""

from __future__ import annotations

import http.client
import json
import logging
import socket
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple

RELAY_DIR = Path(__file__).resolve().parents[1]
if str(RELAY_DIR) not in sys.path:
    sys.path.insert(0, str(RELAY_DIR))

from mpt_relay.auth import encode_basic  # noqa: E402
from mpt_relay.config import Config  # noqa: E402
from mpt_relay.service import build  # noqa: E402
from mpt_relay.store import Store  # noqa: E402

BASE_PATH = "/mpt/relay"
DAV_ROOT = "/mpt/relay/dav/"


@dataclass
class HttpResult:
    status: int
    headers: Dict[str, str]
    body: bytes

    def json(self) -> object:
        return json.loads(self.body.decode("utf-8"))

    def text(self) -> str:
        return self.body.decode("utf-8", "replace")

    def header(self, name: str) -> Optional[str]:
        return self.headers.get(name.lower())


class _ListHandler(logging.Handler):
    def __init__(self) -> None:
        super().__init__()
        self.lines: List[str] = []
        self._lock = threading.Lock()

    def emit(self, record: logging.LogRecord) -> None:
        try:
            line = self.format(record)
        except Exception:  # pragma: no cover - never break a request because of logging
            return
        with self._lock:
            self.lines.append(line)

    def snapshot(self) -> List[str]:
        with self._lock:
            return list(self.lines)


def call(
    port: int,
    method: str,
    path: str,
    *,
    body: Optional[bytes] = None,
    basic: Optional[Tuple[str, str]] = None,
    headers: Optional[Dict[str, str]] = None,
    timeout: float = 15.0,
    host: str = "127.0.0.1",
) -> HttpResult:
    """One real HTTP/1.1 exchange against a relay on loopback."""
    connection = http.client.HTTPConnection(host, port, timeout=timeout)
    try:
        request_headers = dict(headers or {})
        if basic is not None:
            request_headers["Authorization"] = encode_basic(*basic)
        payload = body
        if payload is not None and "Content-Length" not in request_headers:
            request_headers["Content-Length"] = str(len(payload))
        connection.request(method, path, body=payload, headers=request_headers)
        response = connection.getresponse()
        data = response.read()
        return HttpResult(
            status=response.status,
            headers={key.lower(): value for key, value in response.getheaders()},
            body=data,
        )
    finally:
        connection.close()


def read_response(connection: socket.socket, timeout: float = 20.0) -> HttpResult:
    """Reads one complete HTTP/1.1 response from a raw socket.

    A single ``recv`` may return only the headers (or part of the body), so both are read
    until ``Content-Length`` is satisfied. Raw-socket tests use this instead of guessing.
    """
    connection.settimeout(timeout)
    buffer = b""
    while b"\r\n\r\n" not in buffer:
        chunk = connection.recv(4096)
        if not chunk:
            break
        buffer += chunk
    head, _separator, body = buffer.partition(b"\r\n\r\n")
    headers = {}
    for line in head.split(b"\r\n")[1:]:
        name, _, value = line.partition(b":")
        if name:
            headers[name.decode("ascii", "replace").strip().lower()] = value.decode("utf-8", "replace").strip()
    try:
        length = int(headers.get("content-length", "0") or 0)
    except ValueError:
        length = 0
    while len(body) < length:
        chunk = connection.recv(4096)
        if not chunk:
            break
        body += chunk
    try:
        status = int(head.split(b" ")[1])
    except (IndexError, ValueError):
        status = 0
    return HttpResult(status=status, headers=headers, body=body[:length] if length else body)


def free_port() -> int:
    """A loopback port nothing is listening on right now."""
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    return port


class RelayHarness:
    """In-process relay on 127.0.0.1 with an ephemeral port and a throwaway data dir."""

    def __init__(self, keep_dir: Optional[Path] = None, **options: object) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="mpt-relay-test-")) if keep_dir is None else keep_dir
        self.data_dir = self.root / "data"
        self.data_dir.mkdir(parents=True, exist_ok=True)
        environment = {
            "MPT_RELAY_DATA_DIR": str(self.data_dir),
            "MPT_RELAY_PORT": "0",
            "MPT_RELAY_KDF_ITERATIONS": "10000",
            "MPT_RELAY_REGISTER_PER_IP_PER_HOUR": "1000",
            "MPT_RELAY_REGISTER_GLOBAL_PER_HOUR": "10000",
            "MPT_RELAY_LOG_LEVEL": "debug",
            # Tests exercise the WebDAV-only client path by default. The shipped default is 0
            # (only POST registers); ProductionDefaultsTests asserts that value directly.
            "MPT_RELAY_DAV_AUTO_REGISTER": "1",
        }
        for key, value in options.items():
            environment[f"MPT_RELAY_{key.upper()}"] = str(value)
        self.config = Config.load([], environment)
        self.log = _ListHandler()
        self.log.setFormatter(logging.Formatter("%(levelname)s %(message)s"))
        logger = logging.getLogger(f"mpt_relay.test.{id(self)}")
        logger.handlers = [self.log]
        logger.setLevel(logging.DEBUG)
        logger.propagate = False
        self.store, self.app, self.server = build(self.config, logger)
        self.port = self.server.port
        self._thread = threading.Thread(target=self.server.serve_forever, name="relay-test", daemon=True)
        self._thread.start()

    # -- lifecycle -------------------------------------------------------------------

    def stop(self) -> None:
        self.server.stop()
        self.store.shutdown()
        self._thread.join(timeout=5)

    def __enter__(self) -> "RelayHarness":
        return self

    def __exit__(self, *_exc: object) -> None:
        self.stop()

    @property
    def authority(self) -> str:
        return f"127.0.0.1:{self.port}"

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def logs(self) -> List[str]:
        return self.log.snapshot()

    # -- requests --------------------------------------------------------------------

    def request(
        self,
        method: str,
        path: str,
        *,
        body: Optional[bytes] = None,
        basic: Optional[Tuple[str, str]] = None,
        headers: Optional[Dict[str, str]] = None,
        timeout: float = 15.0,
        connection: Optional[http.client.HTTPConnection] = None,
    ) -> HttpResult:
        if connection is None:
            return call(self.port, method, path, body=body, basic=basic, headers=headers, timeout=timeout)
        request_headers = dict(headers or {})
        if basic is not None:
            request_headers["Authorization"] = encode_basic(*basic)
        if body is not None and "Content-Length" not in request_headers:
            request_headers["Content-Length"] = str(len(body))
        connection.request(method, path, body=body, headers=request_headers)
        response = connection.getresponse()
        data = response.read()
        return HttpResult(
            status=response.status,
            headers={key.lower(): value for key, value in response.getheaders()},
            body=data,
        )

    def dav(self, method: str, relative: str, **kwargs: object) -> HttpResult:
        return self.request(method, DAV_ROOT + relative.lstrip("/"), **kwargs)

    def conversations(self, basic: Tuple[str, str], **kwargs: object) -> HttpResult:
        return self.request("POST", f"{BASE_PATH}/v1/conversations", basic=basic, **kwargs)

    def changes(self, basic: Tuple[str, str], since: Optional[int] = None, **kwargs: object) -> HttpResult:
        path = f"{BASE_PATH}/v1/changes" if since is None else f"{BASE_PATH}/v1/changes?since={since}"
        return self.request("GET", path, basic=basic, **kwargs)

    def health(self, **kwargs: object) -> HttpResult:
        return self.request("GET", f"{BASE_PATH}/health", **kwargs)

    # -- raw sockets -----------------------------------------------------------------

    def raw_connection(self, timeout: float = 10.0) -> socket.socket:
        connection = socket.create_connection(("127.0.0.1", self.port), timeout=timeout)
        connection.settimeout(timeout)
        return connection

    def partial_put(
        self,
        path: str,
        basic: Tuple[str, str],
        declared_length: int,
        sent: bytes,
        *,
        hold_open: bool = False,
        headers: Optional[Dict[str, str]] = None,
    ) -> socket.socket:
        """Starts a PUT with a declared length, sends only part of it, then aborts."""
        connection = self.raw_connection()
        extra = "".join(f"{name}: {value}\r\n" for name, value in (headers or {}).items())
        head = (
            f"PUT {path} HTTP/1.1\r\n"
            f"Host: {self.authority}\r\n"
            f"Authorization: {encode_basic(*basic)}\r\n"
            f"Content-Length: {declared_length}\r\n"
            f"Content-Type: application/octet-stream\r\n"
            f"{extra}"
            f"Connection: close\r\n"
            f"\r\n"
        ).encode("ascii")
        connection.sendall(head)
        if sent:
            connection.sendall(sent)
        if not hold_open:
            connection.close()
        return connection

    def wait_for(self, predicate, timeout: float = 5.0, interval: float = 0.02) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return True
            time.sleep(interval)
        return predicate()


def read_body_lines(body: bytes) -> Iterable[str]:
    return body.decode("utf-8", "replace").splitlines()


class LocalStore:
    """A store-only harness for direct unit assertions about disk state."""

    def __init__(self, **options: object) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="mpt-relay-store-"))
        environment = {
            "MPT_RELAY_DATA_DIR": str(self.root / "data"),
            "MPT_RELAY_KDF_ITERATIONS": "10000",
        }
        for key, value in options.items():
            environment[f"MPT_RELAY_{key.upper()}"] = str(value)
        self.store = Store(Config.load([], environment))

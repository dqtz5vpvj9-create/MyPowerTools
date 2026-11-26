#!/usr/bin/env python3
"""Post-deploy verification against a *running* relay, over the public HTTPS endpoint.

This is what the operator runs after nginx is wired up. It proves the real path
(TLS + nginx + relay + SQLite + disk), not a loopback unit test:

    python3 deploy/smoke.py --base https://proxy.lixinrui000.cn
    python3 deploy/smoke.py --base https://proxy.lixinrui000.cn --conversation self-ab12cd34 --key <64hex>

With ``--generate`` (the default when no credentials are given) it invents a throwaway
``smoke-<hex>`` namespace so a check can never disturb a real user's conversation. Note
that the registration rate limit applies per client address; reusing ``--conversation``/
``--key`` does not consume it.

Standard library only, Python >= 3.10.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from xml.etree import ElementTree

BASE_PATH = "/mpt/relay"
DAV_ROOT = "/mpt/relay/dav/"
TIMEOUT = 20.0
LONGPOLL_TIMEOUT = 40.0


class SmokeFailure(AssertionError):
    pass


class Client:
    def __init__(self, base: str, conversation_id: str, conversation_key: str) -> None:
        self.base = base.rstrip("/")
        self.conversation_id = conversation_id
        self.conversation_key = conversation_key
        self.authorization = "Basic " + base64.b64encode(
            f"{conversation_id}:{conversation_key}".encode("utf-8")
        ).decode("ascii")

    def call(
        self,
        method: str,
        path: str,
        *,
        body: bytes | None = None,
        authenticated: bool = True,
        timeout: float = TIMEOUT,
        extra_headers: dict | None = None,
    ) -> tuple:
        url = self.base + path
        headers = dict(extra_headers or {})
        if authenticated:
            headers["Authorization"] = self.authorization
        if body is not None:
            headers["Content-Length"] = str(len(body))
        request = urllib.request.Request(url, data=body, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.status, dict(response.headers), response.read()
        except urllib.error.HTTPError as error:
            return error.code, dict(error.headers or {}), error.read()

    def dav(self, method: str, relative: str, **kwargs) -> tuple:
        return self.call(method, DAV_ROOT + relative.lstrip("/"), **kwargs)


def expect(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeFailure(message)


def check_health(client: Client) -> dict:
    status, _headers, body = client.call("GET", f"{BASE_PATH}/health", authenticated=False)
    expect(status == 200, f"health returned HTTP {status}")
    payload = json.loads(body.decode("utf-8"))
    expect(payload.get("ok") is True, f"health payload is not ok: {payload}")
    return payload


def check_registration(client: Client) -> None:
    status, _headers, body = client.call("POST", f"{BASE_PATH}/v1/conversations")
    expect(status in (200, 201), f"registration returned HTTP {status}: {body[:200]!r}")
    payload = json.loads(body.decode("utf-8"))
    expect(payload.get("conversationId") == client.conversation_id, f"unexpected registration payload: {payload}")
    expect(payload.get("davPath") == DAV_ROOT, f"registration davPath is {payload.get('davPath')!r}")

    wrong = Client(client.base, client.conversation_id, hashlib.sha256(b"smoke-wrong-key").hexdigest())
    status, _headers, _body = wrong.call("POST", f"{BASE_PATH}/v1/conversations")
    expect(status == 401, f"a wrong key must be rejected with 401, got {status}")


def check_dav_round_trip(client: Client, payload: bytes) -> str:
    item_id = hashlib.sha256(f"smoke-{time.time()}".encode()).hexdigest()[:32]
    prefix = f"assistant/{client.conversation_id}/{item_id}/"

    expect(client.dav("PROPFIND", "", extra_headers={"Depth": "0"})[0] == 207, "PROPFIND on the DAV root failed")
    expect(client.dav("MKCOL", "assistant/")[0] in (201, 405), "MKCOL assistant/ failed")
    expect(client.dav("MKCOL", f"assistant/{client.conversation_id}/")[0] in (201, 405), "MKCOL conversation failed")
    expect(client.dav("MKCOL", prefix)[0] == 201, "MKCOL item failed")

    status, _headers, _body = client.dav("PUT", prefix + "payload", body=payload)
    expect(status == 201, f"payload PUT returned HTTP {status}")

    manifest = {
        "version": 1,
        "id": item_id,
        "kind": "file",
        "text": None,
        "name": "smoke.bin",
        "size": len(payload),
        "createdAt": "2026-01-01T00:00:00+00:00",
        "senderDeviceId": "smoke-device",
        "senderName": "smoke",
        "targetDeviceId": None,
    }
    status, _headers, _body = client.dav("PUT", prefix + "manifest.json", body=json.dumps(manifest).encode("utf-8"))
    expect(status == 201, f"manifest PUT returned HTTP {status}")

    status, _headers, body = client.dav("PROPFIND", f"assistant/{client.conversation_id}/", extra_headers={"Depth": "1"})
    expect(status == 207, f"PROPFIND conversation returned HTTP {status}")
    hrefs = [node.text or "" for node in ElementTree.fromstring(body).iter("{DAV:}href")]
    expect(any(href.startswith(DAV_ROOT) for href in hrefs), f"hrefs lost the {DAV_ROOT} prefix: {hrefs}")
    expect(any(href.rstrip("/").endswith(item_id) for href in hrefs), f"the published item is not listed: {hrefs}")

    status, _headers, downloaded = client.dav("GET", prefix + "payload")
    expect(status == 200, f"payload GET returned HTTP {status}")
    expect(downloaded == payload, "downloaded payload differs from the uploaded bytes")

    status, _headers, body = client.dav("GET", prefix + "manifest.json")
    expect(status == 200 and json.loads(body.decode("utf-8"))["id"] == item_id, "manifest GET failed")

    expect(client.dav("MKCOL", prefix + "receipts/")[0] == 201, "MKCOL receipts/ failed")
    receipt = {
        "itemId": item_id,
        "deviceId": "smoke-device",
        "deviceName": "smoke",
        "savedAt": "2026-01-01T00:00:01+00:00",
        "bytes": len(payload),
    }
    status, _headers, _body = client.dav(
        "PUT", prefix + "receipts/smoke-device.json", body=json.dumps(receipt).encode("utf-8")
    )
    expect(status == 201, f"receipt PUT returned HTTP {status}")
    status, _headers, body = client.dav("PROPFIND", prefix + "receipts/", extra_headers={"Depth": "1"})
    expect(status == 207 and b"smoke-device.json" in body, "the receipt is not listed")

    expect(client.dav("GET", prefix + "missing.json")[0] == 404, "a missing file must answer 404")
    expect(client.call("GET", DAV_ROOT + "assistant/", authenticated=False)[0] == 401, "unauthenticated DAV must answer 401")
    return item_id


def check_namespace_isolation(client: Client) -> None:
    other = Client(
        client.base,
        "smoke-other-" + hashlib.sha256(os.urandom(8)).hexdigest()[:8],
        hashlib.sha256(os.urandom(16)).hexdigest(),
    )
    # With MPT_RELAY_DAV_AUTO_REGISTER=0 (the shipped default) an unknown credential is refused
    # outright; with 1 it would first create the probing namespace. Both outcomes are a pass.
    status, _headers, _body = other.dav("PROPFIND", f"assistant/{client.conversation_id}/", extra_headers={"Depth": "1"})
    # 401/403: an unknown or foreign credential may not address this namespace.
    # 429: registration throttling refused to create the probing namespace at all.
    expect(status in (401, 403, 429), f"a foreign namespace probe must be refused, got {status}")


def check_changes(client: Client, expected_revision: int) -> None:
    status, _headers, body = client.call("GET", f"{BASE_PATH}/v1/changes")
    expect(status == 200, f"changes returned HTTP {status}")
    revision = json.loads(body.decode("utf-8"))["revision"]
    expect(revision >= expected_revision, f"revision {revision} is behind the published writes ({expected_revision})")

    started = time.monotonic()
    status, _headers, _body = client.call("GET", f"{BASE_PATH}/v1/changes?since={revision + 1000}")
    expect(status == 200 and time.monotonic() - started < 5, "a future revision must be answered immediately")

    woken: dict = {}

    def poll() -> None:
        woken["result"] = client.call("GET", f"{BASE_PATH}/v1/changes?since={revision}", timeout=LONGPOLL_TIMEOUT)

    worker = threading.Thread(target=poll, daemon=True)
    worker.start()
    time.sleep(0.5)
    item_id = hashlib.sha256(os.urandom(16)).hexdigest()[:32]
    prefix = f"assistant/{client.conversation_id}/{item_id}/"
    client.dav("MKCOL", prefix)
    client.dav("PUT", prefix + "manifest.json", body=b"{}")
    worker.join(timeout=LONGPOLL_TIMEOUT)
    expect(not worker.is_alive(), "the long poll did not return after a manifest write")
    status, _headers, body = woken["result"]
    expect(status == 200, f"long poll returned HTTP {status}")
    expect(json.loads(body.decode("utf-8"))["revision"] > revision, "the long poll did not report the new revision")


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description="Verify a deployed MyPowerTools file relay.")
    parser.add_argument("--base", default="https://proxy.lixinrui000.cn", help="public base URL")
    parser.add_argument("--conversation", help="reuse an existing conversation id")
    parser.add_argument("--key", help="reuse an existing 64 hex conversation key")
    parser.add_argument("--generate", action="store_true", help="force a throwaway namespace")
    arguments = parser.parse_args(argv)

    if arguments.conversation and arguments.key and not arguments.generate:
        conversation_id, conversation_key = arguments.conversation, arguments.key
    else:
        conversation_id = arguments.conversation or f"smoke-{os.urandom(4).hex()}"
        conversation_key = arguments.key or hashlib.sha256(os.urandom(32)).hexdigest()
        print(f"# using throwaway namespace {conversation_id} (key is not printed)")

    client = Client(arguments.base, conversation_id, conversation_key)
    steps = [
        ("health", lambda: check_health(client) and None),
        ("register", lambda: check_registration(client)),
        ("dav round trip", lambda: check_dav_round_trip(client, os.urandom(4096))),
        ("namespace isolation", lambda: check_namespace_isolation(client)),
        ("changes long poll", lambda: check_changes(client, 1)),
    ]
    failures = 0
    for name, step in steps:
        started = time.monotonic()
        try:
            step()
        except SmokeFailure as error:
            failures += 1
            print(f"FAIL {name}: {error}")
        except Exception as error:  # noqa: BLE001 - the operator wants the real cause
            failures += 1
            print(f"FAIL {name}: {type(error).__name__}: {error}")
        else:
            print(f"ok   {name} ({time.monotonic() - started:.2f}s)")

    print()
    if failures:
        print(f"{failures} check(s) failed against {arguments.base}")
        return 1
    print(f"all checks passed against {arguments.base}")
    print(f"note: namespace {conversation_id} now holds smoke items; it is a throwaway identity.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

"""Routing, authentication and the non-WebDAV API.

Routes (behind the nginx prefix, default ``/mpt/relay``)::

    GET    /health              short unauthenticated JSON
    POST   /v1/conversations    first-use namespace registration (empty body, Basic auth)
    GET    /v1/changes          authenticated long poll, ``?since=<revision>``
    *      /dav/...             WebDAV over the caller's private conversation root

Authentication is HTTP Basic with ``conversationId`` as the user name and the 64 hex
conversation key as the password. The key of an existing namespace can never be replaced.
"""

from __future__ import annotations

import json
import logging
import re
import threading
import time
from dataclasses import dataclass
from typing import Optional

from . import __version__
from .auth import CredentialFormatError, REALM, parse_basic, validate
from .config import Config
from . import dav
from .limits import FailureWindow, RateLimiter
from .logutil import mask_identity, mask_path
from .messages import Request, Response, empty, json_response, text_response
from .inbox import ITEM_ID_PATTERN, InboxApi, InboxError
from .cloud_stream import CloudError, CloudStreams, error_response as cloud_error
from .store import CredentialMismatch, DepositKeyConflict, QuotaExceeded, Store, TooLarge

UNAUTHORIZED_HEADERS = (("WWW-Authenticate", f'Basic realm="{REALM}", charset="UTF-8"'),)
#: An owner key and a deposit key are both exactly this shape.
KEY_PATTERN = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class AuthResult:
    conversation_id: str
    created: bool


class RelayApp:
    """The whole service as a pure ``Request -> Response`` function."""

    def __init__(self, config: Config, store: Store, logger: Optional[logging.Logger] = None) -> None:
        self.config = config
        self.store = store
        self.log = logger or logging.getLogger("mpt_relay")
        self._register_per_ip = RateLimiter(
            config.register_per_ip_per_hour, 3600, max_keys=config.max_tracked_addresses
        ) if config.register_per_ip_per_hour > 0 else None
        self._register_global = RateLimiter(config.register_global_per_hour, 3600, max_keys=16) if config.register_global_per_hour > 0 else None
        self._auth_failures = FailureWindow(
            config.auth_failures_per_ip, config.auth_failure_window_seconds, max_keys=config.max_tracked_addresses
        )
        self._not_ready_per_ip = RateLimiter(config.not_ready_per_ip_per_minute, 60, max_keys=config.max_tracked_addresses)
        self._auth_slots = threading.BoundedSemaphore(config.max_concurrent_auth)
        self.inbox = InboxApi(config, store, self.log)
        self.cloud = CloudStreams(config, store)
        self._started_at = time.time()

    # -- routing ---------------------------------------------------------------------

    def handle(self, request: Request) -> Response:
        base = self.config.normalized_base_path
        path = request.url_path
        if base:
            if path == base:
                rest = "/"
            elif path.startswith(base + "/"):
                rest = path[len(base) :]
            else:
                return text_response(404, "not found\n")
        else:
            rest = path

        if rest == "/health":
            if request.method != "GET" and request.method != "HEAD":
                return self._method_not_allowed("GET, HEAD")
            return self._health(request)

        if rest == "/v1/conversations":
            if request.method != "POST":
                return self._method_not_allowed("POST")
            return self._register(request)

        if rest == "/v1/changes":
            if request.method != "GET":
                return self._method_not_allowed("GET")
            return self._changes(request)

        if rest == "/v1/inboxes":
            if request.method != "POST":
                return self._method_not_allowed("POST")
            return self._register_inbox(request)

        if rest == "/v1/inboxes/items" or rest.startswith("/v1/inboxes/items/"):
            return self._inbox_route(request, rest)

        if rest == "/v1/cloud/requests" or rest.startswith("/v1/cloud/requests/"):
            if not self.config.cloud_payload_stream:
                return text_response(404, "not found\n")
            auth = self._authenticate(request, allow_register=False)
            if isinstance(auth, Response):
                return auth
            try:
                if rest == "/v1/cloud/requests" and request.method == "GET":
                    return self.cloud.poll(auth.conversation_id, request)
                match = re.fullmatch(r"/v1/cloud/requests/([0-9a-f]{32})/body", rest)
                if match and request.method == "PUT":
                    return self.cloud.supply(auth.conversation_id, match[1], request)
                return self._method_not_allowed("GET, PUT")
            except CloudError as error:
                return cloud_error(error.status, error.code)

        if rest == "/dav" or rest.startswith("/dav/"):
            auth = self._authenticate(request, allow_register=self.config.dav_auto_register)
            if isinstance(auth, Response):
                return auth
            try:
                return dav.handle(self.store, self.config, auth.conversation_id, request, self.log, cloud=self.cloud)
            except CloudError as error:
                return cloud_error(error.status, error.code)
            except dav.DavError as error:
                return text_response(error.status, error.message + "\n")
            except TooLarge as error:
                # A quota failure must never surface as a generic 500, whatever path raised it.
                return text_response(413, str(error) + "\n")
            except QuotaExceeded as error:
                return text_response(507, str(error) + "\n")
            except CredentialMismatch:
                return empty(401, *UNAUTHORIZED_HEADERS)

        return text_response(404, "not found\n")

    # -- API -------------------------------------------------------------------------

    def _health(self, request: Request) -> Response:
        payload = {
            "ok": True,
            "service": "mpt-relay",
            "version": __version__,
            "uptimeSeconds": int(time.time() - self._started_at),
            "longPollSeconds": self.config.longpoll_max_seconds,
        }
        capabilities = {}
        if self.config.tail_payload_proxy:
            # Advertised only by a relay that actually serves the shared payload proxy; absent
            # everywhere else, including the Tail relay. This is the exact shape the client reads
            # (``SharedLocatorClient.SupportsPayloadLocatorAsync``).
            capabilities["sharedPayloadLocator"] = 1
        if self.config.cloud_payload_stream:
            capabilities["cloudPayloadStream"] = 1
        if capabilities:
            payload["capabilities"] = capabilities
        return json_response(200, payload)

    def _register(self, request: Request) -> Response:
        # The body must be empty. A declared body is refused without reading it; an undeclared
        # (chunked) one is read up to a small bound and refused if it carries anything.
        if request.body.declared_length not in (None, 0):
            request.body.discard()
            return self._body_not_allowed()
        if request.body.read_all(8192).strip():
            return self._body_not_allowed()
        # Registration is the one route that always creates a namespace; every other route only
        # auto-registers when the operator allows first-use namespace creation outside it.
        auth = self._authenticate(request, allow_register=True)
        if isinstance(auth, Response):
            return auth
        revision = self.store.revision(auth.conversation_id)
        return json_response(
            201 if auth.created else 200,
            {
                "conversationId": auth.conversation_id,
                "created": auth.created,
                "revision": revision,
                "davPath": self.config.dav_root,
                "changesPath": f"{self.config.normalized_base_path}/v1/changes",
            },
        )

    @staticmethod
    def _body_not_allowed() -> Response:
        response = json_response(400, {"error": "body_not_allowed", "detail": "注册请求必须使用空 body。"})
        response.close = True
        return response

    def _changes(self, request: Request) -> Response:
        auth = self._authenticate(request, allow_register=self.config.dav_auto_register)
        if isinstance(auth, Response):
            return auth
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

        current = self.store.revision(auth.conversation_id)
        if since is None or since > current:
            # No cursor (or a cursor ahead of this server) is answered immediately.
            return json_response(200, {"revision": current})
        if since == current:
            waited = self.store.wait_for_revision(
                auth.conversation_id,
                since,
                float(self.config.longpoll_max_seconds),
                abort=request.peer_closed,
            )
            if waited is None:
                response = empty(204)
                response.close = True
                return response
            current = waited
        return json_response(200, {"revision": current})

    def _method_not_allowed(self, allow: str) -> Response:
        return text_response(405, "method not allowed\n", "text/plain; charset=utf-8", ("Allow", allow))

    # -- pairing inboxes --------------------------------------------------------------

    def _register_inbox(self, request: Request) -> Response:
        """``POST /v1/inboxes``: Basic ``inboxId:ownerKey`` plus ``{"depositKey": "..."}``."""
        try:
            presented = parse_basic(request.header("authorization"))
        except CredentialFormatError:
            return self._unauthorized(request, "凭据格式非法。", punish=True)
        if presented is None:
            return empty(401, *UNAUTHORIZED_HEADERS)
        try:
            credentials = validate(presented)
        except CredentialFormatError as error:
            return self._unauthorized(request, str(error), punish=True)
        inbox_id = credentials.conversation_id
        owner_key = credentials.conversation_key

        raw = request.body.read_all(1024)
        try:
            payload = json.loads(raw.decode("utf-8")) if raw.strip() else {}
        except (ValueError, UnicodeDecodeError):
            return json_response(400, {"error": "invalid_body", "detail": "注册请求必须是 JSON 对象。"})
        if not isinstance(payload, dict):
            return json_response(400, {"error": "invalid_body", "detail": "注册请求必须是 JSON 对象。"})
        deposit_key = str(payload.get("depositKey", "")).strip().lower()
        if not KEY_PATTERN.match(deposit_key):
            return json_response(400, {"error": "invalid_deposit_key", "detail": "depositKey 必须是 64 位十六进制。"})
        if self.store.conversation_exists(inbox_id):
            # The identifier already belongs to a conversation: an inbox may not shadow it.
            return self._unauthorized(request, "该标识已用于会话。", punish=False, punish_reason=None)

        known = self.store.inbox_exists(inbox_id)
        if known:
            # Ownership is decided before anything else: a deposit credential (or any other
            # key) can never take over an inbox, whatever the body claims.
            check = self.store.verify_inbox(inbox_id, owner_key)
            if check.role != "owner":
                return self._unauthorized(request, "收件箱 owner 密钥不一致。", punish=True)
            if deposit_key == owner_key:
                return json_response(
                    400, {"error": "keys_must_differ", "detail": "depositKey 不能与 ownerKey 相同。"}
                )
            try:
                created, revision = self.store.register_inbox(inbox_id, owner_key, deposit_key)
            except DepositKeyConflict as error:
                return json_response(409, {"error": "deposit_key_locked", "detail": str(error)})
            except CredentialMismatch:  # pragma: no cover - verify_inbox already proved ownership
                return self._unauthorized(request, "收件箱 owner 密钥不一致。", punish=True)
            return self._inbox_registration_response(request, inbox_id, False, created, revision)

        for limiter, key in ((self._register_per_ip, request.remote_ip), (self._register_global, "*")):
            if limiter is not None:
                allowed, retry = limiter.allow(key)
                if not allowed:
                    return self._too_many(retry, "注册过于频繁。")
        if deposit_key == owner_key:
            # A fresh inbox needs two independent keys, so the pair code never doubles as owner.
            return json_response(400, {"error": "keys_must_differ", "detail": "depositKey 不能与 ownerKey 相同。"})
        if self.store.inbox_count() >= self.config.max_inboxes:
            return self._too_many(3600.0, "服务收件箱数达到上限。")
        try:
            created, revision = self.store.register_inbox(inbox_id, owner_key, deposit_key)
        except DepositKeyConflict as error:  # pragma: no cover - lost a registration race
            return json_response(409, {"error": "deposit_key_locked", "detail": str(error)})
        except CredentialMismatch:  # pragma: no cover - lost a registration race
            return self._unauthorized(request, "收件箱 owner 密钥不一致。", punish=True)
        return self._inbox_registration_response(request, inbox_id, True, created, revision)

    def _inbox_registration_response(
        self, request: Request, inbox_id: str, newly_seen: bool, created: bool, revision: int
    ) -> Response:
        if created and newly_seen:
            self.log.info(
                "inbox registered id=%s ip=%s",
                mask_identity(inbox_id, self.config.mask_log_ids),
                request.remote_ip,
            )
        return json_response(
            201 if created else 200,
            {
                "inboxId": inbox_id,
                "created": created,
                "revision": revision,
                "itemsPath": f"{self.config.normalized_base_path}/v1/inboxes/items",
            },
        )

    def _inbox_route(self, request: Request, rest: str) -> Response:
        """Routes ``/v1/inboxes/items...`` and enforces the owner/deposit split."""
        suffix = rest[len("/v1/inboxes/items") :].strip("/")
        parts = [part for part in suffix.split("/") if part]
        method = request.method

        if not parts:
            route = "list"
        elif len(parts) == 1 and method in ("GET", "HEAD"):
            route = "payload"
        elif len(parts) == 1 and method == "PUT":
            route = "deposit"
        elif len(parts) == 1 and method == "DELETE":
            route = "delete"
        elif len(parts) == 2 and parts[1] == "receipt" and method == "POST":
            route = "receipt_write"
        elif len(parts) == 2 and parts[1] == "receipt" and method in ("GET", "HEAD"):
            route = "receipt_read"
        else:
            return self._method_not_allowed("GET, HEAD, PUT, DELETE, POST")

        item_id = parts[0] if parts else ""
        if route != "list" and not ITEM_ID_PATTERN.match(item_id):
            return json_response(400, {"error": "invalid_item_id", "detail": "条目 id 必须是 32 位小写十六进制。"})

        try:
            presented = parse_basic(request.header("authorization"))
        except CredentialFormatError:
            return self._unauthorized(request, "凭据格式非法。", punish=True)
        if presented is None:
            return empty(401, *UNAUTHORIZED_HEADERS)
        try:
            credentials = validate(presented)
        except CredentialFormatError as error:
            return self._unauthorized(request, str(error), punish=True)

        ip = request.remote_ip
        if self.config.auth_failures_per_ip > 0:
            blocked = self._auth_failures.blocked_for(ip)
            if blocked > 0:
                return self._too_many(blocked, "认证失败次数过多。")
        acquired = self._auth_slots.acquire(timeout=2.0)
        if not acquired:
            return json_response(503, {"error": "busy"}, ("Retry-After", "2"))
        try:
            check = self.store.verify_inbox(credentials.conversation_id, credentials.conversation_key)
        finally:
            self._auth_slots.release()

        if not check.known:
            if route in ("deposit", "receipt_read"):
                # The receiver has not registered this inbox yet. That is a *retryable* state:
                # the sender already holds a pairing code produced offline.
                allowed, retry = self._not_ready_per_ip.allow(ip)
                if not allowed:
                    return self._too_many(retry, "请求过于频繁。")
                return json_response(
                    503,
                    {"error": "inbox_not_ready", "detail": "收件端尚未注册该收件箱，请稍后重试。"},
                    ("Retry-After", "5"),
                )
            return self._unauthorized(request, "收件箱尚未注册。", punish=False, punish_reason=None)
        if check.role is None:
            return self._unauthorized(request, "收件箱密钥不一致。", punish=True)
        self._auth_failures.reset(ip)

        if route in ("list", "payload", "delete", "receipt_write") and check.role != "owner":
            # A deposit credential can never enumerate, read a payload, or forge a receipt.
            return json_response(403, {"error": "owner_required", "detail": "该操作需要 owner 权限。"})
        if route == "deposit" and check.role != "deposit":
            # The owner delivers through its own conversation, not through a pairing inbox.
            return json_response(403, {"error": "deposit_required", "detail": "该操作需要投递密钥。"})

        inbox_id = credentials.conversation_id
        try:
            if route == "list":
                return self.inbox.list_items(request, inbox_id)
            if route == "payload":
                return self.inbox.get_payload(request, inbox_id, item_id)
            if route == "delete":
                return self.inbox.delete_item(request, inbox_id, item_id)
            if route == "receipt_write":
                return self.inbox.put_receipt(request, inbox_id, item_id)
            if route == "receipt_read":
                return self.inbox.get_receipt(inbox_id, item_id)
            return self.inbox.put_item(request, inbox_id, item_id)
        except InboxError as error:
            return json_response(error.status, {"error": error.code, "detail": error.message})
        except TooLarge as error:
            return json_response(413, {"error": "quota_exceeded", "detail": str(error)})
        except QuotaExceeded as error:
            return json_response(507, {"error": "quota_exceeded", "detail": str(error)})

    # -- authentication ---------------------------------------------------------------

    def _authenticate(self, request: Request, allow_register: bool) -> "AuthResult | Response":
        """Returns the authenticated conversation, or the response that must be sent."""
        try:
            credentials = parse_basic(request.header("authorization"))
        except CredentialFormatError:
            return self._unauthorized(request, "凭据格式非法。", punish=True)
        if credentials is None:
            return empty(401, *UNAUTHORIZED_HEADERS)
        try:
            credentials = validate(credentials)
        except CredentialFormatError as error:
            return self._unauthorized(request, str(error), punish=True)

        ip = request.remote_ip
        # 0 disables the failure window entirely (shared NAT, local probing, tests).
        if self.config.auth_failures_per_ip > 0:
            blocked = self._auth_failures.blocked_for(ip)
            if blocked > 0:
                return self._too_many(blocked, "认证失败次数过多。")

        acquired = self._auth_slots.acquire(timeout=2.0)
        if not acquired:
            return json_response(503, {"error": "busy"}, ("Retry-After", "2"))
        try:
            known = self.store.verify(credentials.conversation_id, credentials.conversation_key)
        finally:
            self._auth_slots.release()

        if known is True:
            self._auth_failures.reset(ip)
            return AuthResult(credentials.conversation_id, False)
        if known is False:
            return self._unauthorized(request, "会话密钥不一致。", punish=True)

        # Unknown namespace: on the explicit registration route the credential pair registers the
        # namespace; elsewhere it does so only when first-use auto-registration is enabled.
        if not allow_register:
            return self._unauthorized(request, "会话尚未注册。", punish=False, punish_reason=None)
        return self._register_new_namespace(request, credentials.conversation_id, credentials.conversation_key)

    def _register_new_namespace(self, request: Request, conversation_id: str, conversation_key: str) -> "AuthResult | Response":
        if self.store.conversation_exists(conversation_id):  # lost a race, treat as auth failure
            return self._unauthorized(request, "会话密钥不一致。", punish=True)
        if self.store.inbox_exists(conversation_id):
            # The identifier already belongs to a pairing inbox: a conversation may not shadow it.
            return self._unauthorized(request, "该标识已用于收件箱。", punish=False, punish_reason=None)
        if self.store.conversation_count() >= self.config.max_conversations:
            return self._too_many(3600.0, "服务会话数达到上限。")
        for limiter, key in ((self._register_per_ip, request.remote_ip), (self._register_global, "*")):
            if limiter is not None:
                allowed, retry = limiter.allow(key)
                if not allowed:
                    return self._too_many(retry, "注册过于频繁。")
        try:
            created, _revision = self.store.register(conversation_id, conversation_key)
        except CredentialMismatch:
            return self._unauthorized(request, "会话密钥不一致。", punish=True)
        if created:
            self.log.info(
                "conversation registered id=%s ip=%s",
                mask_identity(conversation_id, self.config.mask_log_ids),
                request.remote_ip,
            )
        return AuthResult(conversation_id, created)

    def _unauthorized(
        self,
        request: Request,
        detail: str,
        punish: bool,
        punish_reason: Optional[str] = None,
    ) -> Response:
        if punish:
            if self.config.auth_failures_per_ip > 0:
                self._auth_failures.record(request.remote_ip)
            self.log.warning(
                "auth rejected path=%s ip=%s reason=%s",
                mask_path(request.url_path, self.config.mask_log_ids),
                request.remote_ip,
                punish_reason or detail,
            )
        return json_response(401, {"error": "unauthorized", "detail": detail}, *UNAUTHORIZED_HEADERS)

    def _too_many(self, retry_after: float, detail: str) -> Response:
        return json_response(
            429,
            {"error": "rate_limited", "detail": detail},
            ("Retry-After", str(max(1, int(retry_after + 0.999)))),
        )

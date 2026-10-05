"""Registration, authentication, rate limiting and log hygiene."""

from __future__ import annotations

import base64
import hashlib
import json
import unittest
import time

from relay_testkit import BASE_PATH, DAV_ROOT, RelayHarness


def key_for(seed: str) -> str:
    """A realistic conversation key: 32 random bytes as 64 lower-case hex characters."""
    return hashlib.sha256(seed.encode("utf-8")).hexdigest()


CONVERSATION = "self-1a2b3c4d"
CONVERSATION_KEY = key_for("conversation-a")


class HealthTests(unittest.TestCase):
    def test_health_is_public_short_json(self) -> None:
        with RelayHarness() as relay:
            response = relay.health()
            self.assertEqual(200, response.status)
            payload = response.json()
            self.assertTrue(payload["ok"])
            self.assertEqual("mpt-relay", payload["service"])
            self.assertLessEqual(len(response.body), 256)
            self.assertEqual("no-store", response.header("cache-control"))

    def test_health_rejects_other_methods(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(405, relay.request("POST", f"{BASE_PATH}/health").status)

    def test_unknown_prefix_is_404(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(404, relay.request("GET", "/mpt/other/health").status)
            self.assertEqual(404, relay.request("GET", "/").status)
            self.assertEqual(404, relay.request("GET", f"{BASE_PATH}/v1/unknown").status)


class RegistrationTests(unittest.TestCase):
    def test_first_registration_then_idempotent(self) -> None:
        with RelayHarness() as relay:
            created = relay.conversations((CONVERSATION, CONVERSATION_KEY))
            self.assertEqual(201, created.status)
            payload = created.json()
            self.assertEqual(CONVERSATION, payload["conversationId"])
            self.assertTrue(payload["created"])
            self.assertEqual(0, payload["revision"])
            self.assertEqual(DAV_ROOT, payload["davPath"])
            self.assertEqual(f"{BASE_PATH}/v1/changes", payload["changesPath"])

            again = relay.conversations((CONVERSATION, CONVERSATION_KEY))
            self.assertEqual(200, again.status)
            self.assertFalse(again.json()["created"])

    def test_wrong_key_is_rejected_and_key_is_immutable(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(201, relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            wrong = key_for("other-key")
            self.assertEqual(401, relay.conversations((CONVERSATION, wrong)).status)
            # The original key still works, the rejected one never became a second credential.
            self.assertEqual(200, relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            self.assertEqual(401, relay.dav("PROPFIND", "", basic=(CONVERSATION, wrong), headers={"Depth": "0"}).status)
            self.assertEqual(207, relay.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"}).status)

    def test_malformed_credentials_are_401(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(401, relay.conversations((CONVERSATION, "short")).status)
            self.assertEqual(401, relay.conversations((CONVERSATION, "z" * 64)).status)
            self.assertEqual(401, relay.conversations((CONVERSATION, CONVERSATION_KEY.upper() + "0")).status)
            self.assertEqual(401, relay.conversations(("has space", CONVERSATION_KEY)).status)
            self.assertEqual(401, relay.conversations(("a" * 65, CONVERSATION_KEY)).status)
            self.assertEqual(401, relay.conversations(("../etc", CONVERSATION_KEY)).status)
            self.assertEqual(401, relay.request("POST", f"{BASE_PATH}/v1/conversations").status)
            unauthorized = relay.request("POST", f"{BASE_PATH}/v1/conversations")
            self.assertIn("basic", unauthorized.header("www-authenticate").lower())

    def test_uppercase_hex_key_matches_lowercase(self) -> None:
        with RelayHarness() as relay:
            self.assertEqual(201, relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            self.assertEqual(200, relay.conversations((CONVERSATION, CONVERSATION_KEY.upper())).status)

    def test_registration_body_must_be_empty(self) -> None:
        with RelayHarness() as relay:
            response = relay.conversations((CONVERSATION, CONVERSATION_KEY), body=b'{"key":"x"}')
            self.assertEqual(400, response.status)
            self.assertEqual("body_not_allowed", response.json()["error"])

            # The same rule for a body that arrives chunked (no Content-Length to inspect).
            connection = relay.raw_connection()
            try:
                token = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
                connection.sendall(
                    (
                        f"POST {BASE_PATH}/v1/conversations HTTP/1.1\r\n"
                        f"Host: 127.0.0.1:{relay.port}\r\n"
                        f"Authorization: Basic {token}\r\n"
                        f"Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                        "2\r\n{}\r\n0\r\n\r\n"
                    ).encode("ascii")
                )
                status_line = connection.recv(4096).split(b"\r\n", 1)[0]
            finally:
                connection.close()
            self.assertIn(b"400", status_line)
            self.assertFalse(relay.store.conversation_exists(CONVERSATION))

    def test_dav_auto_register_is_opt_in_with_a_strict_shipped_default(self) -> None:
        with RelayHarness() as relay:
            # Opt-in mode keeps a WebDAV-only client (the existing OpenListClient) working.
            response = relay.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"})
            self.assertEqual(207, response.status)
            self.assertTrue(relay.store.conversation_exists(CONVERSATION))

        # The shipped first-up posture: an unauthenticated DAV request cannot mint a namespace;
        # the client must register explicitly (PublicRelayClient.RegisterAsync does).
        with RelayHarness(dav_auto_register=0) as strict:
            response = strict.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"})
            self.assertEqual(401, response.status)
            self.assertFalse(strict.store.conversation_exists(CONVERSATION))
            self.assertEqual(201, strict.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            self.assertEqual(207, strict.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"}).status)
            self.assertEqual(401, strict.dav("PROPFIND", "", basic=("self-unknown1", key_for("unknown")), headers={"Depth": "0"}).status)


class ProductionDefaultsTests(unittest.TestCase):
    """The values `python3 -m mpt_relay` uses with no configuration at all."""

    def test_shipped_defaults(self) -> None:
        from mpt_relay.config import Config

        config = Config.load([], {})
        self.assertEqual("127.0.0.1", config.host)
        self.assertEqual(18765, config.port)
        self.assertEqual("/mpt/relay", config.normalized_base_path)
        self.assertEqual("/mpt/relay/dav/", config.dav_root)
        self.assertEqual(1 << 30, config.per_conversation_bytes)
        self.assertEqual(8 << 30, config.global_bytes)
        self.assertEqual(512 << 20, config.max_file_bytes)
        self.assertEqual(25, config.longpoll_max_seconds)
        self.assertEqual(0, config.empty_namespace_ttl_days)
        self.assertFalse(config.dav_auto_register, "first-up must not mint namespaces from DAV")
        self.assertGreaterEqual(config.body_budget_seconds, 60)
        self.assertGreaterEqual(config.max_tracked_addresses, 256)

    def test_default_creation_has_no_shared_exit_hourly_quota(self) -> None:
        with RelayHarness() as relay:
            for index in range(8):
                self.assertEqual(201, relay.conversations(
                    (f"self-{index:08x}", key_for(f"shared-exit-{index}"))).status)

    def test_global_creation_limit_is_explicit_opt_in(self) -> None:
        with RelayHarness(register_global_per_hour=1) as relay:
            self.assertEqual(201, relay.conversations(("self-aaaa1111", key_for("a"))).status)
            self.assertEqual(429, relay.conversations(("self-bbbb2222", key_for("b"))).status)

    def test_rate_limit_blocks_creation_not_existing_sessions(self) -> None:
        with RelayHarness(register_per_ip_per_hour=2) as relay:
            self.assertEqual(201, relay.conversations(("self-aaaa1111", key_for("a"))).status)
            self.assertEqual(201, relay.conversations(("self-bbbb2222", key_for("b"))).status)
            blocked = relay.conversations(("self-cccc3333", key_for("c")))
            self.assertEqual(429, blocked.status)
            self.assertIsNotNone(blocked.header("retry-after"))
            # An already-registered conversation is authenticated, not created: still fine.
            self.assertEqual(207, relay.dav("PROPFIND", "", basic=("self-aaaa1111", key_for("a")), headers={"Depth": "0"}).status)

    def test_repeated_auth_failures_are_rate_limited(self) -> None:
        with RelayHarness(auth_failures_per_ip=3, auth_failure_window_seconds=1) as relay:
            self.assertEqual(201, relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            wrong = key_for("wrong")
            for _ in range(3):
                self.assertEqual(401, relay.dav("PROPFIND", "", basic=(CONVERSATION, wrong), headers={"Depth": "0"}).status)
            limited = relay.dav("PROPFIND", "", basic=(CONVERSATION, wrong), headers={"Depth": "0"})
            self.assertEqual(429, limited.status)
            self.assertIsNotNone(limited.header("retry-after"))
            # Fail-closed while the window is open: even a correct key waits it out instead of
            # letting a password oracle keep the KDF busy.
            self.assertEqual(429, relay.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"}).status)
            time.sleep(1.1)
            self.assertEqual(207, relay.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"}).status)

    def test_auth_failure_window_can_be_disabled(self) -> None:
        with RelayHarness(auth_failures_per_ip=0) as relay:
            self.assertEqual(201, relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            wrong = key_for("wrong")
            for _ in range(10):
                self.assertEqual(401, relay.dav("PROPFIND", "", basic=(CONVERSATION, wrong), headers={"Depth": "0"}).status)
            self.assertEqual(207, relay.dav("PROPFIND", "", basic=(CONVERSATION, CONVERSATION_KEY), headers={"Depth": "0"}).status)

    def test_max_conversations_is_enforced(self) -> None:
        with RelayHarness(max_conversations=1) as relay:
            self.assertEqual(201, relay.conversations(("self-aaaa1111", key_for("a"))).status)
            self.assertEqual(429, relay.conversations(("self-bbbb2222", key_for("b"))).status)


class ClientAddressTests(unittest.TestCase):
    """nginx connects from loopback, so the per-IP limits must key on the forwarded address."""

    def test_rate_limits_follow_the_forwarded_address(self) -> None:
        with RelayHarness(register_per_ip_per_hour=1) as relay:
            first = relay.conversations(("self-aaaa1111", key_for("a")), headers={"X-Real-IP": "10.0.0.1"})
            self.assertEqual(201, first.status)
            second = relay.conversations(("self-bbbb2222", key_for("b")), headers={"X-Real-IP": "10.0.0.2"})
            self.assertEqual(201, second.status, "a different client address must have its own bucket")
            third = relay.conversations(("self-cccc3333", key_for("c")), headers={"X-Real-IP": "10.0.0.1"})
            self.assertEqual(429, third.status)

    def test_forwarded_address_is_only_trusted_from_a_trusted_proxy(self) -> None:
        from mpt_relay.config import Config
        from mpt_relay.httpd import client_ip

        config = Config.load([], {"MPT_RELAY_TRUSTED_PROXIES": "127.0.0.1"})
        self.assertEqual("10.0.0.9", client_ip("127.0.0.1", {"x-real-ip": "10.0.0.9"}, config))
        self.assertEqual("10.0.0.9", client_ip("127.0.0.1", {"x-forwarded-for": "203.0.113.7, 10.0.0.9"}, config))
        self.assertEqual("192.0.2.5", client_ip("192.0.2.5", {"x-real-ip": "10.0.0.9"}, config))
        untrusting = Config.load([], {"MPT_RELAY_TRUST_PROXY_HEADERS": "0"})
        self.assertEqual("127.0.0.1", client_ip("127.0.0.1", {"x-real-ip": "10.0.0.9"}, untrusting))


class LogHygieneTests(unittest.TestCase):
    def test_logs_never_contain_credentials_or_bodies(self) -> None:
        with RelayHarness(mask_log_ids=1) as relay:
            self.assertEqual(201, relay.conversations((CONVERSATION, CONVERSATION_KEY)).status)
            relay.dav("MKCOL", "assistant/", basic=(CONVERSATION, CONVERSATION_KEY))
            relay.dav("MKCOL", "assistant/" + CONVERSATION + "/", basic=(CONVERSATION, CONVERSATION_KEY))
            secret_body = b'{"secret":"payload-must-not-be-logged"}'
            relay.dav(
                "PUT",
                f"assistant/{CONVERSATION}/{'a' * 32}/manifest.json",
                basic=(CONVERSATION, CONVERSATION_KEY),
                body=secret_body,
            )
            relay.dav("PROPFIND", "", basic=(CONVERSATION, key_for("wrong")), headers={"Depth": "0"})

            encoded = base64.b64encode(f"{CONVERSATION}:{CONVERSATION_KEY}".encode()).decode()
            logs = "\n".join(relay.logs())
            self.assertNotEqual("", logs)
            self.assertNotIn(CONVERSATION_KEY, logs)
            self.assertNotIn(CONVERSATION_KEY.upper(), logs)
            self.assertNotIn(encoded, logs)
            self.assertNotIn("payload-must-not-be-logged", logs)
            self.assertNotIn(CONVERSATION, logs)  # ids are masked in paths and events
            self.assertIn("assistant/self…3c4d", logs)


if __name__ == "__main__":
    unittest.main()

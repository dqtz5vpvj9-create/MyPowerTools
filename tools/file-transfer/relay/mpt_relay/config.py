"""Configuration for the public relay.

Precedence (lowest to highest): built-in defaults, ``--config`` file, ``MPT_RELAY_*``
environment variables, command line flags. The config file uses the systemd
``EnvironmentFile`` syntax (``KEY=VALUE`` lines, ``#`` comments, optional quotes) so the
same file can be passed to ``systemd`` and to ``python3 -m mpt_relay --config``.
"""

from __future__ import annotations

import argparse
import json
import os
from dataclasses import asdict, dataclass, fields
from pathlib import Path
from typing import Dict, Mapping, Optional, Sequence, Tuple

ENV_PREFIX = "MPT_RELAY_"

MIB = 1 << 20
GIB = 1 << 30


def _defaults() -> Dict[str, object]:
    return {
        # --- transport -----------------------------------------------------------------
        "host": "127.0.0.1",
        "port": 18765,
        "base_path": "/mpt/relay",
        "socket_timeout_seconds": 120,
        "max_connections": 64,
        "longpoll_max_seconds": 25,
        "body_budget_seconds": 900,
        # --- storage -------------------------------------------------------------------
        "data_dir": "/var/lib/mpt-relay",
        "per_conversation_bytes": 1 * GIB,
        "global_bytes": 8 * GIB,
        "max_file_bytes": 512 * MIB,
        "max_conversations": 10000,
        "max_inboxes": 10000,
        "max_entries_per_conversation": 20000,
        "max_path_depth": 8,
        "empty_namespace_ttl_days": 0,
        # --- abuse control -------------------------------------------------------------
        "register_per_ip_per_hour": 5,
        "register_global_per_hour": 100,
        "auth_failures_per_ip": 60,
        "not_ready_per_ip_per_minute": 120,
        "auth_failure_window_seconds": 300,
        "max_concurrent_auth": 8,
        "max_tracked_addresses": 4096,
        "kdf_iterations": 120000,
        "trust_proxy_headers": True,
        "trusted_proxies": ("127.0.0.1", "::1"),
        # First-up production posture: only the explicit POST registers a namespace. The
        # WebDAV-only compatibility path can be re-enabled with MPT_RELAY_DAV_AUTO_REGISTER=1.
        "dav_auto_register": False,
        # --- diagnostics ---------------------------------------------------------------
        "log_level": "info",
        "mask_log_ids": True,
    }


#: Attributes whose values are comma separated lists in the environment / config file.
_TUPLE_FIELDS = ("trusted_proxies",)
_BOOL_TRUE = ("1", "true", "yes", "on")
_BOOL_FALSE = ("0", "false", "no", "off")


class ConfigError(Exception):
    """The operator supplied a configuration value the service cannot use."""


@dataclass(frozen=True)
class Config:
    host: str = "127.0.0.1"
    port: int = 18765
    base_path: str = "/mpt/relay"
    socket_timeout_seconds: int = 120
    max_connections: int = 64
    longpoll_max_seconds: int = 25
    body_budget_seconds: int = 900
    data_dir: str = "/var/lib/mpt-relay"
    per_conversation_bytes: int = 1 * GIB
    global_bytes: int = 8 * GIB
    max_file_bytes: int = 512 * MIB
    max_conversations: int = 10000
    max_inboxes: int = 10000
    max_entries_per_conversation: int = 20000
    max_path_depth: int = 8
    empty_namespace_ttl_days: int = 0
    register_per_ip_per_hour: int = 5
    register_global_per_hour: int = 100
    auth_failures_per_ip: int = 60
    not_ready_per_ip_per_minute: int = 120
    auth_failure_window_seconds: int = 300
    max_concurrent_auth: int = 8
    max_tracked_addresses: int = 4096
    kdf_iterations: int = 120000
    trust_proxy_headers: bool = True
    trusted_proxies: Tuple[str, ...] = ("127.0.0.1", "::1")
    dav_auto_register: bool = False
    log_level: str = "info"
    mask_log_ids: bool = True

    # -- derived ---------------------------------------------------------------------

    @property
    def normalized_base_path(self) -> str:
        """``""`` for the server root, otherwise ``/prefix`` without a trailing slash."""
        value = (self.base_path or "").strip()
        if value in ("", "/"):
            return ""
        if not value.startswith("/"):
            value = "/" + value
        return value.rstrip("/")

    @property
    def dav_root(self) -> str:
        return f"{self.normalized_base_path}/dav/"

    @property
    def data_path(self) -> Path:
        return Path(self.data_dir)

    def to_json(self) -> str:
        payload = asdict(self)
        payload["trusted_proxies"] = list(self.trusted_proxies)
        return json.dumps(payload, indent=2, sort_keys=True)

    # -- loading ---------------------------------------------------------------------

    @classmethod
    def load(
        cls,
        argv: Optional[Sequence[str]] = None,
        environ: Optional[Mapping[str, str]] = None,
    ) -> "Config":
        environ = os.environ if environ is None else environ
        parser = _build_parser()
        args = parser.parse_args(list(argv) if argv is not None else None)

        values: Dict[str, object] = _defaults()
        if args.config:
            values.update(read_config_file(args.config))
        values.update(_from_environ(environ))
        for name in ("host", "port", "data_dir", "base_path", "log_level"):
            value = getattr(args, name, None)
            if value is not None:
                values[name] = value
        config = cls(**_coerce_all(values))
        _validate(config)
        return config


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="mpt_relay",
        description="MyPowerTools public relay for the file assistant (stdlib only).",
    )
    parser.add_argument("--config", metavar="FILE", help="EnvironmentFile-style configuration file.")
    parser.add_argument("--host", help="Listen address (default 127.0.0.1).")
    parser.add_argument("--port", type=int, help="Listen port (default 18765).")
    parser.add_argument("--data-dir", help="State directory (SQLite + conversation roots).")
    parser.add_argument("--base-path", help="Public path prefix (default /mpt/relay).")
    parser.add_argument("--log-level", choices=("debug", "info", "warning", "error"))
    parser.add_argument("--print-config", action="store_true", help="Print the effective configuration and exit.")
    parser.add_argument("--version", action="store_true", help="Print the version and exit.")
    return parser


def read_config_file(path: str) -> Dict[str, object]:
    """Reads an ``EnvironmentFile``; returns attribute-name keyed raw string values."""
    values: Dict[str, object] = {}
    file_path = Path(path)
    if not file_path.exists():
        raise ConfigError(f"配置文件不存在：{path}")
    for number, raw in enumerate(file_path.read_text(encoding="utf-8").splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[len("export ") :].strip()
        if "=" not in line:
            raise ConfigError(f"{path}:{number} 不是 KEY=VALUE 形式。")
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        values[_attribute_name(key)] = value
    return values


def _attribute_name(key: str) -> str:
    name = key.strip()
    if name.upper().startswith(ENV_PREFIX):
        name = name[len(ENV_PREFIX) :]
    return name.lower()


def _from_environ(environ: Mapping[str, str]) -> Dict[str, object]:
    values: Dict[str, object] = {}
    for key, value in environ.items():
        if key.upper().startswith(ENV_PREFIX):
            values[_attribute_name(key)] = value
    return values


def _coerce_all(values: Mapping[str, object]) -> Dict[str, object]:
    known = {item.name: item for item in fields(Config)}
    defaults = _defaults()
    coerced: Dict[str, object] = {}
    for name, raw in values.items():
        if name not in known:
            # Unknown MPT_RELAY_* keys are ignored on purpose: a typo in one variable must
            # not stop a running relay from starting after an upgrade.
            continue
        coerced[name] = _coerce(name, raw, defaults.get(name))
    return coerced


def _coerce(name: str, raw: object, default: object) -> object:
    if name in _TUPLE_FIELDS:
        if isinstance(raw, (tuple, list)):
            return tuple(str(item).strip() for item in raw if str(item).strip())
        return tuple(part.strip() for part in str(raw).split(",") if part.strip())
    if isinstance(default, bool):
        if isinstance(raw, bool):
            return raw
        text = str(raw).strip().lower()
        if text in _BOOL_TRUE:
            return True
        if text in _BOOL_FALSE:
            return False
        raise ConfigError(f"{name} 需要布尔值（1/0），收到 {raw!r}。")
    if isinstance(default, int) and not isinstance(default, bool):
        try:
            return int(str(raw).strip())
        except ValueError as error:
            raise ConfigError(f"{name} 需要整数，收到 {raw!r}。") from error
    return str(raw).strip() if isinstance(raw, str) else raw


def _validate(config: Config) -> None:
    # Port 0 lets the kernel pick a free port; the test suite and local probes use it, while
    # production always binds the configured fixed port.
    if not (0 <= config.port < 65536):
        raise ConfigError("port 必须在 0..65535 之间。")
    if not config.host:
        raise ConfigError("host 不能为空。")
    if ":" in config.base_path:
        raise ConfigError("base_path 只能是路径前缀。")
    if config.per_conversation_bytes <= 0 or config.global_bytes <= 0 or config.max_file_bytes <= 0:
        raise ConfigError("配额必须是正数。")
    if config.max_file_bytes > config.per_conversation_bytes:
        # A single file that cannot fit a namespace is a configuration mistake, not a policy.
        raise ConfigError("max_file_bytes 不能大于 per_conversation_bytes。")
    if not (1 <= config.max_path_depth <= 32):
        raise ConfigError("max_path_depth 必须在 1..32 之间。")
    if config.longpoll_max_seconds < 1 or config.longpoll_max_seconds > 300:
        raise ConfigError("longpoll_max_seconds 必须在 1..300 之间。")
    if config.kdf_iterations < 10000:
        raise ConfigError("kdf_iterations 太小，至少 10000。")
    if config.max_inboxes < 0:
        raise ConfigError("max_inboxes 不能为负数。")
    if config.not_ready_per_ip_per_minute < 1:
        raise ConfigError("not_ready_per_ip_per_minute 必须 ≥ 1。")
    if config.auth_failures_per_ip < 0:
        raise ConfigError("auth_failures_per_ip 不能为负数（0 = 关闭失败窗口）。")
    if config.auth_failure_window_seconds < 1:
        raise ConfigError("auth_failure_window_seconds 必须 ≥ 1。")
    if config.max_concurrent_auth < 1:
        raise ConfigError("max_concurrent_auth 必须 ≥ 1。")
    if config.max_connections < 8:
        raise ConfigError("max_connections 必须 ≥ 8。")
    if config.body_budget_seconds < 1:
        raise ConfigError("body_budget_seconds 必须 ≥ 1（请求体读取的墙钟预算）。")
    if config.max_tracked_addresses < 16:
        raise ConfigError("max_tracked_addresses 必须 ≥ 16。")
    if config.log_level not in ("debug", "info", "warning", "error"):
        raise ConfigError("log_level 取值非法。")

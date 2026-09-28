"""Process wiring: configuration -> store -> app -> loopback HTTP server."""

from __future__ import annotations

import logging
import signal
import sys
import threading
from typing import Optional, Tuple

from . import __version__
from .app import RelayApp
from .config import Config
from .httpd import RelayServer
from .store import Store

LOG_FORMAT = "%(asctime)s %(levelname)s %(name)s %(message)s"


def setup_logging(config: Config, stream=None) -> logging.Logger:
    logger = logging.getLogger("mpt_relay")
    logger.setLevel(getattr(logging, config.log_level.upper(), logging.INFO))
    handler = logging.StreamHandler(stream or sys.stderr)
    handler.setFormatter(logging.Formatter(LOG_FORMAT))
    logger.handlers = [handler]
    logger.propagate = False
    return logger


def build(config: Config, logger: Optional[logging.Logger] = None) -> Tuple[Store, RelayApp, RelayServer]:
    logger = logger or setup_logging(config)
    store = Store(config)
    app = RelayApp(config, store, logger)
    server = RelayServer(config, app, store, logger)
    return store, app, server


def serve_forever(config: Config, logger: Optional[logging.Logger] = None) -> int:
    logger = logger or setup_logging(config)
    store, app, server = build(config, logger)
    stopping = threading.Event()

    def request_stop(signum, _frame):  # pragma: no cover - signal path
        logger.info("received signal %s, shutting down", signum)
        stopping.set()

    for name in ("SIGTERM", "SIGINT"):
        if hasattr(signal, name):
            try:
                signal.signal(getattr(signal, name), request_stop)
            except (ValueError, OSError):  # not the main thread
                pass

    logger.info(
        "mpt-relay %s listening on %s:%d base=%s data=%s conversations=%d usage=%d/%d",
        __version__,
        config.host,
        server.port,
        config.normalized_base_path or "/",
        config.data_dir,
        store.conversation_count(),
        store.global_usage(),
        config.global_bytes,
    )
    server.start_maintenance()
    thread = threading.Thread(target=server.serve_forever, name="mpt-relay-serve", daemon=True)
    thread.start()
    try:
        while not stopping.wait(0.5):
            if not thread.is_alive():  # pragma: no cover - defensive
                break
    except KeyboardInterrupt:  # pragma: no cover - interactive stop
        pass
    finally:
        server.stop()
        store.shutdown()
        thread.join(timeout=5)
        logger.info("mpt-relay stopped")
    return 0

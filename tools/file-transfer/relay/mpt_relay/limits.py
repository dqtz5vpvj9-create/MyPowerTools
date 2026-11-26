"""Small, dependency-free abuse controls.

Three independent limiters guard the public surface:

* namespace *creation* per client address (the relay is on the open internet, so accounts
  must not be free to mint),
* namespace creation across the whole service (protects the disk and the SQLite file),
* failed authentication attempts per client address (each verification runs a KDF, so an
  unbounded password oracle would be a CPU denial of service).

Both trackers are hard bounded: the number of distinct keys is capped and the oldest key is
evicted when the cap is reached, so a client rotating addresses can neither grow memory nor
turn every request into a full-table scan. Per key only the newest ``limit`` events are
kept, which is all the decision needs.

All limiters are in-memory on purpose: a restart clears them, and every decision they make
only ever *delays* a legitimate client, never loses data.
"""

from __future__ import annotations

import threading
import time
from collections import OrderedDict
from typing import List, Optional, Tuple

DEFAULT_MAX_TRACKED_KEYS = 4096


class FailureWindow:
    """Counts recent failures per key inside a sliding window."""

    def __init__(self, limit: int, window_seconds: int, max_keys: int = DEFAULT_MAX_TRACKED_KEYS) -> None:
        self._limit = max(1, int(limit))
        self._window = max(1.0, float(window_seconds))
        self._max_keys = max(16, int(max_keys))
        self._events: "OrderedDict[str, List[float]]" = OrderedDict()
        self._lock = threading.Lock()

    def blocked_for(self, key: str, now: Optional[float] = None) -> float:
        """Seconds the caller must wait, or ``0.0`` when it may proceed."""
        moment = time.monotonic() if now is None else now
        with self._lock:
            events = self._events.get(key)
            if not events:
                return 0.0
            self._trim(events, moment)
            if not events:
                self._events.pop(key, None)
                return 0.0
            if len(events) < self._limit:
                return 0.0
            return max(0.0, events[0] + self._window - moment)

    def record(self, key: str, now: Optional[float] = None) -> None:
        moment = time.monotonic() if now is None else now
        with self._lock:
            events = self._events.get(key)
            if events is None:
                # O(1) eviction of the least recently recorded key: no full scan, no growth.
                while len(self._events) >= self._max_keys:
                    self._events.popitem(last=False)
                events = []
                self._events[key] = events
            else:
                self._events.move_to_end(key)
                self._trim(events, moment)
            events.append(moment)
            if len(events) > self._limit:
                del events[: len(events) - self._limit]

    def reset(self, key: str) -> None:
        with self._lock:
            self._events.pop(key, None)

    def tracked_keys(self) -> int:
        with self._lock:
            return len(self._events)

    def _trim(self, events: List[float], now: float) -> None:
        cutoff = now - self._window
        while events and events[0] <= cutoff:
            events.pop(0)


class RateLimiter:
    """At most ``limit`` accepted events per key in a sliding window."""

    def __init__(self, limit: int, window_seconds: float, max_keys: int = DEFAULT_MAX_TRACKED_KEYS) -> None:
        self._limit = max(1, int(limit))
        self._window = max(1.0, float(window_seconds))
        self._max_keys = max(16, int(max_keys))
        self._events: "OrderedDict[str, List[float]]" = OrderedDict()
        self._lock = threading.Lock()

    def allow(self, key: str, now: Optional[float] = None) -> Tuple[bool, float]:
        """Records the attempt when allowed; returns ``(allowed, retry_after_seconds)``."""
        moment = time.monotonic() if now is None else now
        with self._lock:
            events = self._events.get(key)
            if events is None:
                while len(self._events) >= self._max_keys:
                    self._events.popitem(last=False)
                events = []
                self._events[key] = events
            else:
                self._events.move_to_end(key)
                cutoff = moment - self._window
                while events and events[0] <= cutoff:
                    events.pop(0)
            if len(events) >= self._limit:
                return False, max(1.0, events[0] + self._window - moment)
            events.append(moment)
            return True, 0.0

    def tracked_keys(self) -> int:
        with self._lock:
            return len(self._events)

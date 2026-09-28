"""Durable state: credentials, revisions, per-namespace usage and the conversation roots.

Layout under ``data_dir``::

    relay.sqlite3                  credentials (salted digest only), revisions, usage
    conversations/<conversation>/  the private WebDAV root of one conversation

Design notes that the protocol depends on:

* A conversation key is **never** stored, logged or returned. Only a PBKDF2-HMAC-SHA256
  digest with a random per-conversation salt is persisted, and that is only ever used to
  answer "does this presented key match?".
* The revision counter is per conversation. Two users of the public relay never observe
  each other's writes, and a long poll only wakes for the caller's own namespace.
* Usage is measured from the filesystem at start-up, then maintained under a lock; the
  SQLite copy is a report, the filesystem is the truth.
"""

from __future__ import annotations

import hashlib
import hmac
import os
import shutil
import sqlite3
import threading
import time
from pathlib import Path
from typing import Callable, Dict, NamedTuple, Optional, Tuple

from .auth import CONVERSATION_ID_PATTERN
from .config import Config
from .fsutil import TEMP_PREFIX

#: Every control file the service owns inside a namespace root.
INTERNAL_PREFIXES = (TEMP_PREFIX,)

SCHEMA = """
CREATE TABLE IF NOT EXISTS conversations (
    conversation_id TEXT PRIMARY KEY,
    salt            BLOB    NOT NULL,
    digest          BLOB    NOT NULL,
    iterations      INTEGER NOT NULL,
    created_at      REAL    NOT NULL,
    last_seen_at    REAL    NOT NULL DEFAULT 0,
    revision        INTEGER NOT NULL DEFAULT 0,
    used_bytes      INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS inboxes (
    inbox_id        TEXT PRIMARY KEY,
    owner_salt      BLOB    NOT NULL,
    owner_digest    BLOB    NOT NULL,
    deposit_salt    BLOB    NOT NULL,
    deposit_digest  BLOB    NOT NULL,
    iterations      INTEGER NOT NULL,
    created_at      REAL    NOT NULL,
    last_seen_at    REAL    NOT NULL DEFAULT 0,
    revision        INTEGER NOT NULL DEFAULT 0,
    used_bytes      INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""

#: Accounting/revision spaces. The prefix keeps a conversation and an inbox with the same
#: identifier from ever sharing quota or a revision counter.
CONVERSATION_SPACE = "c"
INBOX_SPACE = "i"


class StoreError(Exception):
    """Base class for storage failures the HTTP layer maps onto status codes."""


class CredentialMismatch(StoreError):
    """The conversation exists and the presented key is wrong."""


class QuotaExceeded(StoreError):
    """The write would exceed a namespace or service quota."""


class TooLarge(StoreError):
    """The write exceeds the single-file limit."""


class Conflict(StoreError):
    """The WebDAV request cannot be satisfied by the namespace tree as it stands."""


class AlreadyExists(StoreError):
    """WebDAV MKCOL on an existing resource."""


class DepositKeyConflict(StoreError):
    """An inbox is already registered and its deposit key may not be replaced."""


class InboxAuth(NamedTuple):
    """Result of an inbox credential check: the namespace and the role of the key."""

    known: bool
    role: Optional[str]  # "owner" | "deposit" | None


def is_internal_name(name: str) -> bool:
    return name.startswith(INTERNAL_PREFIXES)


class Store:
    """Owns the SQLite database, the conversation roots and the long-poll wakeups."""

    def __init__(self, config: Config) -> None:
        self._config = config
        self._data_dir = config.data_path
        self._conversations_dir = self._data_dir / "conversations"
        self._conversations_dir.mkdir(parents=True, exist_ok=True)
        self._inboxes_dir = self._data_dir / "inboxes"
        self._inboxes_dir.mkdir(parents=True, exist_ok=True)
        self._inbox_verify_cache: Dict[Tuple[str, bytes], str] = {}
        self._db = sqlite3.connect(str(self._data_dir / "relay.sqlite3"), check_same_thread=False)
        self._db.row_factory = sqlite3.Row
        self._db_lock = threading.RLock()
        with self._db_lock:
            self._db.execute("PRAGMA journal_mode=WAL")
            self._db.execute("PRAGMA synchronous=FULL")
            self._db.execute("PRAGMA busy_timeout=10000")
            self._db.executescript(SCHEMA)
            self._db.commit()

        self._lock = threading.RLock()
        self._usage: Dict[str, int] = {}
        self._global_usage = 0
        self._global_reserved = 0
        self._conversation_locks: Dict[str, threading.RLock] = {}

        self._watch_lock = threading.Lock()
        self._watch: Dict[str, threading.Condition] = {}
        self._stopping = threading.Event()

        self._verify_cache: Dict[Tuple[str, bytes], bool] = {}
        self._verify_cache_secret = os.urandom(32)
        self._verify_cache_limit = 512

        self._reconcile()

    # -- lifecycle -------------------------------------------------------------------

    def shutdown(self) -> None:
        self._stopping.set()
        with self._watch_lock:
            conditions = list(self._watch.values())
        for condition in conditions:
            with condition:
                condition.notify_all()
        with self._db_lock:
            try:
                self._db.commit()
            except sqlite3.Error:
                pass

    @property
    def config(self) -> Config:
        return self._config

    @property
    def data_dir(self) -> Path:
        return self._data_dir

    # -- paths -----------------------------------------------------------------------

    def conversation_dir(self, conversation_id: str) -> Path:
        """The private WebDAV root of one conversation.

        ``conversation_id`` is always validated first, so it can never contain a path
        separator; the containment check below is defence in depth.
        """
        if not CONVERSATION_ID_PATTERN.match(conversation_id):
            raise ValueError("非法的会话 id。")
        root = self._conversations_dir / conversation_id
        resolved = Path(os.path.normpath(str(root)))
        if resolved.parent != self._conversations_dir:
            raise ValueError("会话目录越界。")
        return root

    def conversation_lock(self, conversation_id: str) -> threading.RLock:
        return self.space_lock(f"{CONVERSATION_SPACE}:{conversation_id}")

    # -- credentials -----------------------------------------------------------------

    def register(self, conversation_id: str, conversation_key: str) -> Tuple[bool, int]:
        """First-use registration.

        Returns ``(created, revision)``. An existing conversation accepts only its own key
        (idempotent re-registration) and raises :class:`CredentialMismatch` otherwise; the
        key of an existing namespace can never be replaced.
        """
        key = conversation_key.lower()
        # The root exists before the credential row does. A crash in between leaves an empty
        # directory that start-up reconciliation removes as an orphan; the reverse order would
        # leave a namespace that can authenticate but cannot store anything.
        self.conversation_dir(conversation_id).mkdir(parents=True, exist_ok=True)
        salt = os.urandom(16)
        with self._db_lock:
            row = self._db.execute(
                "SELECT salt, digest, iterations, revision FROM conversations WHERE conversation_id = ?",
                (conversation_id,),
            ).fetchone()
            if row is not None:
                if not self._matches(row, key):
                    raise CredentialMismatch("会话已注册，密钥不一致。")
                self._touch(conversation_id)
                return False, int(row["revision"])
            digest = self._derive(key, salt, self._config.kdf_iterations)
            now = time.time()
            try:
                self._db.execute(
                    "INSERT INTO conversations (conversation_id, salt, digest, iterations, created_at, last_seen_at)"
                    " VALUES (?, ?, ?, ?, ?, ?)",
                    (conversation_id, salt, digest, self._config.kdf_iterations, now, now),
                )
                self._db.commit()
            except sqlite3.IntegrityError:
                # A parallel request created it between the SELECT and the INSERT.
                row = self._db.execute(
                    "SELECT salt, digest, iterations, revision FROM conversations WHERE conversation_id = ?",
                    (conversation_id,),
                ).fetchone()
                if row is None or not self._matches(row, key):
                    raise CredentialMismatch("会话已注册，密钥不一致。")
                return False, int(row["revision"])
        with self._lock:
            self._usage.setdefault(conversation_id, 0)
        return True, 0

    def verify(self, conversation_id: str, conversation_key: str) -> Optional[bool]:
        """``True``/``False`` for a known conversation, ``None`` when it is unknown."""
        key = conversation_key.lower()
        cache_key = (conversation_id, hmac.new(self._verify_cache_secret, key.encode("ascii"), hashlib.sha256).digest())
        with self._lock:
            cached = self._verify_cache.get(cache_key)
        if cached is not None:
            return cached
        with self._db_lock:
            row = self._db.execute(
                "SELECT salt, digest, iterations FROM conversations WHERE conversation_id = ?",
                (conversation_id,),
            ).fetchone()
            if row is None:
                return None
            matched = self._matches(row, key)
        if matched:
            self._remember_verification(cache_key)
            self._touch(conversation_id)
        return matched

    def _remember_verification(self, cache_key: Tuple[str, bytes]) -> None:
        with self._lock:
            if len(self._verify_cache) >= self._verify_cache_limit:
                # Cheap bounded eviction: drop the oldest quarter.
                for stale in list(self._verify_cache)[: self._verify_cache_limit // 4]:
                    self._verify_cache.pop(stale, None)
            self._verify_cache[cache_key] = True

    def _matches(self, row: sqlite3.Row, key: str) -> bool:
        candidate = self._derive(key, row["salt"], int(row["iterations"]))
        return hmac.compare_digest(candidate, row["digest"])

    def _derive(self, key: str, salt: bytes, iterations: int) -> bytes:
        return hashlib.pbkdf2_hmac("sha256", key.encode("ascii"), salt, iterations, dklen=32)

    def _touch(self, identifier: str, table: str = "conversations", column: str = "conversation_id") -> None:
        try:
            with self._db_lock:
                self._db.execute(
                    f"UPDATE {table} SET last_seen_at = ? WHERE {column} = ?",
                    (time.time(), identifier),
                )
                self._db.commit()
        except sqlite3.Error:
            pass

    def conversation_count(self) -> int:
        with self._db_lock:
            row = self._db.execute("SELECT COUNT(*) AS total FROM conversations").fetchone()
        return int(row["total"]) if row else 0

    def conversation_exists(self, conversation_id: str) -> bool:
        with self._db_lock:
            row = self._db.execute(
                "SELECT 1 FROM conversations WHERE conversation_id = ?", (conversation_id,)
            ).fetchone()
        return row is not None

    # -- inbox namespaces --------------------------------------------------------------

    def inbox_dir(self, inbox_id: str) -> Path:
        """The private root of one pairing inbox. Same containment rules as a conversation."""
        if not CONVERSATION_ID_PATTERN.match(inbox_id):
            raise ValueError("非法的收件箱 id。")
        root = self._inboxes_dir / inbox_id
        resolved = Path(os.path.normpath(str(root)))
        if resolved.parent != self._inboxes_dir:
            raise ValueError("收件箱目录越界。")
        return root

    @staticmethod
    def inbox_space(inbox_id: str) -> str:
        return f"{INBOX_SPACE}:{inbox_id}"

    def register_inbox(self, inbox_id: str, owner_key: str, deposit_key: str) -> Tuple[bool, int]:
        """First-use registration of a pairing inbox.

        The owner key is the Basic password of this call; the deposit key arrives in the body.
        Re-registering with the same pair is idempotent. A different key for an existing inbox
        never takes effect: a foreign key is :class:`CredentialMismatch` and a new deposit key
        is :class:`DepositKeyConflict`, so a deposit credential can never become the owner and
        the pair code handed out earlier keeps working.
        """
        owner = owner_key.lower()
        deposit = deposit_key.lower()
        self.inbox_dir(inbox_id).mkdir(parents=True, exist_ok=True)
        owner_salt = os.urandom(16)
        deposit_salt = os.urandom(16)
        with self._db_lock:
            row = self._db.execute(
                "SELECT owner_salt, owner_digest, deposit_salt, deposit_digest, iterations, revision"
                " FROM inboxes WHERE inbox_id = ?",
                (inbox_id,),
            ).fetchone()
            if row is not None:
                if not self._matches_columns(row, "owner_salt", "owner_digest", owner):
                    raise CredentialMismatch("收件箱已注册，owner 密钥不一致。")
                if not self._matches_columns(row, "deposit_salt", "deposit_digest", deposit):
                    raise DepositKeyConflict("收件箱已注册，投递密钥不可更换。")
                self._touch(inbox_id, table="inboxes", column="inbox_id")
                return False, int(row["revision"])
            iterations = self._config.kdf_iterations
            now = time.time()
            try:
                self._db.execute(
                    "INSERT INTO inboxes (inbox_id, owner_salt, owner_digest, deposit_salt,"
                    " deposit_digest, iterations, created_at, last_seen_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    (
                        inbox_id,
                        owner_salt,
                        self._derive(owner, owner_salt, iterations),
                        deposit_salt,
                        self._derive(deposit, deposit_salt, iterations),
                        iterations,
                        now,
                        now,
                    ),
                )
                self._db.commit()
            except sqlite3.IntegrityError:
                row = self._db.execute(
                    "SELECT owner_salt, owner_digest, deposit_salt, deposit_digest, iterations, revision"
                    " FROM inboxes WHERE inbox_id = ?",
                    (inbox_id,),
                ).fetchone()
                if row is None or not self._matches_columns(row, "owner_salt", "owner_digest", owner):
                    raise CredentialMismatch("收件箱已注册，owner 密钥不一致。") from None
                if not self._matches_columns(row, "deposit_salt", "deposit_digest", deposit):
                    raise DepositKeyConflict("收件箱已注册，投递密钥不可更换。") from None
                return False, int(row["revision"])
        with self._lock:
            self._usage.setdefault(self.inbox_space(inbox_id), 0)
        return True, 0

    def verify_inbox(self, inbox_id: str, key: str) -> InboxAuth:
        """Which role a presented key has for this inbox; ``known=False`` for an unknown inbox."""
        lowered = key.lower()
        cache_key = (
            self.inbox_space(inbox_id),
            hmac.new(self._verify_cache_secret, lowered.encode("ascii"), hashlib.sha256).digest(),
        )
        with self._lock:
            cached = self._inbox_verify_cache.get(cache_key)
        if cached is not None:
            return InboxAuth(True, cached)
        with self._db_lock:
            row = self._db.execute(
                "SELECT owner_salt, owner_digest, deposit_salt, deposit_digest, iterations"
                " FROM inboxes WHERE inbox_id = ?",
                (inbox_id,),
            ).fetchone()
            if row is None:
                return InboxAuth(False, None)
            if self._matches_columns(row, "owner_salt", "owner_digest", lowered):
                role = "owner"
            elif self._matches_columns(row, "deposit_salt", "deposit_digest", lowered):
                role = "deposit"
            else:
                return InboxAuth(True, None)
        with self._lock:
            if len(self._inbox_verify_cache) >= self._verify_cache_limit:
                for stale in list(self._inbox_verify_cache)[: self._verify_cache_limit // 4]:
                    self._inbox_verify_cache.pop(stale, None)
            self._inbox_verify_cache[cache_key] = role
        self._touch(inbox_id, table="inboxes", column="inbox_id")
        return InboxAuth(True, role)

    def inbox_count(self) -> int:
        with self._db_lock:
            row = self._db.execute("SELECT COUNT(*) AS total FROM inboxes").fetchone()
        return int(row["total"]) if row else 0

    def inbox_exists(self, inbox_id: str) -> bool:
        with self._db_lock:
            row = self._db.execute("SELECT 1 FROM inboxes WHERE inbox_id = ?", (inbox_id,)).fetchone()
        return row is not None

    def inbox_usage(self, inbox_id: str) -> int:
        return self.usage_space(self.inbox_space(inbox_id))

    def _matches_columns(self, row: sqlite3.Row, salt_column: str, digest_column: str, key: str) -> bool:
        candidate = self._derive(key, row[salt_column], int(row["iterations"]))
        return hmac.compare_digest(candidate, row[digest_column])

    # -- revisions and long poll ------------------------------------------------------

    def revision(self, conversation_id: str) -> int:
        return self.revision_space(f"{CONVERSATION_SPACE}:{conversation_id}")

    def revision_space(self, space: str) -> int:
        table, column = _space_table(space)
        with self._db_lock:
            row = self._db.execute(
                f"SELECT revision FROM {table} WHERE {column} = ?", (space.split(":", 1)[1],)
            ).fetchone()
        return int(row["revision"]) if row else 0

    def bump_revision(self, conversation_id: str) -> int:
        return self.bump_revision_space(f"{CONVERSATION_SPACE}:{conversation_id}")

    def bump_revision_space(self, space: str) -> int:
        """Advances one namespace revision and wakes its waiters (never another namespace's)."""
        table, column = _space_table(space)
        identifier = space.split(":", 1)[1]
        with self._db_lock:
            self._db.execute(
                f"UPDATE {table} SET revision = revision + 1 WHERE {column} = ?", (identifier,)
            )
            self._db.commit()
            row = self._db.execute(
                f"SELECT revision FROM {table} WHERE {column} = ?", (identifier,)
            ).fetchone()
            revision = int(row["revision"]) if row else 0
        self._notify(space)
        return revision

    def _condition(self, space: str) -> threading.Condition:
        with self._watch_lock:
            condition = self._watch.get(space)
            if condition is None:
                condition = threading.Condition()
                self._watch[space] = condition
            return condition

    def _notify(self, space: str) -> None:
        condition = self._condition(space)
        with condition:
            condition.notify_all()

    def wait_for_revision(
        self,
        conversation_id: str,
        since: int,
        timeout: float,
        abort: Optional[Callable[[], bool]] = None,
    ) -> Optional[int]:
        return self.wait_for_revision_space(f"{CONVERSATION_SPACE}:{conversation_id}", since, timeout, abort)

    def wait_for_revision_space(
        self,
        space: str,
        since: int,
        timeout: float,
        abort: Optional[Callable[[], bool]] = None,
    ) -> Optional[int]:
        """Blocks until the namespace revision is greater than ``since``.

        Returns the revision that satisfied the wait, the (unchanged) revision on timeout,
        or ``None`` when ``abort()`` reported that the caller went away.
        """
        deadline = time.monotonic() + max(0.0, timeout)
        condition = self._condition(space)
        while True:
            current = self.revision_space(space)
            if current > since:
                return current
            if self._stopping.is_set():
                return current
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return current
            if abort is not None and abort():
                return None
            with condition:
                condition.wait(min(0.5, remaining))

    # -- usage and quotas -------------------------------------------------------------

    def usage(self, conversation_id: str) -> int:
        return self.usage_space(f"{CONVERSATION_SPACE}:{conversation_id}")

    def usage_space(self, space: str) -> int:
        with self._lock:
            return self._usage.get(space, 0)

    def global_usage(self) -> int:
        with self._lock:
            return self._global_usage

    def global_reserved(self) -> int:
        """Bytes in flight: checked by an upload but not yet committed to disk."""
        with self._lock:
            return self._global_reserved

    def reserve(self, conversation_id: str, replacing: int = 0, declared: Optional[int] = None) -> "Reservation":
        return self.reserve_space(f"{CONVERSATION_SPACE}:{conversation_id}", replacing, declared)

    def reserve_inbox(self, inbox_id: str, replacing: int = 0, declared: Optional[int] = None) -> "Reservation":
        return self.reserve_space(self.inbox_space(inbox_id), replacing, declared)

    def reserve_space(self, space: str, replacing: int = 0, declared: Optional[int] = None) -> "Reservation":
        """Claims quota for one upload *before* its body is read.

        A plain check-then-write is racy across namespaces: two uploads in different
        namespaces could both pass the global check and then both commit, overfilling the
        disk. A reservation holds the projected bytes until :meth:`Reservation.commit` or
        :meth:`Reservation.release`, so the global ceiling holds under concurrency. Uploads
        within one namespace are already serialized by the namespace lock.
        """
        reservation = Reservation(self, space, max(0, int(replacing)))
        try:
            if declared is not None:
                reservation.grow(declared)
        except BaseException:
            reservation.release()
            raise
        return reservation

    def apply_usage(self, conversation_id: str, new_size: int, old_size: int = 0) -> None:
        self.apply_usage_space(f"{CONVERSATION_SPACE}:{conversation_id}", new_size, old_size)

    def apply_usage_space(self, space: str, new_size: int, old_size: int = 0) -> None:
        with self._lock:
            current = self._usage.get(space, 0)
            updated = current - min(old_size, current) + new_size
            self._usage[space] = updated
            self._global_usage += updated - current
            if self._global_usage < 0:
                self._global_usage = 0
            self._persist_usage(space, updated)

    def ensure_entry_budget(self, conversation_id: str, adding: int = 1) -> None:
        self.ensure_entry_budget_space(f"{CONVERSATION_SPACE}:{conversation_id}", adding)

    def ensure_entry_budget_space(self, space: str, adding: int = 1) -> None:
        cap = self._config.max_entries_per_conversation
        if self.count_entries_space(space, cap) + adding > cap:
            raise QuotaExceeded(f"命名空间条目数达到上限 {cap}。")

    def count_entries(self, conversation_id: str, cap: Optional[int] = None) -> int:
        return self.count_entries_space(f"{CONVERSATION_SPACE}:{conversation_id}", cap)

    def space_dir(self, space: str) -> Path:
        kind, identifier = space.split(":", 1)
        return self.conversation_dir(identifier) if kind == CONVERSATION_SPACE else self.inbox_dir(identifier)

    def space_lock(self, space: str) -> threading.RLock:
        with self._lock:
            lock = self._conversation_locks.get(space)
            if lock is None:
                lock = threading.RLock()
                self._conversation_locks[space] = lock
            return lock

    def count_entries_space(self, space: str, cap: Optional[int] = None) -> int:
        """Counts files and directories, stopping early once ``cap`` is exceeded."""
        root = self.space_dir(space)
        if not root.is_dir():
            return 0
        limit = self._config.max_entries_per_conversation if cap is None else cap
        total = 0
        for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
            total += len(dirnames)
            total += sum(1 for name in filenames if not is_internal_name(name))
            if total > limit:
                return total
        return total

    def _persist_usage(self, space: str, value: int) -> None:
        table, column = _space_table(space)
        try:
            with self._db_lock:
                self._db.execute(
                    f"UPDATE {table} SET used_bytes = ? WHERE {column} = ?",
                    (value, space.split(":", 1)[1]),
                )
                self._db.commit()
        except sqlite3.Error:
            pass

    def measure_tree(self, path: Path) -> int:
        total = 0
        for dirpath, _dirnames, filenames in os.walk(path, followlinks=False):
            for name in filenames:
                if is_internal_name(name):
                    continue
                try:
                    total += os.stat(os.path.join(dirpath, name), follow_symlinks=False).st_size
                except OSError:
                    continue
        return total

    def remove_tree(self, path: Path) -> int:
        """Deletes a file or a collection, returning the bytes it occupied."""
        if path.is_dir() and not path.is_symlink():
            freed = self.measure_tree(path)
            shutil.rmtree(path)
            return freed
        try:
            size = os.stat(path, follow_symlinks=False).st_size
        except OSError:
            size = 0
        os.unlink(path)
        return size

    def sweep_temporary_files(self) -> int:
        """Removes upload temp files left by a crash. They were never visible to clients."""
        removed = 0
        for dirpath, _dirnames, filenames in os.walk(self._conversations_dir, followlinks=False):
            for name in filenames:
                if name.startswith(TEMP_PREFIX):
                    try:
                        os.unlink(os.path.join(dirpath, name))
                        removed += 1
                    except OSError:
                        continue
        return removed

    # -- maintenance ------------------------------------------------------------------

    def reap_empty_namespaces(self) -> int:
        """Optionally forgets namespaces that never stored anything (disabled by default).

        A namespace is only ever reaped when it has no bytes, no revision and no recent
        authentication, so a user file is never deleted silently.
        """
        ttl_days = self._config.empty_namespace_ttl_days
        if ttl_days <= 0:
            return 0
        cutoff = time.time() - ttl_days * 86400
        removed = 0
        with self._db_lock:
            rows = self._db.execute(
                "SELECT conversation_id FROM conversations WHERE used_bytes = 0 AND revision = 0 AND last_seen_at < ?",
                (cutoff,),
            ).fetchall()
        for row in rows:
            conversation_id = row["conversation_id"]
            root = self.conversation_dir(conversation_id)
            if root.is_dir() and self.measure_tree(root) > 0:
                continue
            with self._db_lock:
                self._db.execute("DELETE FROM conversations WHERE conversation_id = ?", (conversation_id,))
                self._db.commit()
            shutil.rmtree(root, ignore_errors=True)
            with self._lock:
                self._usage.pop(conversation_id, None)
            removed += 1
        return removed

    # -- start-up reconciliation -------------------------------------------------------

    def _reconcile(self) -> None:
        """Rebuilds usage from disk; the filesystem is the authority, SQLite is a cache."""
        self.sweep_temporary_files()
        total = 0
        with self._db_lock:
            known_conversations = {
                row["conversation_id"]
                for row in self._db.execute("SELECT conversation_id FROM conversations").fetchall()
            }
            known_inboxes = {
                row["inbox_id"] for row in self._db.execute("SELECT inbox_id FROM inboxes").fetchall()
            }
        for directory, table, column, known in (
            (self._conversations_dir, "conversations", "conversation_id", known_conversations),
            (self._inboxes_dir, "inboxes", "inbox_id", known_inboxes),
        ):
            total += self._reconcile_root(directory, table, column, known)
        with self._lock:
            self._global_usage = total
            self._global_reserved = 0

    def _reconcile_root(self, directory: Path, table: str, column: str, known: set) -> int:
        """Reconciles one namespace root; returns the bytes it currently holds."""
        prefix = f"{CONVERSATION_SPACE}:" if column == "conversation_id" else f"{INBOX_SPACE}:"
        on_disk = set()
        total = 0
        for entry in os.scandir(directory):
            if not entry.is_dir(follow_symlinks=False):
                continue
            identifier = entry.name
            if not CONVERSATION_ID_PATTERN.match(identifier):
                continue
            on_disk.add(identifier)
            used = self.measure_tree(Path(entry.path))
            self._usage[f"{prefix}{identifier}"] = used
            total += used
            if identifier in known:
                self._persist_usage(f"{prefix}{identifier}", used)
            elif used == 0:
                # A crash between mkdir and INSERT; nothing of value can live here.
                shutil.rmtree(entry.path, ignore_errors=True)
        for identifier in known - on_disk:
            self._usage[f"{prefix}{identifier}"] = 0
            self._persist_usage(f"{prefix}{identifier}", 0)
        return total


def _space_table(space: str) -> Tuple[str, str]:
    """Maps an accounting space onto the table and identifier column that own its revision."""
    kind = space.split(":", 1)[0]
    if kind == CONVERSATION_SPACE:
        return "conversations", "conversation_id"
    if kind == INBOX_SPACE:
        return "inboxes", "inbox_id"
    raise ValueError(f"未知的命名空间类型：{space!r}")


class Reservation:
    """Quota claimed for one in-flight upload.

    ``grow`` is called with the total number of bytes written so far for every piece the
    transport reads, so a chunked upload without a declared length is still accounted for
    before the next piece is read. ``commit`` converts the reservation into committed
    usage; ``release`` (always in a ``finally``) gives it back when the upload fails,
    is cancelled, or the client disconnects.
    """

    __slots__ = ("_store", "_space", "_replacing", "_reserved", "_closed")

    def __init__(self, store: Store, space: str, replacing: int) -> None:
        self._store = store
        self._space = space
        self._replacing = max(0, int(replacing))
        self._reserved = 0
        self._closed = False

    @property
    def reserved(self) -> int:
        return self._reserved

    def grow(self, written: int) -> None:
        """Reserves up to ``written`` net bytes; raises when a quota would be exceeded."""
        if self._closed:
            raise RuntimeError("配额预留已结束。")
        store = self._store
        config = store.config
        if written < 0:
            raise ValueError("已写字节数不能为负。")
        if written > config.max_file_bytes:
            raise TooLarge(f"单个文件超过 {config.max_file_bytes} 字节上限。")
        needed = max(0, written - self._replacing)
        if needed <= self._reserved:
            return
        with store._lock:  # noqa: SLF001 - the reservation is part of the store's accounting
            used = store._usage.get(self._space, 0)  # noqa: SLF001
            if used - min(self._replacing, used) + needed > config.per_conversation_bytes:
                raise QuotaExceeded(
                    f"命名空间配额不足：已用 {used} 字节，上限 {config.per_conversation_bytes} 字节。"
                )
            other_reserved = store._global_reserved - self._reserved  # noqa: SLF001
            if store._global_usage + other_reserved + needed > config.global_bytes:  # noqa: SLF001
                raise QuotaExceeded(f"服务总配额不足：上限 {config.global_bytes} 字节。")
            store._global_reserved += needed - self._reserved  # noqa: SLF001
            self._reserved = needed

    def commit(self, new_size: int, old_size: int) -> None:
        """Publishes the written bytes as usage and drops the reservation."""
        with self._store._lock:  # noqa: SLF001 - atomic with the release below
            self._store._global_reserved -= self._reserved  # noqa: SLF001
            self._reserved = 0
            self._closed = True
        self._store.apply_usage_space(self._space, new_size, old_size)

    def release(self) -> None:
        """Returns an uncommitted reservation; safe to call more than once."""
        with self._store._lock:  # noqa: SLF001
            if self._closed:
                return
            self._store._global_reserved -= self._reserved  # noqa: SLF001
            if self._store._global_reserved < 0:  # noqa: SLF001
                self._store._global_reserved = 0  # noqa: SLF001
            self._reserved = 0
            self._closed = True

    def __enter__(self) -> "Reservation":
        return self

    def __exit__(self, *_exc: object) -> None:
        self.release()

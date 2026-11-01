# Author: Codex, 2026-10-19
"""Durable task notices. This module imports no Hermes modules and performs no I/O on import."""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import sqlite3
import time
from typing import Callable
import uuid


AUTHOR = "Codex, 2026-10-19"
PUBLIC_KINDS = frozenset({"received", "progress", "completed"})
RETRYABLE_SEND_REASONS = frozenset({"transport_not_connected_before_post",
                                   "qq_http_rejected_408", "qq_http_rejected_429"})
_LABELS = {"received": "收到", "progress": "还在做", "completed": "做完了"}


def _json(value: dict) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def render_notice(row: dict) -> str:
    """One bounded text message; artifact paths are supplied as approved public text."""
    executor = f" · {row['executor']}" if row.get("executor") else ""
    return f"[{row['task_id']}] {_LABELS[row['kind']]}{executor}\n{row['text']}".rstrip()


class TaskOutbox:
    """SQLite outbox with durable idempotency and a conservative ambiguous-send state.

    Caller supplies a persistent, profile-scoped path. Sending is never done while
    holding a SQLite transaction. Leases protect concurrent senders; an expired send
    becomes unknown rather than pending because it might already have reached QQ.
    """

    def __init__(self, path: str | Path, *, clock: Callable[[], float] = time.time):
        self.path = Path(path).resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.clock = clock
        with self._connection() as conn:
            conn.execute("PRAGMA journal_mode=WAL")
            conn.executescript("""
                CREATE TABLE IF NOT EXISTS task_outbox (
                    id INTEGER PRIMARY KEY, author TEXT NOT NULL,
                    task_id TEXT NOT NULL, kind TEXT NOT NULL,
                    event_key TEXT NOT NULL, group_id TEXT NOT NULL,
                    profile TEXT NOT NULL, executor TEXT NOT NULL,
                    text TEXT NOT NULL, metadata TEXT NOT NULL,
                    payload_hash TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    attempts INTEGER NOT NULL DEFAULT 0,
                    claim_token TEXT, lease_until REAL,
                    message_id TEXT, reason TEXT NOT NULL DEFAULT '',
                    created_at REAL NOT NULL, updated_at REAL NOT NULL,
                    UNIQUE(group_id, profile, task_id, kind, event_key),
                    CHECK(kind IN ('received','progress','completed')),
                    CHECK(status IN ('pending','sending','sent','failed','unknown'))
                );
                CREATE INDEX IF NOT EXISTS task_outbox_status ON task_outbox(status,id);
                CREATE INDEX IF NOT EXISTS task_outbox_source_receipt ON task_outbox(
                    group_id, profile, kind,
                    CAST(json_extract(metadata,'$.source_message_id') AS TEXT),
                    CAST(json_extract(metadata,'$.source_profile') AS TEXT), id);
                CREATE TABLE IF NOT EXISTS task_outbox_events (
                    id INTEGER PRIMARY KEY, notice_id INTEGER NOT NULL,
                    author TEXT NOT NULL, action TEXT NOT NULL,
                    created_at REAL NOT NULL, details TEXT NOT NULL,
                    FOREIGN KEY(notice_id) REFERENCES task_outbox(id)
                );
                CREATE TABLE IF NOT EXISTS task_outbox_sources (
                    notice_id INTEGER NOT NULL, author TEXT NOT NULL,
                    group_id TEXT NOT NULL, profile TEXT NOT NULL,
                    source_message_id TEXT NOT NULL, source_profile TEXT NOT NULL,
                    created_at REAL NOT NULL,
                    PRIMARY KEY(notice_id,group_id,profile,source_message_id,source_profile),
                    FOREIGN KEY(notice_id) REFERENCES task_outbox(id)
                );
                CREATE INDEX IF NOT EXISTS task_outbox_sources_route ON task_outbox_sources(
                    group_id,profile,source_message_id,source_profile,notice_id);
            """)

    @contextmanager
    def _connection(self):
        conn = sqlite3.connect(self.path, timeout=2, isolation_level=None)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute("PRAGMA busy_timeout=2000")
        conn.execute("PRAGMA synchronous=FULL")
        try:
            yield conn
        finally:
            conn.close()

    @contextmanager
    def _write(self):
        with self._connection() as conn:
            conn.execute("BEGIN IMMEDIATE")
            try:
                yield conn
                conn.execute("COMMIT")
            except BaseException:
                conn.execute("ROLLBACK")
                raise

    def _audit(self, conn, notice_id: int, action: str, **details):
        conn.execute("INSERT INTO task_outbox_events "
                     "(notice_id,author,action,created_at,details) VALUES (?,?,?,?,?)",
                     (notice_id, AUTHOR, action, self.clock(), _json(details)))

    @staticmethod
    def _row(row) -> dict | None:
        if row is None:
            return None
        value = dict(row)
        value["metadata"] = json.loads(value["metadata"])
        return value

    def enqueue(self, *, task_id: str, kind: str, event_key: str, group_id: str,
                profile: str, executor: str = "", text: str = "",
                metadata: dict | None = None, progress_interval: float = 600) -> dict:
        """Persist one public notice and return it. event_key must survive replay.

        received/completed keys identify a task revision, never a callback UUID.
        Progress is coalesced to the last notice within the requested interval.
        A changed payload under the same key is rejected instead of silently sent.
        """
        if kind not in PUBLIC_KINDS:
            raise ValueError("Only received, progress and completed may be public notices")
        if not all(str(value).strip() for value in (task_id, event_key, group_id, profile)):
            raise ValueError("Task, event, group and profile are required")
        values = {"task_id": str(task_id), "kind": kind, "event_key": str(event_key),
                  "group_id": str(group_id), "profile": str(profile),
                  "executor": str(executor), "text": str(text), "metadata": metadata or {}}
        if len(render_notice(values).encode("utf-16-le")) // 2 > 3500:
            raise ValueError("Task notice exceeds one QQ message; summarize results before enqueue")
        fingerprint = hashlib.sha256(_json(values).encode("utf-8")).hexdigest()
        now = self.clock()
        with self._write() as conn:
            old = conn.execute("SELECT * FROM task_outbox WHERE group_id=? AND profile=? "
                               "AND task_id=? AND kind=? AND event_key=?",
                               (group_id, profile, task_id, kind, event_key)).fetchone()
            if old is not None:
                if old["payload_hash"] != fingerprint:
                    raise ValueError("Outbox idempotency key already has a different payload")
                return self._row(old)
            if kind == "progress" and progress_interval > 0:
                old = conn.execute("SELECT * FROM task_outbox WHERE group_id=? AND profile=? "
                                   "AND task_id=? AND kind='progress' AND created_at>? "
                                   "ORDER BY id DESC LIMIT 1",
                                   (group_id, profile, task_id, now-progress_interval)).fetchone()
                if old is not None:
                    self._audit(conn, old["id"], "progress_coalesced", event_key=event_key)
                    return self._row(old)
            cursor = conn.execute("INSERT INTO task_outbox "
                                  "(author,task_id,kind,event_key,group_id,profile,executor,text,metadata,"
                                  "payload_hash,created_at,updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
                                  (AUTHOR, task_id, kind, event_key, group_id, profile, executor,
                                   text, _json(metadata or {}), fingerprint, now, now))
            notice_id = cursor.lastrowid
            self._audit(conn, notice_id, "enqueued", kind=kind)
            return self._row(conn.execute("SELECT * FROM task_outbox WHERE id=?", (notice_id,)).fetchone())

    def get(self, notice_id: int) -> dict:
        with self._connection() as conn:
            row = self._row(conn.execute("SELECT * FROM task_outbox WHERE id=?", (notice_id,)).fetchone())
        if row is None:
            raise KeyError(notice_id)
        return row

    def events(self, notice_id: int) -> list[dict]:
        with self._connection() as conn:
            return [{**dict(row), "details": json.loads(row["details"])} for row in conn.execute(
                "SELECT * FROM task_outbox_events WHERE notice_id=? ORDER BY id", (notice_id,))]

    def pending(self, *, limit: int = 20, group_id: str | None = None,
                profile: str | None = None) -> list[dict]:
        filters, parameters = ["status IN ('pending','unknown','sending')"], []
        for field, value in (("group_id", group_id), ("profile", profile)):
            if value is not None:
                filters.append(f"{field}=?")
                parameters.append(str(value))
        with self._connection() as conn:
            return [self._row(row) for row in conn.execute(
                "SELECT * FROM task_outbox WHERE " + " AND ".join(filters) + " "
                "ORDER BY CASE status WHEN 'pending' THEN 0 WHEN 'sending' THEN 1 ELSE 2 END,id "
                "LIMIT ?", (*parameters, max(1, min(int(limit), 100))))]

    def latest(self, *, task_id: str, kind: str, group_id: str, profile: str) -> dict | None:
        with self._connection() as conn:
            return self._row(conn.execute("SELECT * FROM task_outbox WHERE task_id=? AND kind=? "
                "AND group_id=? AND profile=? ORDER BY id DESC LIMIT 1",
                (task_id, kind, group_id, profile)).fetchone())

    def received_for_message(self, message_id: str, *, group_id: str, profile: str,
                             source_profile: str, limit: int = 30) -> list[dict]:
        """Find only genuine admission receipts for one exact source message/route.

        Every delivery state stays associated: an unknown or in-flight receipt
        must not let the supervisor bypass the outbox with a second plain send.
        """
        if not all(str(value or '').strip() for value in (message_id, group_id, profile, source_profile)):
            return []
        with self._connection() as conn:
            return [self._row(row) for row in conn.execute(
                "SELECT o.* FROM task_outbox o WHERE o.group_id=? AND o.profile=? AND o.kind='received' "
                "AND ((CAST(json_extract(o.metadata,'$.source_message_id') AS TEXT)=? "
                "AND CAST(json_extract(o.metadata,'$.source_profile') AS TEXT)=?) OR EXISTS ("
                "SELECT 1 FROM task_outbox_sources s WHERE s.notice_id=o.id AND s.group_id=o.group_id "
                "AND s.profile=o.profile AND s.source_message_id=? AND s.source_profile=?)) "
                "ORDER BY o.id LIMIT ?",
                (str(group_id), str(profile), str(message_id), str(source_profile), str(message_id), str(source_profile),
                 max(1, min(int(limit), 100))))]

    def link_received_source(self, notice_id: int, *, message_id: str, group_id: str,
                             profile: str, source_profile: str) -> bool:
        """Associate a replay's real source with a canonical receipt without changing it."""
        if not all(str(value or '').strip() for value in (message_id, source_profile)):
            return False
        with self._write() as conn:
            row = conn.execute('SELECT kind,group_id,profile FROM task_outbox WHERE id=?', (notice_id,)).fetchone()
            if row is None:
                raise KeyError(notice_id)
            if (row['kind'] != 'received' or row['group_id'] != str(group_id) or row['profile'] != str(profile)):
                raise ValueError('Source link must use the canonical received notice route')
            cursor = conn.execute('INSERT OR IGNORE INTO task_outbox_sources '
                '(notice_id,author,group_id,profile,source_message_id,source_profile,created_at) VALUES(?,?,?,?,?,?,?)',
                (notice_id, AUTHOR, str(group_id), str(profile), str(message_id), str(source_profile), self.clock()))
            if cursor.rowcount:
                self._audit(conn, notice_id, 'source_linked', source_message_id=str(message_id),
                            source_profile=str(source_profile))
            return bool(cursor.rowcount)

    def claim(self, notice_id: int, *, lease_seconds: float = 90) -> dict | None:
        if lease_seconds <= 0:
            raise ValueError("Positive send lease required")
        with self._write() as conn:
            row = conn.execute("SELECT * FROM task_outbox WHERE id=?", (notice_id,)).fetchone()
            if row is None:
                raise KeyError(notice_id)
            now = self.clock()
            if row["status"] == "sending" and row["lease_until"] <= now:
                conn.execute("UPDATE task_outbox SET status='unknown', updated_at=?, "
                             "reason='send_lease_expired' WHERE id=?", (now, notice_id))
                self._audit(conn, notice_id, "unknown", reason="send_lease_expired")
                return None
            if row["status"] != "pending":
                return None
            token = uuid.uuid4().hex
            conn.execute("UPDATE task_outbox SET status='sending',claim_token=?,lease_until=?,"
                         "attempts=attempts+1,updated_at=?,reason='' WHERE id=?",
                         (token, now+lease_seconds, now, notice_id))
            self._audit(conn, notice_id, "claimed", token=token)
            return self._row(conn.execute("SELECT * FROM task_outbox WHERE id=?", (notice_id,)).fetchone())

    def settle(self, notice_id: int, claim_token: str, *, state: str,
               message_id: str = "", reason: str = "") -> bool:
        if state not in {"sent", "failed", "unknown"}:
            raise ValueError("Invalid send outcome")
        if state == "sent" and not message_id:
            raise ValueError("Sent requires a confirmed remote message id")
        with self._write() as conn:
            cursor = conn.execute("UPDATE task_outbox SET status=?,message_id=?,reason=?,"
                                  "updated_at=?,lease_until=NULL WHERE id=? AND claim_token=? "
                                  "AND status IN ('sending','unknown')",
                                  (state, message_id or None, reason, self.clock(), notice_id, claim_token))
            if cursor.rowcount:
                self._audit(conn, notice_id, state, message_id=message_id, reason=reason)
            return bool(cursor.rowcount)

    def resolve_unknown(self, notice_id: int, *, state: str, evidence: str,
                        message_id: str = "") -> dict:
        """Only an authoritative lookup or explicit operator observation may resolve it."""
        if state not in {"sent", "failed"} or not evidence.strip():
            raise ValueError("Resolution requires sent/failed and evidence")
        if state == "sent" and not message_id:
            raise ValueError("Sent resolution requires remote message id")
        with self._write() as conn:
            row = conn.execute("SELECT status FROM task_outbox WHERE id=?", (notice_id,)).fetchone()
            if row is None:
                raise KeyError(notice_id)
            if row["status"] != "unknown":
                raise ValueError("Only unknown sends may be reconciled")
            conn.execute("UPDATE task_outbox SET status=?,message_id=?,reason=?,claim_token=NULL,"
                         "lease_until=NULL,updated_at=? WHERE id=?",
                         (state, message_id or None, evidence, self.clock(), notice_id))
            self._audit(conn, notice_id, "reconciled", state=state, evidence=evidence, message_id=message_id)
        return self.get(notice_id)

    def retry(self, notice_id: int, *, reason: str) -> dict:
        if not reason.strip():
            raise ValueError("Retry reason is required")
        with self._write() as conn:
            row = conn.execute("SELECT status FROM task_outbox WHERE id=?", (notice_id,)).fetchone()
            if row is None:
                raise KeyError(notice_id)
            if row["status"] == "sent":
                return self.get(notice_id)
            if row["status"] != "failed":
                raise ValueError("Retry requires a definite failure; reconcile unknown sends first")
            conn.execute("UPDATE task_outbox SET status='pending',claim_token=NULL,lease_until=NULL,"
                         "reason=?,updated_at=? WHERE id=?", (reason, self.clock(), notice_id))
            self._audit(conn, notice_id, "retry_requested", reason=reason)
        return self.get(notice_id)

    def retry_due(self, *, group_id: str, profile: str, max_attempts: int = 3,
                  base_delay: float = 30) -> list[dict]:
        """Requeue only definite, temporary non-deliveries after bounded backoff.

        Automatic attempts total at most three. Explicit operator retry remains
        available after repair; an unknown send never enters this transition.
        """
        if not str(group_id).strip() or not str(profile).strip() or base_delay <= 0:
            raise ValueError("Retry requires a route and positive backoff")
        maximum = max(1, min(int(max_attempts), 3))
        now, retried = self.clock(), []
        reasons = sorted(RETRYABLE_SEND_REASONS)
        marks = ','.join('?' for _ in reasons)
        with self._write() as conn:
            rows = conn.execute("SELECT * FROM task_outbox WHERE group_id=? AND profile=? "
                f"AND status='failed' AND reason IN ({marks}) AND attempts>=1 AND attempts<? "
                "ORDER BY updated_at,id LIMIT 100", (str(group_id), str(profile), *reasons, maximum)).fetchall()
            for row in rows:
                delay = float(base_delay) * 2 ** (row['attempts']-1)
                if now - row['updated_at'] < delay:
                    continue
                conn.execute("UPDATE task_outbox SET status='pending',claim_token=NULL,lease_until=NULL,"
                             "updated_at=? WHERE id=?", (now, row['id']))
                self._audit(conn, row['id'], 'automatic_retry_requested', reason=row['reason'],
                            previous_attempts=row['attempts'], max_attempts=maximum, backoff_seconds=delay)
                retried.append(self._row(conn.execute('SELECT * FROM task_outbox WHERE id=?', (row['id'],)).fetchone()))
        return retried

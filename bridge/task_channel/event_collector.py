# Author: Codex, 2026-10-19.
"""Collect durable native task events without waiting for a supervisor LLM turn."""
from __future__ import annotations

from collections.abc import Mapping
from contextlib import closing, contextmanager
import json
from pathlib import Path
import re
import sqlite3
import time


AUTHOR = 'Codex, 2026-10-19'
WAKE_KINDS = frozenset({'completed', 'gave_up', 'crashed', 'timed_out', 'blocked',
                        'review_requested', 'changes_requested', 'block_loop_detected'})
RETRY_DIAGNOSTICS = frozenset({'crashed', 'timed_out', 'gave_up'})


class EventCollector:
    """One exact board/QQ route, a separate cursor DB, and the existing outbox.

    ``resolver.notice_payload(task,event)`` is the sole public payload builder.
    A cursor transaction serializes collectors; it never owns an outbox write
    transaction. Enqueue commits before the cursor, so interrupted batches replay
    through the outbox's stable event key rather than losing completion notices.
    """

    def __init__(self, root, board, group_id, profile, outbox, resolver, since=None, *,
                 clock=time.time, diagnostic_max_age=60):
        self.root = Path(root).resolve()
        if not re.fullmatch(r'[a-z0-9][a-z0-9_-]{0,63}', str(board)):
            raise ValueError('An exact board slug is required')
        if not str(group_id).strip() or not str(profile).strip():
            raise ValueError('An exact QQ route is required')
        self.board, self.group_id, self.profile = str(board), str(group_id), str(profile)
        self.outbox, self.resolver = outbox, resolver
        self.clock, self.diagnostic_max_age = clock, max(0, float(diagnostic_max_age))
        self.board_db = (self.root/'kanban.db' if self.board == 'default' else
                         self.root/'kanban'/'boards'/self.board/'kanban.db').resolve()
        if not self.board_db.is_relative_to(self.root):
            raise ValueError('Board escapes the configured root')
        self.state_db = self.root/'task-channel'/'events.sqlite3'
        self.since = float(since) if since is not None else (self.root/'task-channel'/'runtime.json').stat().st_mtime
        if self.since < 0:
            raise ValueError('Collection start time must not be negative')
        self.last_result = {}

    @contextmanager
    def _cursor(self):
        self.state_db.parent.mkdir(parents=True, exist_ok=True)
        with closing(sqlite3.connect(self.state_db, timeout=2, isolation_level=None)) as conn:
            conn.row_factory = sqlite3.Row
            conn.execute('PRAGMA busy_timeout=2000')
            conn.execute('CREATE TABLE IF NOT EXISTS event_cursors ('
                'board TEXT NOT NULL,group_id TEXT NOT NULL,profile TEXT NOT NULL,author TEXT NOT NULL,'
                'last_event_id INTEGER NOT NULL,since REAL NOT NULL,updated_at REAL NOT NULL,'
                'PRIMARY KEY(board,group_id,profile))')
            conn.execute('BEGIN IMMEDIATE')
            try:
                conn.execute('INSERT OR IGNORE INTO event_cursors VALUES(?,?,?,?,?,?,?)',
                    (self.board, self.group_id, self.profile, AUTHOR, 0, self.since, time.time()))
                yield conn
                conn.execute('COMMIT')
            except BaseException:
                conn.execute('ROLLBACK')
                raise

    def _read_batch(self, after, since, limit):
        with closing(sqlite3.connect(self.board_db.as_uri()+'?mode=ro', uri=True, timeout=2)) as conn:
            conn.row_factory = sqlite3.Row
            conn.execute('PRAGMA query_only=ON')
            conn.execute('BEGIN')
            events = [dict(row) for row in conn.execute('SELECT * FROM task_events WHERE id>? '
                'AND created_at>=? ORDER BY id LIMIT ?', (after, since, limit))]
            # If only old/imported rows remain, advance through that same read snapshot.
            end = events[-1]['id'] if events else conn.execute('SELECT COALESCE(MAX(id),0) FROM task_events').fetchone()[0]
            task_ids = sorted({row['task_id'] for row in events if row['kind'] in WAKE_KINDS})
            if not task_ids:
                return events, {}, set(), {}, max(after, end)
            marks = ','.join('?' for _ in task_ids)
            tasks = {row['id']: dict(row) for row in conn.execute(
                f'SELECT * FROM tasks WHERE id IN ({marks})', task_ids)}
            subscriptions = [dict(row) for row in conn.execute(
                f'SELECT * FROM kanban_notify_subs WHERE task_id IN ({marks}) AND platform=? '
                'AND chat_id=? AND notifier_profile=? AND delivery_mode IN (?,?)',
                (*task_ids, 'qqbot', self.group_id, self.profile, 'wake', 'notify+wake'))]
            activity = {row['task_id']: row['last_activity'] for row in conn.execute(
                f'SELECT task_id,MAX(id) AS last_activity FROM task_events WHERE task_id IN ({marks}) '
                "AND kind IN ('claimed','spawned','worker_bound','run_started') GROUP BY task_id", task_ids)}
        owned = set()
        for sub in subscriptions:
            metadata = sub.get('delivery_metadata') or {}
            if isinstance(metadata, str):
                metadata = json.loads(metadata)
            if not isinstance(metadata, Mapping):
                continue
            if (sub.get('chat_type') or metadata.get('chat_type')) == 'group':
                owned.add(sub['task_id'])
        return events, tasks, owned, activity, max(after, end)

    def _already_queued(self, args):
        # This is a read-only lookup, closed before enqueue opens its own transaction.
        with closing(sqlite3.connect(Path(self.outbox.path).resolve().as_uri()+'?mode=ro', uri=True, timeout=2)) as conn:
            return conn.execute('SELECT 1 FROM task_outbox WHERE group_id=? AND profile=? '
                'AND task_id=? AND kind=? AND event_key=? LIMIT 1',
                tuple(args[key] for key in ('group_id', 'profile', 'task_id', 'kind', 'event_key'))).fetchone() is not None

    def collect(self, limit=100):
        stats = dict(seen=0, enqueued=0, replayed=0, skipped=0, coalesced=0, cursor=0,
                     stale_run_skipped=0, old_diagnostic_skipped=0, stale_state_skipped=0)
        if not self.board_db.is_file():
            self.last_result = {**stats, 'outcome': 'missing_board'}
            return 0
        with self._cursor() as cursor:
            state = cursor.execute('SELECT last_event_id,since FROM event_cursors '
                'WHERE board=? AND group_id=? AND profile=?', (self.board, self.group_id, self.profile)).fetchone()
            events, tasks, owned, activity, end = self._read_batch(state['last_event_id'], state['since'],
                                                                  max(1, min(int(limit), 100)))
            stats.update(seen=len(events), cursor=end, since=state['since'])
            latest = {}
            for event in events:
                if event['kind'] not in WAKE_KINDS:
                    stats['skipped'] += 1
                    continue
                if event['task_id'] in latest:
                    stats['coalesced'] += 1
                latest[event['task_id']] = event
            for event in sorted(latest.values(), key=lambda row: row['id']):
                task = tasks.get(event['task_id'])
                if task is None or event['task_id'] not in owned:
                    stats['skipped'] += 1
                    continue
                stale_retry = event['kind'] in RETRY_DIAGNOSTICS and task.get('status') in {'done', 'archived'}
                blocked_states = {'blocked'} if event['kind'] == 'blocked' else {'blocked', 'triage'}
                stale_block = event['kind'] in {'blocked', 'block_loop_detected'} and task.get('status') not in blocked_states
                if stale_retry or stale_block:
                    stats['stale_state_skipped'] += 1
                    continue
                if event['kind'] in RETRY_DIAGNOSTICS:
                    current_run, previous_run = task.get('current_run_id'), event.get('run_id')
                    newer_run = current_run is not None and str(current_run) != str(previous_run)
                    if (task.get('status') == 'running'
                            and (newer_run or activity.get(task['id'], 0) > event['id'])):
                        stats['stale_run_skipped'] += 1
                        continue
                    if self.clock()-float(event['created_at']) > self.diagnostic_max_age:
                        stats['old_diagnostic_skipped'] += 1
                        continue
                event['payload'] = json.loads(event['payload']) if event.get('payload') else {}
                args = self.resolver.notice_payload(task, event)
                if (args.get('group_id') != self.group_id or args.get('profile') != self.profile
                        or args.get('task_id') != task['id']):
                    raise ValueError('Resolver notice payload escaped the collected task route')
                existing = self._already_queued(args)
                self.outbox.enqueue(**args)
                stats['replayed' if existing else 'enqueued'] += 1
            cursor.execute('UPDATE event_cursors SET last_event_id=?,updated_at=? '
                'WHERE board=? AND group_id=? AND profile=?',
                (end, time.time(), self.board, self.group_id, self.profile))
        self.last_result = {**stats, 'outcome': 'ok'}
        return stats['enqueued']

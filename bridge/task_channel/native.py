# Author: Codex, 2026-10-19.
"""Lazy native board calls; importing this module never imports hermes_bootstrap."""
from __future__ import annotations

from contextlib import contextmanager
import json
import logging
from pathlib import Path
import sqlite3
import sys


logger = logging.getLogger(__name__)


def install_source(root):
    source = str(Path(root) / 'hermes-agent')
    if source not in sys.path:
        sys.path.insert(0, source)


@contextmanager
def board_connection(root, board):
    install_source(root)
    from hermes_cli import kanban_db as kb
    from hermes_cli import kanban_db_connect as kbc
    from .dispatch import board_path
    conn = kbc.connect(db_path=board_path(root, board), board=board)
    try:
        yield kb, conn
    finally:
        conn.close()


def normalize_group_subscriptions(root, board, task_id, settings):
    """Only this physical QQ group's five openids share the canonical supervisor."""
    install_source(root)
    from hermes_cli import kanban_db_notify as notify
    with board_connection(root, board) as (_, conn):
        groups = set(settings.get('group_chat_ids', {}).values())
        destination = settings['delivery_group_id']
        groups.add(destination)
        subscriptions = notify.list_notify_subs(conn, task_id)
        anchor = next((sub for sub in subscriptions if sub['platform'] == 'qqbot'
                       and sub['chat_id'] == destination and sub.get('notifier_profile') == 'doloris'
                       and sub.get('chat_type') == 'group'), None)
        if anchor is None:
            anchor = conn.execute("SELECT user_id,user_id_alt,thread_id,delivery_metadata FROM kanban_notify_subs "
                              "WHERE platform='qqbot' AND chat_id=? AND notifier_profile='doloris' "
                              "AND chat_type='group' ORDER BY created_at DESC LIMIT 1", (destination,)).fetchone()
        raw_routing = anchor['delivery_metadata'] if anchor else {}
        if isinstance(raw_routing, str):
            try:
                raw_routing = json.loads(raw_routing)
            except ValueError:
                raw_routing = {}
        routing = raw_routing if isinstance(raw_routing, dict) else {}
        thread_id = anchor['thread_id'] if anchor else None
        had_canonical = any(sub['platform'] == 'qqbot' and sub['chat_id'] == destination
                            and sub.get('notifier_profile') == 'doloris'
                            and sub.get('chat_type') == 'group'
                            and sub.get('delivery_mode') in {'wake', 'notify+wake'}
                            and str(sub.get('thread_id') or '') == str(thread_id or '')
                            for sub in subscriptions)
        for sub in subscriptions:
            wrong_target = sub['chat_id'] != destination
            wrong_owner = sub.get('notifier_profile') != 'doloris' or sub.get('chat_type') != 'group'
            if (sub['platform'] == 'qqbot' and sub['chat_id'] in groups
                    and _is_group_subscription(sub) and (wrong_target or wrong_owner)):
                notify.remove_notify_sub(conn, task_id=task_id, platform='qqbot',
                                         chat_id=sub['chat_id'], thread_id=sub.get('thread_id'))
        notify.add_notify_sub(conn, task_id=task_id, platform='qqbot', chat_id=destination,
                              chat_type='group', notifier_profile='doloris', delivery_mode='wake',
                              user_id=anchor['user_id'] if anchor else None,
                              user_id_alt=anchor['user_id_alt'] if anchor else None,
                              thread_id=thread_id,
                              delivery_metadata={**routing, 'chat_type': 'group', 'author': 'Codex, 2026-10-19',
                                                 'task_channel': True, 'board': board})
        if not had_canonical:
            _restore_terminal_wake(conn, notify, task_id, destination, thread_id)


def _restore_terminal_wake(conn, notify, task_id, destination, thread_id):
    """A repaired subscription must still see an already committed terminal event."""
    task = conn.execute('SELECT status FROM tasks WHERE id=?', (task_id,)).fetchone()
    kind = {'done': 'completed', 'blocked': 'blocked'}.get(task['status'] if task else None)
    if kind is None:
        return
    terminal = conn.execute('SELECT id FROM task_events WHERE task_id=? AND kind=? ORDER BY id DESC LIMIT 1',
                            (task_id, kind)).fetchone()
    if terminal is None:
        return
    sub = next((row for row in notify.list_notify_subs(conn, task_id)
                if row['platform'] == 'qqbot' and row['chat_id'] == destination
                and str(row.get('thread_id') or '') == str(thread_id or '')), None)
    if sub is not None and sub['last_event_id'] >= terminal['id']:
        notify.rewind_notify_cursor(conn, task_id=task_id, platform='qqbot', chat_id=destination,
                                    thread_id=thread_id, claimed_cursor=sub['last_event_id'],
                                    old_cursor=terminal['id'] - 1)


def _is_group_subscription(sub):
    metadata = sub.get('delivery_metadata') or {}
    if isinstance(metadata, str):
        try:
            metadata = json.loads(metadata)
        except ValueError:
            metadata = {}
    legacy_type = metadata.get('chat_type') if isinstance(metadata, dict) else None
    return (sub.get('chat_type') or legacy_type) == 'group'


def repair_active_group_subscriptions(root, board, settings):
    """Normalize only this physical group's active cards; correct routes stay read-only.

    Terminal/history cards and cards subscribed solely to another group or a
    private chat are outside this sweep. Return the ids actually repaired.
    """
    from .dispatch import board_path
    path = board_path(root, board).resolve()
    if not path.is_file():
        return []
    destination = str(settings['delivery_group_id'])
    groups = {str(value) for value in settings.get('group_chat_ids', {}).values() if value}
    groups.add(destination)
    conn = sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=2)
    conn.row_factory = sqlite3.Row
    try:
        conn.execute('PRAGMA query_only=ON')
        marks = ','.join('?' for _ in groups)
        rows = conn.execute(
            "SELECT s.* FROM kanban_notify_subs s JOIN tasks t ON t.id=s.task_id "
            "WHERE t.status IN ('ready','running','review') AND s.platform='qqbot' "
            f'AND s.chat_id IN ({marks}) ORDER BY s.task_id,s.chat_id,s.thread_id',
            tuple(sorted(groups))).fetchall()
    finally:
        conn.close()
    tasks = {}
    for row in rows:
        sub = dict(row)
        tasks.setdefault(sub['task_id'], []).append(sub)
    repaired = []
    for task_id, subscriptions in tasks.items():
        group_subs = [sub for sub in subscriptions if _is_group_subscription(sub)]
        if not group_subs:
            continue
        canonical = [sub for sub in group_subs if sub['chat_id'] == destination
                     and sub.get('notifier_profile') == 'doloris' and sub.get('delivery_mode') == 'wake']
        wrong = any(sub['chat_id'] != destination or sub.get('notifier_profile') != 'doloris'
                    or sub.get('delivery_mode') != 'wake' for sub in group_subs)
        if canonical and not wrong:
            continue
        if any(sub['chat_id'] == destination and not _is_group_subscription(sub) for sub in subscriptions):
            # The native subscription key omits chat_type. Preserve a private
            # row rather than silently replacing it with a group subscription.
            logger.warning('task-channel active route repair skipped task=%s: canonical key belongs to a private chat', task_id)
            continue
        normalize_group_subscriptions(root, board, task_id, settings)
        repaired.append(task_id)
    return repaired


def create_card(root, board, args, *, original=None, settings=None):
    """Used by the external entry and by follow-ups after native comment authorization."""
    mapping = {'model': 'model_override', 'provider': 'provider_override', 'project': 'project_id'}
    fields = ('title', 'body', 'assignee', 'priority', 'workspace_kind', 'workspace_path',
              'branch_name', 'tenant', 'parents', 'triage', 'idempotency_key', 'max_runtime_seconds',
              'skills', 'max_retries', 'model', 'provider', 'reasoning_effort', 'goal_mode',
              'goal_max_turns', 'initial_status', 'session_id', 'project', 'completion_contract')
    values = {mapping.get(key, key): args[key] for key in fields if key in args and args[key] is not None}
    if original:
        values['creator_task_id'] = original['id']
    values['created_by'] = 'task-channel'
    with board_connection(root, board) as (kb, conn):
        tid = kb.create_task(conn, board=board, **values)
        task = kb.get_task(conn, tid)
        result = {'ok': True, 'task_id': tid, 'assignee': task.assignee, 'status': task.status}
    if settings:
        normalize_group_subscriptions(root, board, tid, settings)
    return result


def add_followup_link(root, board, task_id, followup_id):
    marker = f'[task-channel-followup:{followup_id}]'
    with board_connection(root, board) as (kb, conn):
        if not any(marker in item.body for item in kb.list_comments(conn, task_id)):
            kb.add_comment(conn, task_id, author='task-channel', body=f'{marker} 追加要求已建跟进任务 {followup_id}。')


def reactivate_card(root, board, task_id, *, assignee=None):
    with board_connection(root, board) as (kb, conn):
        if assignee is not None and not kb.assign_task(conn, task_id, assignee):
            return False
        return kb.unblock_task(conn, task_id)


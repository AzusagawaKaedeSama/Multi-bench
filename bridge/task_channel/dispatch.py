# Author: Codex, 2026-10-19.
"""One durable, serialized admission path around the existing Kanban handlers."""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import inspect
import json
from pathlib import Path
import re
import sqlite3
import time
from typing import Callable

AUTHOR = 'Codex, 2026-10-19'
PROFILES = ('qq-deepseek', 'default', 'uika', 'misumi', 'doloris')
CAPABILITIES = {
    'qq-deepseek': {'general', 'research', 'script', 'files', 'visual'},
    'default': {'general', 'research', 'script', 'files', 'visual', 'creation'},
    'uika': {'general', 'writing', 'story'},
    'misumi': {'general', 'research', 'bilibili', 'review', 'humor'},
    'doloris': {'general', 'research', 'script', 'files', 'visual', 'review', 'coordination'},
}


class AdmissionError(ValueError):
    """A request is recorded but cannot enter a worker queue yet."""


def _json(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'))


def _decoded(value):
    if isinstance(value, str):
        try:
            return json.loads(value)
        except ValueError:
            return {'ok': False, 'error': value[:2000]}
    return dict(value) if isinstance(value, dict) else {'ok': False, 'error': 'Invalid native result'}


def _scope_body(value):
    body = str(value or '')
    # The coordinator appends a fresh conversation snapshot after the user scope.
    # A replay with a newer snapshot still identifies the same requested work.
    for marker in ('\n\n共享群背景（只用于理解任务，执行范围以本任务卡为准）\n',
                   '\n\n共享群背景：先读取 `'):
        body = body.split(marker, 1)[0]
    return body


def _intent(args):
    # Routing/model choices can change between retries; they do not identify the work.
    return {'title': str(args.get('title') or '').strip(), 'body': _scope_body(args.get('body')),
            'workspace_kind': args.get('workspace_kind'), 'workspace_path': args.get('workspace_path'),
            'project': args.get('project', args.get('project_id')),
            'parents': sorted(set(args.get('parents') or [])),
            'completion_contract': args.get('completion_contract')}


def _intent_hash(args):
    return hashlib.sha256(_json(_intent(args)).encode()).hexdigest()


def dispatch_key(args, source):
    explicit = str(args.get('idempotency_key') or '').strip()
    if explicit:
        return explicit
    message = str(source.get('message_id') or '')
    if not message:
        raise AdmissionError('当前调用缺少来源消息编号，请提供稳定的 idempotency_key 后派单。')
    identity = {key: source.get(key, '') for key in ('profile', 'platform', 'chat_id', 'thread_id', 'message_id')}
    return 'tc-' + hashlib.sha256(_json([identity, _intent(args)]).encode()).hexdigest()[:48]


def board_path(root, board):
    if not re.fullmatch(r'[a-zA-Z0-9_-]{1,100}', board):
        raise ValueError('Invalid board name')
    return Path(root) / 'kanban.db' if board == 'default' else Path(root) / 'kanban' / 'boards' / board / 'kanban.db'


class DispatchService:
    def __init__(self, root, board, *, capabilities=None):
        self.root, self.board = Path(root), board
        self.board_db = board_path(root, board)
        self.state_db = self.root / 'task-channel' / 'dispatch.sqlite3'
        self.capabilities = capabilities or CAPABILITIES

    @contextmanager
    def _locked(self):
        self.state_db.parent.mkdir(parents=True, exist_ok=True)
        db = sqlite3.connect(self.state_db, timeout=15, isolation_level=None)
        db.row_factory = sqlite3.Row
        try:
            db.execute('PRAGMA busy_timeout=15000')
            db.execute('CREATE TABLE IF NOT EXISTS dispatches ('
                       'board TEXT NOT NULL, dispatch_key TEXT NOT NULL, task_id TEXT, '
                       'assignee TEXT, status TEXT NOT NULL, receipt TEXT NOT NULL, '
                       'intent_hash TEXT NOT NULL, source TEXT NOT NULL, created_at REAL NOT NULL, '
                       'PRIMARY KEY(board,dispatch_key))')
            db.execute('CREATE INDEX IF NOT EXISTS dispatch_task ON dispatches(board,task_id)')
            db.execute('BEGIN IMMEDIATE')
            yield db
            db.execute('COMMIT')
        except BaseException:
            if db.in_transaction:
                db.execute('ROLLBACK')
            raise
        finally:
            db.close()

    @contextmanager
    def _board_read(self):
        if not self.board_db.is_file():
            raise AdmissionError('指定看板不存在，派单已停止。')
        db = sqlite3.connect(self.board_db.resolve().as_uri() + '?mode=ro', uri=True, timeout=5)
        db.row_factory = sqlite3.Row
        db.execute('PRAGMA query_only=ON')
        try:
            yield db
        finally:
            db.close()

    def task(self, task_id):
        with self._board_read() as db:
            row = db.execute('SELECT * FROM tasks WHERE id=?', (task_id,)).fetchone()
            return dict(row) if row else None

    def _existing(self, key):
        with self._board_read() as db:
            row = db.execute('SELECT * FROM tasks WHERE idempotency_key=? '
                             'ORDER BY created_at DESC LIMIT 1', (key,)).fetchone()
            return dict(row) if row else None

    def loads(self):
        result = {name: {'running': 0, 'ready': 0} for name in self.capabilities}
        with self._board_read() as db:
            for row in db.execute("SELECT assignee,status,COUNT(*) n FROM tasks "
                                  "WHERE status IN ('ready','running') GROUP BY assignee,status"):
                if row['assignee'] in result:
                    result[row['assignee']][row['status']] = row['n']
        return result

    def _required(self, args):
        text = str(args.get('title') or '') + ' ' + _scope_body(args.get('body'))[:1200]
        if re.search(r'童话|小说|剧情|故事设定', text, re.I):
            return {'writing'}
        if re.search(r'B\s*站收藏|bilibili.{0,8}(?:收藏|favorites)', text, re.I):
            return {'bilibili'}
        if re.search(r'荤段子|成人幽默', text):
            return {'humor'}
        if re.search(r'图片|视频帧|视觉|图像|九宫格|逐帧', text):
            return {'visual'}
        if re.search(r'脚本|代码|批量|清点|文件整理', text):
            return {'files'}
        if re.search(r'检索|收集|调研|资料整理', text):
            return {'research'}
        return {'writing'} if args.get('assignee') == 'uika' else ({'review'} if args.get('assignee') == 'misumi' else {'research'})

    def select(self, args, *, pinned=False):
        requested = str(args.get('assignee') or '')
        if requested not in self.capabilities:
            raise AdmissionError('执行者不在本群启用的分工表中。')
        loads = self.loads()
        total = lambda p: loads[p]['running'] + loads[p]['ready']
        if total(requested) < 2:
            return requested, loads
        if pinned:
            raise AdmissionError('指定执行者已有两张待执行或运行中的卡，请等待名额后重试。')
        required = self._required(args)
        candidates = [p for p, caps in self.capabilities.items() if required <= set(caps) and total(p) < 2]
        if not candidates:
            raise AdmissionError('同样胜任的执行者当前都已满额，请保留本派单号，名额释放后重试。')
        # Keep the foreground coordinator available when an executor can take the card.
        order = {profile: i for i, profile in enumerate(PROFILES)}
        candidates.sort(key=lambda p: (p == 'doloris', total(p), loads[p]['running'],
                                       order.get(p, len(order)), p))
        return candidates[0], loads

    def _receipt(self, task, key, source, *, duplicate=False):
        return {'author': AUTHOR, 'board': self.board, 'dispatch_id': key, 'task_id': task['id'],
                'assignee': task.get('assignee'), 'status': task.get('status'),
                'source_message_id': source.get('message_id'), 'duplicate': duplicate,
                'created_at': time.time()}

    def _record(self, db, receipt, args, source):
        db.execute('INSERT INTO dispatches VALUES(?,?,?,?,?,?,?,?,?) '
                   'ON CONFLICT(board,dispatch_key) DO UPDATE SET task_id=excluded.task_id, '
                   'assignee=excluded.assignee,status=excluded.status,receipt=excluded.receipt',
                   (self.board, receipt['dispatch_id'], receipt.get('task_id'), receipt.get('assignee'),
                    receipt.get('admission_status', receipt['status']), _json(receipt), _intent_hash(args),
                    _json({'author': AUTHOR, **source}), receipt['created_at']))

    def create(self, args, source, create_native: Callable, *, pinned=False):
        args, source = dict(args), dict(source)
        key = dispatch_key(args, source)
        with self._locked() as db:
            row = db.execute('SELECT receipt,intent_hash FROM dispatches WHERE board=? AND dispatch_key=?',
                             (self.board, key)).fetchone()
            if row:
                old = json.loads(row['receipt'])
                if row['intent_hash'] != _intent_hash(args):
                    return {'ok': False, 'error': '同一派单号对应的任务范围发生变化，请为新的要求使用新的派单号。',
                            'receipt': old}
                if old.get('task_id'):
                    task = self.task(old['task_id'])
                    if task is None:
                        return {'ok': False, 'error': '派单回执对应的任务已移除，需要人工核对。', 'receipt': old}
                    receipt = self._receipt(task, key, source, duplicate=True)
                    if old.get('admission_status') == 'awaiting_capacity' and task['status'] == 'blocked':
                        receipt['admission_status'] = 'awaiting_capacity'
                    for field in ('pinned', 'requested_assignee', 'required_capabilities'):
                        if field in old:
                            receipt[field] = old[field]
                    return {'ok': True, 'task_id': task['id'], 'assignee': task.get('assignee'),
                            'status': task.get('status'), 'receipt': receipt, 'duplicate': True}
            # Recovery after native commit but before the receipt commit.
            existing = self._existing(key)
            if existing is not None:
                if (_scope_body(existing.get('body')) != _scope_body(args.get('body'))
                        or str(existing.get('title') or '').strip() != str(args.get('title') or '').strip()):
                    return {'ok': False, 'error': '看板已有同派单号的其它任务，请核对任务范围。',
                            'task_id': existing['id']}
                receipt = self._receipt(existing, key, source, duplicate=True)
                self._admission_details(receipt, args, pinned=pinned)
                if self._initially_capacity_blocked(existing, args):
                    receipt.update(admission_status='awaiting_capacity', reason='等待同样胜任的执行者释放名额')
                self._record(db, receipt, args, source)
                return {'ok': True, 'task_id': existing['id'], 'assignee': existing.get('assignee'),
                        'status': existing.get('status'), 'receipt': receipt, 'duplicate': True}
            waiting = False
            try:
                assignee, loads = self.select(args, pinned=pinned)
            except AdmissionError as exc:
                if str(args.get('assignee') or '') not in self.capabilities:
                    return {'ok': False, 'error': str(exc)}
                assignee, loads, waiting = args['assignee'], self.loads(), True
            updated = {**args, 'board': self.board, 'idempotency_key': key, 'assignee': assignee}
            if waiting:
                updated['initial_status'] = 'blocked'
            result = _decoded(create_native(updated))
            task_id = result.get('task_id') if result.get('ok') else None
            if not task_id:
                return result
            task = self.task(task_id)
            if task is None:
                raise RuntimeError('Native creation returned a missing task')
            receipt = self._receipt(task, key, source)
            self._admission_details(receipt, args, pinned=pinned)
            if waiting:
                receipt.update(admission_status='awaiting_capacity', reason='等待同样胜任的执行者释放名额')
            self._record(db, receipt, args, source)
            return {**result, 'receipt': receipt, 'load_before': loads, 'routed_from': args.get('assignee')}

    def _admission_details(self, receipt, args, *, pinned):
        receipt.update(pinned=bool(pinned), requested_assignee=str(args.get('assignee') or ''),
                       required_capabilities=sorted(self._required(args)))

    def _initially_capacity_blocked(self, task, args):
        if task['status'] != 'blocked' or args.get('initial_status') == 'blocked':
            return False
        with self._board_read() as db:
            if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='task_events'").fetchone() is None:
                return False
            created = db.execute("SELECT payload FROM task_events WHERE task_id=? AND kind='created' "
                                 'ORDER BY id LIMIT 1', (task['id'],)).fetchone()
        return bool(created and _decoded(created['payload']).get('status') == 'blocked')

    def record_for_task(self, task_id):
        if not self.state_db.exists():
            return None
        db = sqlite3.connect(self.state_db, timeout=5)
        try:
            row = db.execute('SELECT receipt FROM dispatches WHERE board=? AND task_id=? LIMIT 1',
                             (self.board, task_id)).fetchone()
            return json.loads(row[0]) if row else None
        finally:
            db.close()

    def followup(self, task_id, comment_id, create_native, *, source=None, dispatch_id=None):
        original = self.task(task_id)
        if original is None or original['status'] not in {'done', 'archived'}:
            return None
        with self._board_read() as db:
            comment = db.execute('SELECT body FROM task_comments WHERE id=? AND task_id=?',
                                 (comment_id, task_id)).fetchone()
        if comment is None:
            raise AdmissionError('追加要求未在原任务中登记。')
        body = comment['body']
        inherited = ('assignee', 'priority', 'max_runtime_seconds', 'workspace_kind', 'workspace_path',
                     'tenant', 'completion_contract', 'goal_mode', 'goal_max_turns')
        args = {key: original[key] for key in inherited if original.get(key) is not None}
        if (args.get('workspace_kind') == 'scratch' and args.get('workspace_path')
                and not Path(args['workspace_path']).exists()):
            # Completed scratch is disposable; let the new card resolve its own
            # directory while retaining the original scope and origin linkage.
            args.pop('workspace_path')
        for native, tool_arg in (('project_id', 'project'), ('model_override', 'model'),
                                 ('provider_override', 'provider')):
            if original.get(native) is not None:
                args[tool_arg] = original[native]
        if original.get('skills') is not None:
            args['skills'] = json.loads(original['skills']) if isinstance(original['skills'], str) else original['skills']
        args.update(title=('跟进 ' + task_id + ' ' + original['title'])[:120],
                    body=f'原任务 {task_id} 的追加要求\n\n{body}\n\n原任务范围与验收要求\n\n{original.get("body") or ""}',
                    idempotency_key=dispatch_id or f'followup:{self.board}:{task_id}:{comment_id}')
        return self.create(args, source or {'profile': 'doloris'},
                           lambda data: create_native(data, original=original), pinned=True)

    def activate_waiting(self, reactivate_native):
        activated = []
        with self._locked() as db:
            rows = db.execute("SELECT receipt FROM dispatches WHERE board=? AND status='awaiting_capacity' "
                              "ORDER BY created_at", (self.board,)).fetchall()
            for row in rows:
                receipt = json.loads(row['receipt'])
                task = self.task(receipt['task_id'])
                if task is None:
                    db.execute("UPDATE dispatches SET status='missing' WHERE board=? AND dispatch_key=?",
                               (self.board, receipt['dispatch_id']))
                    continue
                if task['status'] != 'blocked':
                    # A previous native unblock may have committed before its receipt did.
                    self._record_activation(db, receipt, task)
                    if task['status'] in {'ready', 'running'}:
                        activated.append(receipt)
                    continue
                try:
                    assignee, _ = self.select(task, pinned=receipt.get('pinned', False))
                except AdmissionError:
                    continue
                if assignee != task['assignee']:
                    parameters = inspect.signature(reactivate_native).parameters
                    if ('assignee' not in parameters
                            and not any(p.kind == inspect.Parameter.VAR_KEYWORD for p in parameters.values())):
                        continue
                    result = reactivate_native(task['id'], assignee=assignee)
                else:
                    result = reactivate_native(task['id'])
                if result:
                    current = self.task(task['id'])
                    if current and current['status'] != 'blocked' and current['assignee'] == assignee:
                        self._record_activation(db, receipt, current)
                        activated.append(receipt)
        return activated

    def _record_activation(self, db, receipt, task):
        receipt.update(status=task['status'], assignee=task.get('assignee'),
                       admission_status='admitted', activated_at=time.time())
        db.execute('UPDATE dispatches SET status=?,assignee=?,receipt=? WHERE board=? AND dispatch_key=?',
                   (task['status'], task.get('assignee'), _json(receipt), self.board, receipt['dispatch_id']))

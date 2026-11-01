# Author: Codex, 2026-10-19.
"""The same admission and receipt path for tools and authorized external dispatch."""
from __future__ import annotations

import json
from pathlib import Path
import sqlite3
import time

from .dispatch import AUTHOR, DispatchService
from .outbox import TaskOutbox


class TaskChannel:
    def __init__(self, settings):
        self.settings = dict(settings)
        self.root = Path(settings['root'])
        self.board = settings['board']
        self.dispatch = DispatchService(self.root, self.board)
        self.outbox = TaskOutbox(self.root / 'task-channel' / 'outbox.sqlite3')

    def receipt_notice(self, result, source):
        receipt = result.get('receipt') or {}
        task_id = receipt.get('task_id')
        if not task_id:
            return None
        old = self.outbox.latest(task_id=task_id, kind='received',
                                 group_id=self.settings['delivery_group_id'], profile='doloris')
        notice = old
        if notice is None:
            waiting = receipt.get('admission_status') == 'awaiting_capacity'
            text = ('已关联已有完成记录。' if receipt.get('status') in {'done','archived'} else
                    ('已登记，等待空闲名额。' if waiting else '已接单，进入执行队列。'))
            text += ' 执行者：' + str(receipt.get('assignee') or '')
            notice = self.outbox.enqueue(task_id=task_id, kind='received',
                event_key=f'dispatch:{self.board}:{receipt["dispatch_id"]}:received',
                group_id=self.settings['delivery_group_id'], profile='doloris',
                executor=receipt.get('assignee') or '', text=text,
                metadata={'author': AUTHOR, 'board': self.board,
                          'source_message_id': source.get('message_id') or '',
                          'source_profile': source.get('profile') or '',
                          'dispatch_id': receipt['dispatch_id']})
        self.outbox.link_received_source(notice['id'], message_id=source.get('message_id') or '',
            group_id=self.settings['delivery_group_id'], profile='doloris',
            source_profile=source.get('profile') or '')
        return notice

    def create(self, args, source, native, *, pinned=False):
        result = self.dispatch.create(args, source, native, pinned=pinned)
        if result.get('ok'):
            from .native import normalize_group_subscriptions
            normalize_group_subscriptions(self.root, self.board, result['task_id'], self.settings)
            notice = self.receipt_notice(result, source)
            if notice:
                result['task_notice_id'] = notice['id']
        return result

    def progress(self, *, now=None):
        now = time.time() if now is None else now
        interval = max(60, int(self.settings.get('progress_interval_seconds', 600)))
        notices = []
        if not self.dispatch.board_db.is_file():
            return notices
        db = sqlite3.connect(self.dispatch.board_db.resolve().as_uri()+'?mode=ro',uri=True,timeout=2)
        db.row_factory = sqlite3.Row
        try:
            rows = db.execute("SELECT t.id,t.assignee,t.current_run_id,r.started_at FROM tasks t "
                              "JOIN task_runs r ON r.id=t.current_run_id WHERE t.status='running'").fetchall()
        finally:
            db.close()
        for row in rows:
            if not row['started_at'] or now-row['started_at'] < interval:
                continue
            last = self.outbox.latest(task_id=row['id'],kind='progress',
                                      group_id=self.settings['delivery_group_id'],profile='doloris')
            if last and now-last['created_at'] < interval:
                continue
            notices.append(self.outbox.enqueue(task_id=row['id'],kind='progress',
                event_key=f'progress:{self.board}:{row["id"]}:{row["current_run_id"]}:{int(now//interval)}',
                group_id=self.settings['delivery_group_id'],profile='doloris',executor=row['assignee'] or '',
                text='任务仍在后台执行，阶段结果会随检查点保存。',progress_interval=interval,
                metadata={'author':AUTHOR,'board':self.board,'run_id':row['current_run_id']}))
        return notices


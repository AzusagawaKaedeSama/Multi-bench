# 作者：Codex（GPT-6）· 2026-10-28
"""Deliver a local operator command to one existing Hermes card, idempotently."""
import json
from pathlib import Path
import re
import sqlite3
import sys
import urllib.parse
import urllib.request

def main():
    data = json.loads(sys.stdin.read(65537))
    job, command = data['job'], data['command']
    board, task_id = job['source_board'], job['source_id']
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,120}', board) or not re.fullmatch(r't_[a-f0-9]{8,32}', task_id):
        raise ValueError('原工单编号需要检查。')
    detail_url = 'http://127.0.0.1:3900/api/task-board/jobs/' + urllib.parse.quote(job['id'], safe='')
    with urllib.request.urlopen(detail_url, timeout=3) as response:
        raw = response.read(65537)
    if len(raw) > 65536:
        raise ValueError('工作台工单详情超过读取上限。')
    active = json.loads(raw).get('meta', {}).get('active_execution', {})
    if active.get('adapter') == 'hermes.idle-gpu' and active.get('external_id') == '20261029-local-idle-gpu-v0001':
        return {'ok': True, 'delivery': 'executor_poll'}
    root = Path('E:/Hermes')
    dbpath = root / 'kanban' / 'boards' / board / 'kanban.db'
    marker = '[workbench-command:' + command['id'] + ']'
    with sqlite3.connect(dbpath.as_uri() + '?mode=ro', uri=True, timeout=2) as db:
        existing = db.execute('SELECT id FROM task_comments WHERE task_id=? AND instr(body,?)>0 ORDER BY id LIMIT 1', (task_id, marker)).fetchone()
    labels = {'pause': '请完整保存断点并暂停本任务', 'resume': '请在原授权与资源范围内恢复本任务', 'cancel': '请完整保存断点并停止本任务', 'deadline': '用户更新了截止时间', 'message': '用户追加要求'}
    kind = command['type']
    if kind not in labels:
        raise ValueError('不支持这类执行指令。')
    payload = (marker + '\n' + labels[kind] + '。\n用户说明：' + command['note']
               + ('\n当前截止时间：' + job['deadline_at'] if kind == 'deadline' and job.get('deadline_at') else '')
               + '\n执行后请回写工作台工单 ' + job['id'] + ' 的实际状态，并确认指令 ' + command['id']
               + '。使用 node E:/AgentData/ReviewHub/task-board-cli.cjs report，通过标准输入提交 id、state、detail、ack_command_id。'
               + '\n暂停/停止需等待本任务自有子进程实际停止；保留其他任务与共享服务。截止时间不会自动扩展既有费用、资源或GPU授权。')
    if kind in {'pause', 'cancel'}:
        payload += '\n保存并停止后，把原生工单以needs_input阻塞，保留断点，防止调度器自动重启；再回写工作台的paused或cancelled与指令确认。'
    sys.path.insert(0, str(root / 'shared'))
    from task_channel.native import board_connection
    with board_connection(root, board) as (kb, conn):
        comment_id = existing[0] if existing else kb.add_comment(conn, task_id, 'workbench-operator', payload)
        resumed = kb.unblock_task(conn, task_id) if kind == 'resume' else False
    return {'ok': True, 'comment_id': comment_id, 'duplicate': bool(existing), 'resumed_native_queue': resumed, 'delivery': 'Hermes任务评论，活跃worker在工具边界接收'}

try:
    print(json.dumps(main(), ensure_ascii=False))
except Exception as exc:
    print(json.dumps({'ok': False, 'message': str(exc)[:300]}, ensure_ascii=False))
    sys.exit(1)

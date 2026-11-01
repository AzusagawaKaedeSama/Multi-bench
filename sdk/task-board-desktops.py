# 作者：Codex（GPT-6）· 2026-10-28
"""Read desktop session status without changing client files or copying transcripts."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import time
import psutil

HOME = Path.home()
START = time.monotonic()
EVENTS, HEALTH = [], []
TITLES_PATH = Path('E:/AgentData/ReviewHub/database/desktop-titles.json')
try:
    TITLES = json.loads(TITLES_PATH.read_text(encoding='utf8'))
except (OSError, ValueError):
    TITLES = {}

def stamp(value):
    return datetime.fromtimestamp(value, timezone.utc).isoformat()

def clean(value, length=220):
    text = str(value or '')
    text = re.sub(r'\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{16,})\b', '[已隐藏凭证]', text)
    text = re.sub(r'(?i)(password|token|api_key|authorization)\s*[:=]\s*\S+', r'\1=[已隐藏]', text)
    return text[:length]

def tail(path, limit=512*1024):
    with path.open('rb') as f:
        size = f.seek(0, 2)
        f.seek(max(0, size-limit))
        data = f.read(limit)
    result=[]
    for line in data.splitlines():
        try:
            value=json.loads(line)
            if isinstance(value, dict):
                result.append(value)
        except (ValueError, UnicodeError):
            pass
    return result

def emit(adapter, external, title, state, path, detail, progress=None, activity_at=None):
    info=path.stat()
    updated=activity_at if activity_at is not None else info.st_mtime
    sequence=int(updated*1000)
    EVENTS.append({'schema':'workbench.task.v1','adapter':adapter,'external_id':external,'session_id':external,
                   'event_id':f'{adapter}:{external}:{sequence}:{state}', 'sequence':sequence,
                   'title':clean(title,180) or adapter+'会话', 'agent':adapter+' desktop', 'state':state,
                   'heartbeat_at':stamp(updated), 'detail':detail,
                   'progress':progress or {}, 'capabilities':['observe']})

def codex():
    dbpath=HOME/'.codex/state_5.sqlite'
    if not dbpath.exists():
        return {'id':'codex','name':'GPT / Codex Desktop','status':'not_installed','mode':'observe'}
    with sqlite3.connect(dbpath.as_uri()+'?mode=ro', uri=True, timeout=1) as db:
        rows=db.execute("SELECT id,title,rollout_path,originator FROM threads WHERE archived=0 AND thread_source='user' AND (originator='Codex Desktop' OR lower(originator) LIKE '%dot%') AND updated_at>? ORDER BY updated_at DESC LIMIT 20",(time.time()-86400,)).fetchall()
    history_path=HOME/'.codex/thread_history_1.sqlite'
    history=sqlite3.connect(history_path.as_uri()+'?mode=ro',uri=True,timeout=1) if history_path.exists() else None
    catalog_path=HOME/'.codex/sqlite/codex-dev.db'
    catalog=sqlite3.connect(catalog_path.as_uri()+'?mode=ro',uri=True,timeout=1) if catalog_path.exists() else None
    if history:
        history.set_progress_handler(lambda: int(time.monotonic()-START>5),10000)
    count=0
    for tid,title,filename,originator in rows:
        if time.monotonic()-START>5:
            break
        path=Path(filename)
        if not path.exists():
            continue
        turn=history.execute('SELECT turn_id,status,started_at,completed_at FROM thread_turns WHERE thread_id=? ORDER BY rollout_ordinal DESC LIMIT 1',(tid,)).fetchone() if history else None
        activity=None
        if turn:
            item=history.execute('SELECT created_at_ms,started_at_ms,completed_at_ms,item_type FROM thread_items WHERE thread_id=? AND turn_id=? ORDER BY rollout_ordinal DESC LIMIT 1',(tid,turn[0])).fetchone()
            activity=max([float(x) for x in ([turn[2],turn[3]]+list(item[:3] if item else [])) if x is not None])
            if activity>1e11:
                activity/=1000
            if time.time()-activity>86400:
                continue
            state={'inProgress':'running','completed':'registered','interrupted':'needs_attention','failed':'needs_attention'}.get(turn[1],'registered')
            last=turn[1]
        else:
            activity=path.stat().st_mtime
            if time.time()-activity>86400:
                continue
        records=[] if turn else tail(path)
        last=''
        for row in records:
            if row.get('type')=='event_msg':
                kind=row.get('payload',{}).get('type','')
                if kind in {'task_complete','task_started','turn_aborted','user_message'}:
                    last=kind
        if not turn:
            state='registered' if last=='task_complete' else 'needs_attention' if last=='turn_aborted' else 'running'
            if not last and time.time()-activity>900:
                state='registered'
        else:
            last=turn[1]
        pretty=TITLES.get(tid)
        if catalog:
            native_title=catalog.execute("SELECT display_title FROM local_thread_catalog WHERE host_id='local' AND thread_id=?",(tid,)).fetchone()
            if native_title and native_title[0]:
                pretty=native_title[0]
        if not pretty:
            # Raw SQLite title may contain the original prompt and reference boilerplate.
            pretty=next((line.strip() for line in str(title).splitlines() if line.strip() and not line.startswith(('#','[','<','These are','These live'))), 'GPT会话 '+tid[-8:])
        adapter='dot' if 'dot' in str(originator).lower() else 'codex'
        emit(adapter,tid,pretty,state,path,'本轮会话已结束，等待接续或提交验收。' if state=='registered' else '从桌面会话事件读取执行状态；心跳时间取自客户端实际写入。',{'session_state':last or 'activity_observed','source':'desktop_turn_database' if turn else 'desktop_session_events'},activity_at=activity)
        count+=1
    if history:
        history.close()
    if catalog:
        catalog.close()
    return {'id':'codex','name':'GPT / Codex Desktop','status':'connected','sessions':count,'mode':'observe','capabilities':['observe']}

def claude():
    root=HOME/'.claude/sessions'
    if not root.exists():
        return {'id':'claude','name':'Claude Desktop','status':'not_installed','mode':'observe'}
    count=0
    for path in list(root.glob('*.json'))[:40]:
        try:
            if path.stat().st_size>32768:
                continue
            session=json.loads(path.read_text(encoding='utf8'))
            pid=int(session.get('pid',0))
            process=psutil.Process(pid)
            if 'claude' not in process.name().lower():
                continue
            expected=int(session.get('procStart',0))/10_000_000-11644473600
            if expected>0 and abs(process.create_time()-expected)>3:
                continue
            sid=session['sessionId']
            status=session.get('status','unknown')
            state='running' if status in {'busy','working','running','active','thinking','tool_use'} else 'registered'
            if status in {'permission','waiting_for_permission','waiting_input','error'}:
                state='needs_attention'
            emit('claude',sid,session.get('name') or 'Claude会话 '+sid[-8:],state,path,
                 'Claude会话空闲，等待接续或提交验收。' if state=='registered' else '读取Claude原生会话状态。',
                 {'session_state':status,'pid':pid,'source':'claude_desktop_session'})
            count+=1
        except (OSError,ValueError,KeyError,psutil.Error):
            continue
    return {'id':'claude','name':'Claude Desktop','status':'connected','sessions':count,'mode':'observe','capabilities':['observe']}

def dsh():
    root=HOME/'.dsh/sessions'
    if not root.exists():
        return {'id':'dsh','name':'DSH','status':'not_installed','mode':'observe'}
    recent=[]
    for project in list(root.iterdir())[:40]:
        if not project.is_dir():
            continue
        for folder in list(project.iterdir())[:2000]:
            if time.monotonic()-START>5:
                break
            if not folder.is_dir():
                continue
            for filename in ('session.jsonl','session.jsonl.zstd'):
                path=folder/filename
                if path.is_file() and time.time()-path.stat().st_mtime<86400:
                    recent.append(path)
                    break
    for path in sorted(recent,key=lambda p:p.stat().st_mtime,reverse=True)[:8]:
        # Compressed histories have independent records. Observation requires only
        # their actual modification time; a native hook can report precise phases.
        state='running' if time.time()-path.stat().st_mtime<180 else 'registered'
        emit('dsh',path.parent.name,'DSH会话 '+path.parent.name[-12:],state,path,
             'DSH会话文件有新活动。' if state=='running' else 'DSH会话等待接续。',{'source':'dsh_session_activity','detail_level':'activity_only'})
    return {'id':'dsh','name':'DSH','status':'connected','sessions':len(recent),'mode':'observe','capabilities':['observe']}

for key,fn in [('codex',codex),('claude',claude),('dsh',dsh)]:
    try:
        HEALTH.append(fn())
    except Exception as exc:
        HEALTH.append({'id':key,'status':'unavailable','message':clean(type(exc).__name__)})
for key,name in [('dot','Dot'),('openclaw','OpenClaw')]:
    HEALTH.append({'id':key,'name':name,'status':'protocol_ready','mode':'adapter','capabilities':[]})
print(json.dumps({'events':EVENTS,'adapters':HEALTH},ensure_ascii=False))

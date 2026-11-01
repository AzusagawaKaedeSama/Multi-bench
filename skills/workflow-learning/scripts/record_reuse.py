# 作者：Codex（GPT-6）· 2026-10-28
"""Record observed skill use with existing artifacts; preview unless --commit."""
import argparse
from datetime import datetime, timezone, timedelta
import hashlib
import json
import os
from pathlib import Path
import time

LEDGER=Path('E:/AgentData/ReviewHub/skill-registry/reuse.jsonl')

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--skill',required=True)
    ap.add_argument('--source-task',required=True)
    ap.add_argument('--current-task',required=True)
    ap.add_argument('--status',choices=['offline_checked','task_verified','blocked'],required=True)
    ap.add_argument('--artifact',action='append',default=[],type=Path)
    ap.add_argument('--validation',required=True)
    ap.add_argument('--observed-result',required=True)
    ap.add_argument('--metrics-json',type=Path)
    ap.add_argument('--human-review',type=Path)
    ap.add_argument('--human-verdict',choices=['adopted','needs_change','rejected','unreviewed'],default='unreviewed')
    ap.add_argument('--commit',action='store_true')
    a=ap.parse_args()
    if a.status=='task_verified' and a.source_task==a.current_task:ap.error('task_verified requires a different task')
    if a.status!='blocked' and not a.artifact:ap.error('an existing artifact is required')
    artifacts=[];deadline=time.monotonic()+20;size=0
    for path in a.artifact+([a.human_review]if a.human_review else []):
        p=path.resolve(strict=True);size+=p.stat().st_size
        if size>64*1024**2:ap.error('evidence exceeds 64 MiB; use a small task receipt')
        h=hashlib.sha256()
        with p.open('rb')as f:
            for b in iter(lambda:f.read(1024*1024),b''):
                if time.monotonic()>deadline:ap.error('evidence read exceeded 20 seconds')
                h.update(b)
        artifacts.append({'path':str(p),'sha256':h.hexdigest(),'bytes':p.stat().st_size})
    row={'author':'Codex (GPT-6)','recorded_at':datetime.now(timezone(timedelta(hours=8))).isoformat(),
        'skill':a.skill,'source_task':a.source_task,'current_task':a.current_task,'status':a.status,
        'artifacts':artifacts,'validation':a.validation,'observed_result':a.observed_result,
        'metrics':json.loads(a.metrics_json.read_text(encoding='utf-8'))if a.metrics_json else None,
        'human_verdict':a.human_verdict,'human_review_source':str(a.human_review.resolve())if a.human_review else None}
    if a.commit:
        assert LEDGER.resolve().drive.upper()=='E:'
        LEDGER.parent.mkdir(parents=True,exist_ok=True)
        lock=LEDGER.with_suffix('.lock')
        try:fd=os.open(lock,os.O_CREAT|os.O_EXCL|os.O_WRONLY)
        except FileExistsError:ap.exit(2,'ledger is busy; preserve the receipt and retry later\n')
        try:
            with LEDGER.open('a',encoding='utf-8')as f:f.write(json.dumps(row,ensure_ascii=False)+'\n')
        finally:os.close(fd);lock.unlink()
    print(json.dumps({'committed':a.commit,'ledger':str(LEDGER),'record':row},ensure_ascii=False,indent=2))

if __name__=='__main__':main()

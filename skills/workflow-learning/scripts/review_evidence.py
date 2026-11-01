# 作者：Codex（GPT-6）· 2026-10-28
"""Read bounded formal review archives referenced by local review-page URLs."""
import hashlib
import json
from pathlib import Path
import re
import time
from urllib.parse import unquote,urlparse

URL=re.compile(r'https?://(?:127\.0\.0\.1|localhost):3955/[^\s<>"\]]+?\.html',re.I)
FIELDS={'score','overall','rating','v','decision','verdict','tags','note','notes','selected',
        'scale','max_score','question_id','source','origin','updated_at','human_review'}

def compact(value,depth=0):
    if depth>5:return '[nested value omitted]'
    if isinstance(value,dict):
        direct={k:compact(v,depth+1) for k,v in value.items()if k in FIELDS}
        result=dict(direct)
        for k,v in value.items():
            if k not in FIELDS and isinstance(v,(dict,list)):
                filtered=compact(v,depth+1)
                if filtered not in ({},[]):result[str(k)[:120]]=filtered
            if len(result)>=40:break
        return result
    if isinstance(value,list):return [compact(x,depth+1) for x in value[:40]]
    if isinstance(value,str):return value[:500]
    return value

def collect(text,max_archives=3,max_chars=4200):
    deadline=time.monotonic()+3;seen=set();candidates=[];result=[]
    for url in URL.findall(text):
        path=unquote(urlparse(url).path)
        if not re.match(r'^/[DEFI]/',path,re.I):continue
        page=Path(path[1]+':/'+path[3:]).resolve()
        if page.drive.upper()not in {'D:','E:','F:','I:'}or not page.is_file():continue
        archive=page.parent/'审阅存档'
        if not archive.is_dir():continue
        for i,p in enumerate(archive.iterdir()):
            if i>=400 or time.monotonic()>deadline:break
            if p.suffix!='.json' or not p.name.startswith(page.stem+'__')or '__QA_'in p.name.upper():continue
            if p.resolve().drive.upper()not in {'D:','E:','F:','I:'}or p.resolve()in seen:continue
            seen.add(p.resolve())
            try:
                if p.stat().st_size<=512_000:candidates.append((p.stat().st_mtime,p))
            except OSError:continue
    for _,p in sorted(candidates,reverse=True):
        if len(result)>=max_archives or time.monotonic()>deadline:break
        try:
            raw=p.read_bytes();d=json.loads(raw)
            if str(d.get('key','')).upper().startswith(('QA_','QA-')):continue
            value=d.get('value',{})
            if isinstance(value,str):value=json.loads(value)
            row={'source':str(p.resolve()),'sha256':hashlib.sha256(raw).hexdigest(),'storage_key':d.get('key'),
                'saved_at':d.get('saved_at'),'original_review_fields':compact(value),
                'provenance':'formal non-QA archive; preserve original source fields and rating scale'}
            if len(json.dumps(result+[row],ensure_ascii=False))>max_chars:continue
            result.append(row)
        except (OSError,ValueError,TypeError):continue
    return result

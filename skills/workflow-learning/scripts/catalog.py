# 作者：Codex（GPT-6）· 2026-10-28
"""Bounded, offline inventory of local user skills. No model calls or source mutations."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import time
from datetime import datetime, timezone, timedelta

BASE = Path('C:/Users/10939')
ROOTS = [BASE/'.codex/skills', BASE/'.agents/skills', BASE/'.claude/skills', BASE/'.dsh/skills',
         BASE/'AppData/Local/Deepseek Harness EAC/dpx/dsh-environments/eac-beta/dsh-home/skills',
         Path('E:/Hermes/shared/skills'), Path('E:/Hermes/skills'),
         Path('E:/Steam/steamapps/common/SVFI/SVFI-Skills')]
SKIP = {'.system', '__pycache__', '.git', 'node_modules', '.cache'}

def inventory(seconds=20, max_entries=12000):
    started = time.monotonic()
    roots = list(ROOTS)
    profiles = Path('E:/Hermes/profiles')
    if profiles.is_dir():
        roots += [p/'skills' for p in profiles.iterdir() if p.is_dir()]
    roots += [Path('D:/2-视频制作/0-工作流')]
    rows, issues, seen, scanned = {}, [], set(), 0
    limited = False
    def walk(directory, depth, max_depth):
        nonlocal scanned, limited
        if time.monotonic()-started > seconds or scanned >= max_entries:
            limited = True
            return
        try:
            entries = sorted(os.scandir(directory), key=lambda x: x.name.lower())
        except OSError as exc:
            issues.append({'path':str(directory), 'issue':type(exc).__name__})
            return
        for ent in entries:
            scanned += 1
            if time.monotonic()-started > seconds or scanned >= max_entries:
                limited = True
                return
            if ent.name in SKIP or ent.name.startswith('.'):
                continue
            p = Path(ent.path)
            is_link = p.is_symlink() or bool(getattr(p.lstat(),'st_file_attributes',0) & 0x400)
            try:
                real = p.resolve()
                if is_link and not p.exists():
                    issues.append({'path':str(p),'target':str(real),'issue':'broken-link'})
                    continue
                if not p.is_dir():
                    continue
                if depth==0 and root.name!='0-工作流' and not any(p.iterdir()):
                    issues.append({'path':str(p),'target':str(real),'issue':'empty-skill-entry'})
                sk = p/'SKILL.md'
                if sk.is_file():
                    resolved = str(sk.resolve())
                    data = sk.read_bytes()
                    content = data.decode('utf-8-sig', errors='replace')
                    match = re.search(r'\A---\s*\n(.*?)\n---',content,re.S)
                    fm = match.group(1) if match else ''
                    def field(name):
                        m = re.search(r'^'+name+r':\s*(.*)$',fm,re.M)
                        return m.group(1).strip().strip('\"\x27') if m else ''
                    row = rows.setdefault(resolved, {'name':field('name') or p.name,
                        'description':field('description'), 'source':resolved,
                        'sha256':hashlib.sha256(data).hexdigest(),'aliases':[]})
                    if str(p) not in row['aliases']:
                        row['aliases'].append(str(p))
                    if not match:
                        issues.append({'path':str(sk),'issue':'missing-frontmatter'})
                    continue
                key = (str(real).lower(), max_depth-depth)
                if depth < max_depth and key not in seen:
                    seen.add(key)
                    walk(p,depth+1,max_depth)
            except OSError as exc:
                issues.append({'path':str(p),'issue':type(exc).__name__})
    for root in roots:
        if root.is_dir():
            walk(root,0,4 if root.name=='0-工作流' else 3)
    skills = sorted(rows.values(),key=lambda x:(x['name'],x['source']))
    names = {}
    for row in skills:
        names.setdefault(row['name'],[]).append(row)
    multi = [{'name':name,'sources':[r['source']for r in group],
              'identical_content':len({r['sha256']for r in group})==1}
             for name,group in names.items() if len(group)>1]
    divergent = [row for row in multi if not row['identical_content']]
    return {'author':'Codex (GPT-6)','generated_at':datetime.now(timezone(timedelta(hours=8))).isoformat(),
        'scope':'Windows user, Hermes and installed SVFI skills plus D:/2-视频制作/0-工作流; Codex bundled plugins and WSL excluded',
        'roots':[str(p) for p in roots], 'skills':skills,'issues':issues,'divergent_names':divergent,'multi_source_names':multi,
        'skill_count':len(skills),'name_count':len(names),'alias_count':sum(len(x['aliases']) for x in skills),
        'scanned_entries':scanned,'limited':limited,'elapsed_seconds':round(time.monotonic()-started,3)}

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--query',default='')
    ap.add_argument('--output',type=Path)
    ap.add_argument('--seconds',type=float,default=20)
    ap.add_argument('--limit',type=int,default=20)
    args=ap.parse_args()
    data=inventory(args.seconds)
    if args.output:
        resolved=args.output.resolve()
        if resolved.drive.upper() not in {'D:','E:','F:','I:'}:
            ap.error('output must resolve to D/E/F/I')
        resolved.parent.mkdir(parents=True,exist_ok=True)
        resolved.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    if args.query:
        tokens=args.query.lower().split()
        matches=[x for x in data['skills'] if all(t in json.dumps(x,ensure_ascii=False).lower() for t in tokens)]
    else:
        matches=data['skills']
    summary={k:v for k,v in data.items() if k not in {'skills','roots','multi_source_names','divergent_names'}}
    summary['multi_source_name_count']=len(data['multi_source_names'])
    summary['divergent_names']=[x['name']for x in data['divergent_names']]
    print(json.dumps(summary,ensure_ascii=False))
    print(json.dumps({'matches':matches[:args.limit],'matched_count':len(matches)},ensure_ascii=False))

if __name__=='__main__':
    main()

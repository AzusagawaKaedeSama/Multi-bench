# 作者：Codex（GPT-6）· 2026-10-26
"""给审阅HTML补充统一标签页图标；标注、脚本与页面地址保持。"""
from pathlib import Path
import hashlib,os,re,uuid

START='<!-- REVIEW_HUB_ICON_20261026_BEGIN -->'
END='<!-- REVIEW_HUB_ICON_20261026_END -->'
LINKS='<link rel="icon" type="image/x-icon" href="http://127.0.0.1:3900/favicon.ico?v=20261026-1"><link rel="icon" type="image/svg+xml" href="http://127.0.0.1:3900/favicon.svg?v=20261026-1"><link rel="apple-touch-icon" sizes="180x180" href="http://127.0.0.1:3900/apple-touch-icon.png?v=20261026-1"><meta name="theme-color" content="#31232e">'
BLOCK=START+LINKS+END

def branded(source):
    if START in source:return source
    end=re.search(r'</head\s*>',source,re.I)
    if end:return source[:end.start()]+BLOCK+source[end.start():]
    head=re.search(r'<head\b[^>]*>',source,re.I)
    if head:return source[:head.end()]+BLOCK+source[head.end():]
    doc=re.match(r'\ufeff?\s*<!doctype[^>]*>',source,re.I)
    pos=doc.end() if doc else 0
    return source[:pos]+BLOCK+source[pos:]

def ensure_icon(file,backup_dir=None):
    p=Path(file).resolve(strict=True)
    if p.drive.upper() not in {'D:','E:','F:','I:'} or p.suffix.lower() not in {'.html','.htm'}:raise ValueError('审阅页面路径需要核对')
    stat=p.stat()
    if stat.st_size>16*1024*1024:return {'path':str(p),'status':'skipped_large'}
    raw=p.read_bytes();source=raw.decode('utf-8');updated=branded(source)
    if updated==source:return {'path':str(p),'status':'already_present'}
    assert updated.replace(BLOCK,'',1)==source
    if backup_dir:
        backup=Path(backup_dir);backup.mkdir(parents=True,exist_ok=True)
        dest=backup/(hashlib.sha256(str(p).encode()).hexdigest()[:16]+'.html')
        if not dest.exists():dest.write_bytes(raw)
    after=p.stat()
    if (after.st_size,after.st_mtime_ns,after.st_ino)!=(stat.st_size,stat.st_mtime_ns,stat.st_ino):return {'path':str(p),'status':'skipped_changed'}
    temp=p.with_name(p.name+'.'+uuid.uuid4().hex+'.branding.tmp')
    try:temp.write_bytes(updated.encode('utf-8'));os.replace(temp,p)
    finally:temp.unlink(missing_ok=True)
    return {'path':str(p),'status':'added','before_sha256':hashlib.sha256(raw).hexdigest(),'after_sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'content_preserved_except_icon':True}

# 作者：Claude Code（Opus 5.5）· 2026-10-20
# 本地审阅服务：给所有静态审阅页加后台自动保存。每点一次「好/错/漏」就写进服务器上的 JSON，不用手动导出。
# 原理：伺服 HTML 时在 <head> 最前面注入一段脚本——页面打开时先从服务器读回已保存的标注填进 localStorage，
#      之后页面每次 localStorage.setItem 都同步 POST 到服务器。旧页面不用改就能用。
# 存档：<页面所在目录>/审阅存档/<页面文件名>__<存储键>.json，每次保存前把旧版本留一份 .bak。
# 用法：python 审阅服务.py [端口，默认 3955]；页面地址 http://127.0.0.1:3955/<D盘或E盘绝对路径去掉冒号>，首页列出常用审阅页。
import http.server, json, os, sys, time, urllib.parse, shutil, re, threading, uuid, socket
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 3955
ROOTS = {"D": "D:/", "E": "E:/"}
# 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01
# 审阅查看器：/view?doc=docs/x.json 短链。只允许 viewer/docs 下的 *.json。
HERE_DIR = os.path.dirname(os.path.abspath(__file__))
VIEWER_DIR = os.path.join(HERE_DIR, "viewer")
VIEW_DOCS_DIR = os.path.join(VIEWER_DIR, "docs")
def resolve_view_doc(doc):
    """把 doc 参数解析成 viewer/docs 下的真实 .json 路径；越界或不存在返回 None。"""
    raw = urllib.parse.unquote(doc or "").strip().replace("\\", "/")
    if raw.startswith("docs/"):
        raw = raw[5:]
    if not raw or "/" in raw or not raw.lower().endswith(".json"):
        return None
    target = os.path.normpath(os.path.join(VIEW_DOCS_DIR, raw))
    if os.path.commonpath([target, VIEW_DOCS_DIR]) != VIEW_DOCS_DIR:
        return None
    return target if os.path.isfile(target) else None
# 作者：WorkBuddy（智能设计助手）· 2026-10-31。手机经 Tailscale 访问时 Host 是 MagicDNS 名或 100.x IP，
# 这里放行以便在手机上勾选审阅；本地文件读取范围仍受 ROOTS 限制不变。
def _host_allowed(host):
    h = (host or "").strip().lower()
    if ":" in h and not h.startswith("["):
        h = h.split(":")[0]
    if h in ("localhost", "127.0.0.1", "::1", "[::1]"):
        return True
    if re.fullmatch(r"100\.(\d{1,3})\.\d{1,3}\.\d{1,3}", h):
        return 64 <= int(h.split(".")[1]) <= 127
    if h.startswith("fd7a:115c:a1e0:"):
        return True
    return h.endswith(".ts.net")
INJECT = """<link rel="icon" type="image/x-icon" href="http://127.0.0.1:3900/favicon.ico?v=20261026-1"><link rel="icon" type="image/svg+xml" href="http://127.0.0.1:3900/favicon.svg?v=20261026-1"><link rel="apple-touch-icon" sizes="180x180" href="http://127.0.0.1:3900/apple-touch-icon.png?v=20261026-1"><meta name="theme-color" content="#31232e"><script>(function(){var P=window.__reviewPage||location.pathname;
function k2f(k){return P+'::'+k;}
try{var x=new XMLHttpRequest();x.open('GET','/__load?page='+encodeURIComponent(P),false);x.send();
 if(x.status==200){var d=JSON.parse(x.responseText||'{}');for(var k in d){localStorage.setItem(k,d[k]);}}}catch(e){console.warn('审阅服务读取失败',e)}
var _set=Storage.prototype.setItem,_rm=Storage.prototype.removeItem;
var pending={},saving=false;
function drain(){if(saving)return;var keys=Object.keys(pending);if(!keys.length)return;var k=keys[0],v=pending[k];delete pending[k];saving=true;
 try{var y=new XMLHttpRequest();y.open('POST','/__save',true);y.setRequestHeader('Content-Type','application/json');
 y.onloadend=function(){saving=false;drain();};y.send(JSON.stringify({page:P,key:k,value:v}));}catch(e){saving=false;}}
function push(k,v){pending[k]=v;drain();}
Storage.prototype.setItem=function(k,v){_set.call(this,k,v);if(this===localStorage&&!/theme/i.test(k))push(k,String(v));};
Storage.prototype.removeItem=function(k){_rm.call(this,k);if(this===localStorage)push(k,null);};
window.__审阅服务=true;})();</script>"""

# 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01
# 页面内右键兜底：浏览器直接打开审阅页时（没有桌面壳），右键图片给「复制图片 / 复制图片路径 / 复制图片地址」。
# 在桌面壳里 window.chrome.webview 存在，这段直接退出 —— 由壳的原生菜单接管，不出现两套菜单。
INJECT += """<script>(function(){if(window.chrome&&window.chrome.webview)return;var menu=null;
function path(url){try{var u=new URL(url,location.href);if(u.origin!==location.origin)return'';var p=decodeURIComponent(u.pathname).replace(/^[/]+/,'');var m=/^([DEde])[/](.*)$/.exec(p);if(!m)return'';return m[1].toUpperCase()+':'+String.fromCharCode(92)+m[2].split('/').join(String.fromCharCode(92));}catch(e){return''}}
function toast(t){var d=document.getElementById('__wbToast');if(!d){d=document.createElement('div');d.id='__wbToast';d.style.cssText='position:fixed;left:50%;transform:translateX(-50%);bottom:28px;background:#111c;color:#fff;padding:9px 16px;border-radius:8px;font:13px/1.4 "Microsoft YaHei",sans-serif;z-index:2147483647;pointer-events:none;opacity:0;transition:opacity .2s';document.body.appendChild(d);}d.textContent=t;d.style.opacity='1';clearTimeout(d._t);d._t=setTimeout(function(){d.style.opacity='0'},2400);}
function close(){if(menu){menu.remove();menu=null;}}
async function copyImage(url){try{var r=await fetch(url,{credentials:'include'});var b=await r.blob();var t=b.type||'image/png';if(t.indexOf('image/')!==0)throw new Error('不是图片');var it=new ClipboardItem({[t]:b});await navigator.clipboard.write([it]);toast('图片已复制');}catch(e){try{var img=new Image();img.crossOrigin='anonymous';img.src=url;await img.decode();var c=document.createElement('canvas');c.width=img.naturalWidth;c.height=img.naturalHeight;c.getContext('2d').drawImage(img,0,0);var blob=await new Promise(function(res){c.toBlob(res,'image/png')});await navigator.clipboard.write([new ClipboardItem({'image/png':blob})]);toast('图片已复制');}catch(e2){toast('复制图片失败：'+(e2.message||'浏览器不允许'));}}}
function copyText(t,ok){navigator.clipboard.writeText(t).then(function(){toast(ok)},function(e){toast('复制失败：'+e.message)});}
document.addEventListener('contextmenu',function(ev){var el=ev.target&&ev.target.closest?ev.target.closest('img'):null;if(!el)return;var url=el.currentSrc||el.src;if(!url)return;ev.preventDefault();close();menu=document.createElement('div');menu.style.cssText='position:fixed;z-index:2147483647;background:#1b1f24;color:#e8edf1;border:1px solid #333a42;border-radius:8px;padding:4px 0;font:13px/1.5 "Microsoft YaHei",sans-serif;box-shadow:0 8px 24px #0008;min-width:190px';
function item(label,fn){var d=document.createElement('div');d.textContent=label;d.style.cssText='padding:6px 16px;cursor:pointer';d.onmouseenter=function(){d.style.background='#2b333c'};d.onmouseleave=function(){d.style.background='transparent'};d.onclick=function(e){e.stopPropagation();close();fn()};menu.appendChild(d);}
item('复制图片',function(){copyImage(url)});
var lp=path(url);if(lp)item('复制图片路径',function(){copyText(lp,'路径已复制：'+lp)});
item('复制图片地址',function(){copyText(url,'图片地址已复制')});
menu.style.left=Math.min(ev.clientX,innerWidth-210)+'px';menu.style.top=Math.min(ev.clientY,innerHeight-180)+'px';document.body.appendChild(menu);},true);
document.addEventListener('click',close,true);document.addEventListener('scroll',close,true);})();</script>"""
def local_path(url_path):
    p = urllib.parse.unquote(url_path).lstrip("/")
    # 查看器（/viewer/...）：/view 短链的页面键固定是 /viewer/index.html，
    # 评分存档要落到 viewer/审阅存档/ 下，所以这里给它一条同源映射。
    if p.startswith("viewer/"):
        target = os.path.normpath(os.path.join(VIEWER_DIR, p[len("viewer/"):]))
        if os.path.commonpath([target, VIEWER_DIR]) != VIEWER_DIR: return None
        return target if os.path.exists(target) else None
    drive, _, rest = p.partition("/")
    if drive not in ROOTS: return None
    return os.path.normpath(os.path.join(ROOTS[drive], rest))
def store_file(page_path, key):
    lp = local_path(page_path)
    d = os.path.join(os.path.dirname(lp), "审阅存档"); os.makedirs(d, exist_ok=True)
    safe = "".join(c if c.isalnum() or c in "._-" else "_" for c in key)
    return os.path.join(d, f"{os.path.splitext(os.path.basename(lp))[0]}__{safe}.json")
# 作者：Codex（GPT-6）· 2026-10-29：同一份标注串行写入，临时文件独立，坏单项不阻断整页读取。
_STORE_LOCKS = {}
_STORE_GUARD = threading.Lock()
def store_lock(filename):
    with _STORE_GUARD:
        return _STORE_LOCKS.setdefault(os.path.normcase(filename), threading.RLock())
def read_record(filename):
    with store_lock(filename):
        for candidate in (filename, filename + '.bak'):
            try:
                with open(candidate, encoding='utf-8') as handle: record = json.load(handle)
                if isinstance(record, dict) and 'key' in record and 'value' in record: return record
            except (OSError, ValueError): pass
    return None
def write_record(filename, record):
    with store_lock(filename):
        if os.path.exists(filename):
            try:
                with open(filename, encoding='utf-8') as handle: json.load(handle)
                shutil.copy2(filename, filename + '.bak')
            except (OSError, ValueError): pass
        temporary = filename + '.' + uuid.uuid4().hex + '.tmp'
        try:
            with open(temporary, 'w', encoding='utf-8') as handle:
                json.dump(record, handle, ensure_ascii=False); handle.flush(); os.fsync(handle.fileno())
            os.replace(temporary, filename)
        finally:
            if os.path.exists(temporary): os.remove(temporary)
class H(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *a): pass
    # 作者：WorkBuddy（智能设计助手）· 2026-10-31：拒绝非本机与非 tailnet 的 Host。
    def _host_guard(self):
        if not _host_allowed(self.headers.get("Host", "")):
            self._send(403, "forbidden", "text/plain; charset=utf-8")
            return False
        return True
    def _file(self, lp):
        # 作者：Codex（GPT-6）· 2026-10-29；视频/音频按字节读取，支持原生播放器跳转。
        size = os.path.getsize(lp); start, end, code = 0, size - 1, 200
        requested = self.headers.get('Range')
        if requested:
            m = re.fullmatch(r'bytes=(\d*)-(\d*)', requested.strip())
            try:
                if not m or not any(m.groups()) or size == 0: raise ValueError()
                a, b = m.groups()
                if a:
                    start = int(a); end = min(int(b), size - 1) if b else size - 1
                else:
                    suffix = int(b)
                    if suffix <= 0: raise ValueError()
                    start = max(0, size - suffix)
                if start >= size or start > end: raise ValueError()
                code = 206
            except ValueError:
                self.send_response(416); self.send_header('Content-Range', f'bytes */{size}')
                self.send_header('Accept-Ranges', 'bytes'); self.send_header('Content-Length', '0'); self.end_headers(); return
        self.send_response(code); self.send_header('Content-Type', self.guess_type(lp))
        self.send_header('Content-Length', str(max(0, end - start + 1)))
        self.send_header('Accept-Ranges', 'bytes'); self.send_header('Cache-Control', 'no-store')
        if code == 206: self.send_header('Content-Range', f'bytes {start}-{end}/{size}')
        self.end_headers()
        try:
            with open(lp, 'rb') as f:
                f.seek(start); left = end - start + 1
                while left > 0:
                    data = f.read(min(left, 256 * 1024))
                    if not data: break
                    self.wfile.write(data); left -= len(data)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass
    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        b = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code); self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b)))
        self.send_header("Cache-Control", "no-store"); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        if not self._host_guard(): return
        u = urllib.parse.urlparse(self.path)
        if u.path == "/__load":
            page = urllib.parse.parse_qs(u.query).get("page", [""])[0]; lp = local_path(page); out = {}
            if lp:
                d = os.path.join(os.path.dirname(lp), "审阅存档"); pre = os.path.splitext(os.path.basename(lp))[0] + "__"
                if os.path.isdir(d):
                    for f in os.listdir(d):
                        if f.startswith(pre) and f.endswith(".json"):
                            j = read_record(os.path.join(d, f))
                            if j is not None: out[j["key"]] = j["value"]
            return self._send(200, json.dumps(out, ensure_ascii=False))
        if u.path == "/view":
            # 短链：/view?doc=docs/moss-style.json → 伺服 viewer/index.html。
            # 短链下 location.pathname 是 /view（不是真实文件），所以页面键与 doc 都由服务端注入：
            #   __reviewPage → 评分存档落到 viewer/审阅存档/
            #   __docOverride → 查看器用它替代查询串里的 doc
            doc = urllib.parse.parse_qs(u.query).get("doc", [""])[0]
            target = resolve_view_doc(doc)
            if not target:
                return self._send(404, "doc 不在白名单内（只允许 viewer/docs 下的 .json）", "text/plain; charset=utf-8")
            viewer_html = os.path.join(VIEWER_DIR, "index.html")
            if not os.path.isfile(viewer_html):
                return self._send(404, "查看器不存在", "text/plain; charset=utf-8")
            s = open(viewer_html, encoding="utf-8", errors="replace").read()
            rel = os.path.relpath(target, VIEWER_DIR).replace(os.sep, "/")
            boot = ('<script>window.__reviewPage="/viewer/index.html";'
                    'window.__docOverride="/viewer/' + rel + '";</script>')
            i = s.lower().find("<head>")
            s = (s[:i + 6] + boot + INJECT + s[i + 6:]) if i >= 0 else boot + INJECT + s
            return self._send(200, s, "text/html; charset=utf-8")
        if u.path in ("/", "/index.html"):
            links = "".join(f'<li><a href="/{p}">{p}</a></li>' for p in INDEX)
            return self._send(200, f"<meta charset=utf-8><title>审阅服务</title><h3>审阅页（自动保存）</h3><ul>{links}</ul>", "text/html; charset=utf-8")
        lp = local_path(u.path)
        if not lp or not os.path.exists(lp): return self._send(404, "not found", "text/plain")
        if os.path.isdir(lp):
            items = sorted(os.listdir(lp)); base = u.path.rstrip("/")
            return self._send(200, "<meta charset=utf-8>" + "".join(f'<div><a href="{base}/{urllib.parse.quote(i)}">{i}</a></div>' for i in items), "text/html; charset=utf-8")
        if lp.lower().endswith((".html", ".htm")):
            s = open(lp, encoding="utf-8", errors="replace").read()
            i = s.lower().find("<head>"); s = (s[:i + 6] + INJECT + s[i + 6:]) if i >= 0 else INJECT + s
            return self._send(200, s, "text/html; charset=utf-8")
        return self._file(lp)
    def do_POST(self):
        if not self._host_guard(): return
        if self.path != "/__save": return self._send(404, "{}")
        n = int(self.headers.get("Content-Length", 0)); j = json.loads(self.rfile.read(n) or b"{}")
        f = store_file(j["page"], j["key"])
        write_record(f, {"page": j["page"], "key": j["key"], "value": j["value"], "saved_at": time.strftime("%Y-%m-%d %H:%M:%S")})
        return self._send(200, '{"ok":true}')
INDEX = []
if __name__ == "__main__":
    idx = os.path.join(os.path.dirname(os.path.abspath(__file__)), "审阅页清单.txt")
    if os.path.exists(idx): INDEX = [l.strip() for l in open(idx, encoding="utf-8") if l.strip() and not l.startswith("#")]
    print(f"审阅服务 http://127.0.0.1:{PORT}/", flush=True)
    # 作者：WorkBuddy（智能设计助手）· 2026-10-31。经 Tailscale Serve 访问时由本机代理转发，
    # 因此绑定地址仍保持 127.0.0.1，不向局域网开放。
    http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()

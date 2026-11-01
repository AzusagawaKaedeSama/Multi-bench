#!/usr/bin/env node
// 作者：dsh（DeepSeek-V4.1-Flash）· 2026-10-21
/**
 * 作品审片台 —— 图片作品 + 视频代表帧，逐张审（保留 / 一般 / 不要）
 * 用法：node 审片台.cjs [--port 3970]    浏览器打开 http://127.0.0.1:3970
 *
 * 标注实时写入 作品审片结果.json（tmp→rename 原子替换）。
 */
const fs = require('fs');
const path = require('path');
const http = require('http');

const HERE = __dirname;
const argv = process.argv;
const PORT = Number(argv[argv.indexOf('--port') + 1]) || 3970;
const RESULT = path.join(HERE, '作品审片结果.json');

function readJson(f, dflt) { try { return JSON.parse(fs.readFileSync(f,'utf8')); } catch { return dflt; } }
function writeJsonAtomic(f, o) {
  const tmp = f + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(o, null, 1), 'utf8');
  fs.renameSync(tmp, f);
}

let RES = readJson(RESULT, { 判定: {}, 更新时间: null });
function save(){ RES.更新时间 = new Date().toISOString(); writeJsonAtomic(RESULT, RES); }

// 候选：图片 + 视频帧
function loadItems(){
  const imgs = readJson(path.join(HERE,'image_candidates.json'), []);
  const vids = readJson(path.join(HERE,'video_candidates.json'), []);
  const out = [];
  imgs.forEach((it, i) => out.push({
    id: 'img:' + i, kind: '图片', cat: it.cat, title: it.name.replace(/\.[^.]+$/,''),
    file: it.path, size: it.kb + ' KB', sub: it.dir
  }));
  // 视频帧
  const frameDir = process.env.REVIEW_FRAMES || path.join(HERE, 'frames');
  vids.forEach(v => {
    for (let k = 1; k <= 3; k++) {
      const f = path.join(frameDir, v.id + '_' + k + '.jpg');
      if (fs.existsSync(f)) out.push({
        id: 'vid:' + v.id + ':' + k, kind: '视频帧', cat: v.proj,
        title: v.name + ' · 第' + k + '帧', file: f, size: v.orient, sub: v.proj, vid: v.id
      });
    }
  });
  return out;
}

let ITEMS = loadItems();

const PAGE = fs.readFileSync(path.join(HERE, 'board.html'), 'utf8');

http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  const send = (c,t,b) => { res.writeHead(c,{'content-type':t}); res.end(b); };

  if (u.pathname === '/') return send(200,'text/html; charset=utf-8', PAGE);
  if (u.pathname === '/api/items') { ITEMS = loadItems(); return send(200,'application/json; charset=utf-8', JSON.stringify(ITEMS)); }
  if (u.pathname === '/api/result') return send(200,'application/json; charset=utf-8', JSON.stringify(RES));
  if (u.pathname === '/api/mark' && req.method === 'POST') {
    let b=''; req.on('data',d=>b+=d); req.on('end',()=>{
      try {
        const d = JSON.parse(b);
        const marks = d.marks || {};
        for (const [k,v] of Object.entries(marks)) {
          if (!['keep','meh','no'].includes(v)) continue;
          if (v === 'keep') RES.判定[k] = 'keep'; else RES.判定[k] = v;
        }
        if (d.clear) delete RES.判定[d.clear];
        save();
        send(200,'application/json; charset=utf-8', JSON.stringify({ ok:true, count:Object.keys(RES.判定).length }));
      } catch(e){ send(400,'text/plain',String(e)); }
    });
    return;
  }
  // 图片访问
  if (u.pathname === '/img') {
    const p = u.searchParams.get('p') || '';
    const abs = path.resolve(p);
    if (!fs.existsSync(abs)) return send(404,'text/plain','not found');
    const ext = path.extname(abs).toLowerCase();
    const mime = { '.png':'image/png', '.jpg':'image/jpeg', '.jpeg':'image/jpeg', '.webp':'image/webp' }[ext] || 'application/octet-stream';
    res.writeHead(200, { 'content-type': mime, 'cache-control':'max-age=3600' });
    return fs.createReadStream(abs).pipe(res);
  }
  send(404,'text/plain','not found');
}).listen(PORT, '127.0.0.1', () => console.log('审片台: http://127.0.0.1:' + PORT));

#!/usr/bin/env node
// 作者：dsh（DeepSeek-V4.1-Flash）· 2026-10-21
/**
 * D 盘迁移筛选台 —— 可实时保存、带后台执行的本地筛选页面
 * 用法：node 迁移筛选台.cjs [--port 3960]   浏览器打开 http://127.0.0.1:3960
 *
 * 勾选实时写入 迁移选择.json（tmp→rename 原子替换），刷新/重开都在。
 * 「预演」不碰文件；「执行迁移」后台跑 migrate.cjs，页面轮询看进度。
 */
const fs = require('fs');
const path = require('path');
const http = require('http');
const { spawn } = require('child_process');

const HERE = __dirname;
const argv = process.argv;
const PORT = Number(argv[argv.indexOf('--port') + 1]) || 3960;
const CAND = path.join(HERE, 'candidates.json');
const SEL = path.join(HERE, '迁移选择.json');
const PLAN = path.join(HERE, '迁移计划.json');
const LOGF = path.join(HERE, '迁移执行.log');
const ENGINE = path.join(HERE, 'engine.cjs');
const TARGET = 'E:\\_Archive_2-视频制作_20261021';

function readJson(f, dflt) {
  try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch (e) { return dflt; }
}
function writeJsonAtomic(f, obj) {
  const tmp = f + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(obj, null, 1), 'utf8');
  fs.renameSync(tmp, f);
}

let CANDIDATES = readJson(CAND, []);
let STATE = readJson(SEL, { select: {}, updated: null });
let RUN = { phase: 'idle', log: '' };
let child = null;

function saveState() {
  STATE.updated = new Date().toISOString();
  writeJsonAtomic(SEL, STATE);
}
function selectedPaths() {
  return CANDIDATES.filter(function (c) { return STATE.select[c.name]; }).map(function (c) { return c.path; });
}

function send(res, code, type, body) {
  res.writeHead(code, { 'content-type': type });
  res.end(body);
}
function readBody(req, cb) {
  let body = '';
  req.on('data', function (d) { body += d; });
  req.on('end', function () { cb(body); });
}

const server = http.createServer(function (req, res) {
  const u = new URL(req.url, 'http://x');

  if (u.pathname === '/' || u.pathname === '/index.html') {
    const page = fs.readFileSync(path.join(HERE, 'filter.html'), 'utf8');
    return send(res, 200, 'text/html; charset=utf-8', page);
  }

  if (u.pathname === '/api/candidates') {
    CANDIDATES = readJson(CAND, []);
    return send(res, 200, 'application/json', JSON.stringify(CANDIDATES));
  }
  if (u.pathname === '/api/state') {
    return send(res, 200, 'application/json', JSON.stringify(STATE));
  }
  if (u.pathname === '/api/mark' && req.method === 'POST') {
    return readBody(req, function (body) {
      try {
        const d = JSON.parse(body);
        if (d.on) STATE.select[d.name] = true; else delete STATE.select[d.name];
        saveState();
        send(res, 200, 'application/json', JSON.stringify(STATE));
      } catch (e) { send(res, 400, 'text/plain', String(e)); }
    });
  }
  if (u.pathname === '/api/bulk' && req.method === 'POST') {
    return readBody(req, function (body) {
      try {
        const d = JSON.parse(body);
        if (d.which === 'all') CANDIDATES.forEach(function (c) { STATE.select[c.name] = true; });
        else if (d.which === 'none') STATE.select = {};
        else if (d.which === 'rec') {
          STATE.select = {};
          CANDIDATES.forEach(function (c) { if (c.rec) STATE.select[c.name] = true; });
        }
        saveState();
        send(res, 200, 'application/json', JSON.stringify(STATE));
      } catch (e) { send(res, 400, 'text/plain', String(e)); }
    });
  }
  if (u.pathname === '/api/run' && req.method === 'POST') {
    return readBody(req, function (body) {
      try {
        const d = JSON.parse(body);
        if (child) return send(res, 409, 'text/plain', 'busy');
        const items = selectedPaths().map(function (p) { return { path: p }; });
        if (!items.length) return send(res, 400, 'text/plain', 'nothing selected');
        writeJsonAtomic(PLAN, { items: items });
        RUN = { phase: d.mode === 'apply' ? 'running' : 'dry', log: '' };
        fs.writeFileSync(LOGF, '', 'utf8');
        const args = [ENGINE, '--list', PLAN, '--target', TARGET, '--log', path.join(HERE, 'events.jsonl')];
        if (d.mode === 'apply') args.push('--apply');
        child = spawn(process.execPath, args, { cwd: HERE });
        function push(buf) {
          const t = buf.toString('utf8');
          RUN.log = (RUN.log + t).slice(-30000);
          fs.appendFileSync(LOGF, t, 'utf8');
        }
        child.stdout.on('data', push);
        child.stderr.on('data', push);
        child.on('close', function (code) {
          RUN.phase = code === 0 ? 'done' : 'failed';
          RUN.log += '\n[exit=' + code + ']\n';
          child = null;
        });
        send(res, 200, 'application/json', JSON.stringify({ ok: true, count: items.length, mode: d.mode }));
      } catch (e) { send(res, 400, 'text/plain', String(e)); }
    });
  }
  if (u.pathname === '/api/run/status') {
    return send(res, 200, 'application/json', JSON.stringify(RUN));
  }
  send(res, 404, 'text/plain', 'not found');
});

server.listen(PORT, '127.0.0.1', function () {
  console.log('D 盘迁移筛选台: http://127.0.0.1:' + PORT);
});

#!/usr/bin/env node
// 作者：dsh（DeepSeek-V4.1-Flash）· 2026-10-21
// 工作台 API 与独立端口预览：Codex（GPT-6）· 2026-10-21
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
// Live2D catalog counts: Codex (GPT-6), 2026-10-21
const { describeLive2d } = require('./live2d-card.cjs');
const { createWorkbenchHandler } = require('./workbench.cjs');
const { getReviewPages: readReviewPages, changeReviewStatus } = require('./review-pages.cjs');
// 作者：WorkBuddy（智能设计助手）· 2026-10-31。经 Tailscale 访问时 Host 是 MagicDNS 名或 100.x IP，
// 放行判断集中在 remote-access.cjs，避免各模块各写一份导致手机能看不能改的错位。
const { isRemoteAllowed, originMatches } = require('./remote-access.cjs');
// 作者：Codex（GPT-6）· 2026-10-27。审阅分类沿用数据库用途，人工状态仍由原审阅账本维护。
// 作者：workbench-ux · 2026-11-01。/api/workbench?fresh=1 重算时，读取 catalog（约 580 ms）只为拿
// 「审阅页 → 分类」这一张映射表。分类不像在线状态那样秒级变化，给映射表单独加 5 秒缓存：
// 连续点刷新时第一次付 580 ms，之后 0 ms。审阅页本身的目录扫描（约 140 ms）仍然每次重做 —— fresh 的语义不变。
const REVIEW_KINDS_TTL_MS = 20000;
let reviewKindsCache = null, reviewKindsAt = 0;
function catalogKinds() {
  if (reviewKindsCache && Date.now() - reviewKindsAt < REVIEW_KINDS_TTL_MS) return reviewKindsCache;
  const entries = require('./catalog-store.cjs').getCatalog({scope:'all',kind:'all',limit:500,include_versions:true}).families;
  const kinds = new Map();
  for (const family of entries) for (const version of family.versions || []) if (version.review_id) kinds.set(version.review_id,family.kind);
  reviewKindsCache = kinds; reviewKindsAt = Date.now();
  return kinds;
}
async function getReviewPages(options) {
  const pages = await readReviewPages(options);
  const kinds = catalogKinds();
  return pages.map(page=>({...page,catalog_kind:kinds.get(page.id)||null}));
}
const HERE = __dirname;
const portIndex = process.argv.indexOf('--port');
const PORT = portIndex >= 0 ? Number(process.argv[portIndex + 1]) : 3900;
if (!Number.isInteger(PORT) || PORT < 1024 || PORT > 65535) throw new Error('端口需要在 1024 到 65535 之间。');
const REMOTE = [
  { name: '个人作品集', url: 'https://example.com/me/portfolio/', desc: '手工创作、AI 创作与影像作品，分别浏览。', tag: '作品集' },
  { name: '远程审片台', url: 'https://example.com/me/review/', desc: '手机逐张审阅，标注实时保存。', tag: '审阅' }
];
function probe(port) {
  return new Promise(resolve => {
    let finished = false;
    const socket = net.connect({ host: '127.0.0.1', port, timeout: 700 });
    const done = online => { if (finished) return; finished = true; socket.destroy(); resolve(online); };
    socket.once('connect', () => done(true)); socket.once('timeout', () => done(false)); socket.once('error', () => done(false));
  });
}
// 作者：workbench-ux · 2026-11-01。服务状态：两条探活并行 + 5 秒缓存。
//
// 原实现每条服务里 desc（Live2D 健康检查 650 ms）与 online（700 ms TCP 探活）是串行 await 的，
// 单条最慢服务就要 1.35 s，直接把 /api/workbench?fresh=1 拖到 1.2 s 以上。
// 两处一起改：单条服务内两条路并行 → 最慢 700 ms；服务列表整体再缓存 5 秒，
// 让「刷新清单」与「服务在线状态」解耦 —— 刷新不用等探活，探活也不会因为刷新被反复触发。
const SERVICES_TTL_MS = 5000;
let servicesCache = null, servicesAt = 0, servicesInFlight = null;
async function computeServices() {
  let list = [];
  try { list = JSON.parse(fs.readFileSync(path.join(HERE, 'services.json'), 'utf8').replace(/^\uFEFF/, '')); } catch {}
  return Promise.all(list.filter(s => Number.isInteger(s.port) && s.port > 0 && s.port < 65536)
    .map(async s => {
      const [desc, online] = await Promise.all([describeLive2d(s), probe(s.port)]);
      return { ...s, desc, url: `http://127.0.0.1:${s.port}${s.route || '/'}`, online };
    }));
}
async function getServices({ fresh = false } = {}) {
  if (!fresh && servicesCache && Date.now() - servicesAt < SERVICES_TTL_MS) return servicesCache;
  if (servicesInFlight) return servicesInFlight;
  servicesInFlight = computeServices()
    .then(list => { servicesCache = list; servicesAt = Date.now(); return list; })
    .finally(() => { servicesInFlight = null; });
  return servicesInFlight;
}
function sendJson(res, status, data) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' });
  res.end(JSON.stringify(data));
}
// 作者：dsh 子代理（deepseek-flash）· 2026-10-31。工作台接口进程内缓存。
// 页面与原生壳侧边栏打开时会重复拉同一份清单，每次重算要扫审阅页目录、解析 catalog、探活服务。
// 15 秒内的重复请求直接复用上一次算好的整份 JSON；?fresh=1 / ?refresh=1 跳过缓存重算。
// 键按 URL 区分（query 不同各存各的），fresh/refresh 只是刷新开关，不单独占键。
const WORKBENCH_CACHE_TTL_MS = 15000;
const WORKBENCH_CACHE_MAX_KEYS = 16;
const JSON_HEADERS = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' };
function workbenchCacheKey(u) {
  const params = new URLSearchParams(u.search || '');
  params.delete('fresh'); params.delete('refresh');
  const rest = params.toString();
  return u.pathname + (rest ? `?${rest}` : '');
}
function createWorkbenchCache({ ttlMs = WORKBENCH_CACHE_TTL_MS } = {}) {
  const entries = new Map();
  const pending = new Map();
  const readFresh = key => {
    const entry = entries.get(key);
    if (!entry) return null;
    if (Date.now() - entry.at > ttlMs) { entries.delete(key); return null; }
    return entry;
  };
  return {
    readFresh,
    pending,
    get size() { return entries.size; },
    store(key, entry) {
      entries.set(key, entry);
      if (entries.size > WORKBENCH_CACHE_MAX_KEYS) {
        for (const [k, v] of entries) if (Date.now() - v.at > ttlMs) entries.delete(k);
        while (entries.size > WORKBENCH_CACHE_MAX_KEYS) entries.delete(entries.keys().next().value);
      }
    },
    clear() { entries.clear(); },
  };
}
const workbench = createWorkbenchHandler({ here: HERE, port: PORT, getServices, getReviewPages, remote: REMOTE });
const workbenchCache = createWorkbenchCache();
// 用一份假响应跑一遍真正的 /api/workbench 处理器，拿到与线上完全一致的响应体。
// 处理器只读 req.method，不碰 socket，所以这层包装不会改变它的行为。
function captureWorkbench() {
  return new Promise((resolve, reject) => {
    let status = 200, body = '';
    const res = {
      headersSent: false, statusCode: 200,
      writeHead(code) { status = code; this.statusCode = code; this.headersSent = true; return this; },
      setHeader() {}, getHeader() { return undefined; }, removeHeader() {},
      write(chunk) { body += chunk; return true; },
      end(chunk) { if (chunk !== undefined && chunk !== null) body += chunk; resolve({ status, body }); return this; },
    };
    const req = { method: 'GET', url: '/api/workbench', headers: { host: `127.0.0.1:${PORT}` } };
    const u = new URL('/api/workbench', `http://127.0.0.1:${PORT}`);
    Promise.resolve(workbench(req, res, u)).then(handled => { if (handled === false) resolve(null); }, reject);
  });
}
async function computeWorkbench(key, { force = false } = {}) {
  if (!force) { const hit = workbenchCache.readFresh(key); if (hit) return { entry: hit, source: 'hit' }; }
  // 同一 URL 正在算时一起等这一份结果，不重复扫盘（启动预热与首个页面请求常常同时到）。
  const running = workbenchCache.pending.get(key);
  if (running) { const entry = await running; return entry ? { entry, source: 'shared' } : null; }
  const task = (async () => {
    const started = Date.now();
    const captured = await captureWorkbench();
    const ms = Date.now() - started;
    if (!captured || !captured.body) return null;
    try { JSON.parse(captured.body); } catch { return null; }
    const entry = { at: Date.now(), status: captured.status, body: captured.body, ms };
    if (entry.status === 200) workbenchCache.store(key, entry);
    return entry;
  })();
  workbenchCache.pending.set(key, task);
  try {
    const entry = await task;
    return entry ? { entry, source: force ? 'refresh' : 'miss' } : null;
  } finally { workbenchCache.pending.delete(key); }
}
async function workbenchWithCache(req, res, u) {
  if (u.pathname !== '/api/workbench' || req.method !== 'GET') return workbench(req, res, u);
  const force = u.searchParams.get('fresh') === '1' || u.searchParams.get('refresh') === '1';
  let result = null;
  try { result = await computeWorkbench(workbenchCacheKey(u), { force }); } catch {}
  if (!result) return workbench(req, res, u);
  res.writeHead(result.entry.status, {
    ...JSON_HEADERS,
    'x-workbench-cache': result.source,
    'x-workbench-age-ms': String(Math.max(0, Date.now() - result.entry.at)),
    'x-workbench-build-ms': String(result.entry.ms || 0),
  });
  res.end(result.entry.body);
  return true;
}
async function handler(req, res) {
  const host = (req.headers.host || '').toLowerCase();
  if (!isRemoteAllowed(req))
    return sendJson(res, 403, { ok: false, error: 'invalid_host', message: '仅接受本机工作台请求。' });
  let u;
  try { u = new URL(req.url, `http://127.0.0.1:${PORT}`); } catch { return sendJson(res, 400, { ok: false, error: 'invalid_url' }); }
  // 作者：dsh 子代理（deepseek-flash）· 2026-10-31。审阅状态、配置、任务改动成功后立刻失效工作台缓存，不等 TTL。
  const invalidates = (u.pathname === '/api/review-status' && req.method === 'POST')
    || (u.pathname === '/api/settings' && req.method === 'PUT')
    || (u.pathname === '/api/xhs/pause' && req.method === 'POST')
    || /^\/api\/automations\/[a-z]+\/[^/]+\/change$/.test(u.pathname);
  if (invalidates) res.once('finish', () => { if (res.statusCode >= 200 && res.statusCode < 300) workbenchCache.clear(); });
  try {
    if (require("./branding.cjs").handle(req,res,u)) return;
    // 作者：Codex（GPT-6）· 2026-11-01。手机统一入口，生图服务保留原进程。
    if (u.pathname === '/mobile' || u.pathname === '/mobile/') {
      if (!['GET','HEAD'].includes(req.method)) return sendJson(res,405,{error:'method_not_allowed'});
      res.writeHead(302,{location:'https://kaede.tail34f007.ts.net/mobile','cache-control':'no-store'});
      res.end(); return;
    }
    if (await require('./task-board-api.cjs').handle(req,res,u,PORT)) return;
    if (await require("./result-handoff.cjs").handle(req,res,u,PORT)) return;
    if (await require("./project-api.cjs").handle(req,res,u,PORT)) return;
    if (await workbenchWithCache(req, res, u)) return;
    // 作者：Codex（GPT-6）· 2026-10-22；只接收已登记项目ID，不接收文件路径。
    if (u.pathname === '/api/review-status') {
      if(req.method!=='POST')return sendJson(res,405,{message:'请使用POST请求'});
      if(!originMatches(req)||!/^application\/json(?:\s*;|$)/i.test(req.headers['content-type']||''))return sendJson(res,403,{message:'请从工作台页面提交审阅状态'});
      try{let size=0,parts=[];for await(const chunk of req){size+=chunk.length;if(size>4096)return sendJson(res,413,{message:'提交内容过大'});parts.push(chunk);}const data=JSON.parse(Buffer.concat(parts).toString('utf8'));return sendJson(res,200,await changeReviewStatus(data));}
      catch(e){return sendJson(res,e.status||400,{message:e.status?e.message:'状态提交内容需要检查'});}
    }
    if (u.pathname === '/api/review-pages') {
      if (req.method !== 'GET') return sendJson(res, 405, { ok: false, error: 'method_not_allowed', message: '请使用 GET 请求。' });
      return sendJson(res, 200, await getReviewPages({includeArchived:u.searchParams.get('scope')==='all'}));
    }
    if (u.pathname === '/api/services' || u.pathname === '/api/remote') {
      if (req.method !== 'GET') return sendJson(res, 405, { ok: false, error: 'method_not_allowed', message: '请使用 GET 请求。' });
      return sendJson(res, 200, u.pathname === '/api/services' ? await getServices() : REMOTE);
    }
    if (u.pathname === '/' || u.pathname === '/index.html') {
      if (req.method !== 'GET' && req.method !== 'HEAD') return sendJson(res, 405, { ok: false, error: 'method_not_allowed' });
      const page = fs.readFileSync(path.join(HERE, 'index.html'));
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff',
        'referrer-policy': 'same-origin', 'x-frame-options': 'DENY' });
      return res.end(req.method === 'HEAD' ? undefined : page);
    }
    if (u.pathname === '/favicon.ico' && req.method === 'GET') { res.writeHead(204); return res.end(); }
    // 作者：WorkBuddy（智能设计助手）· 2026-10-31。桌面壳原生桥接脚本。
    // 与 /task-board.js 同一模式：普通浏览器打开时脚本自行降级，不影响页面。
    if (u.pathname === '/workbench-bridge.js') {
      if (req.method !== 'GET' && req.method !== 'HEAD') return sendJson(res, 405, { ok: false, error: 'method_not_allowed' });
      const body = fs.readFileSync(path.join(HERE, 'workbench-bridge.js'));
      res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' });
      return res.end(req.method === 'HEAD' ? undefined : body);
    }
    // 角色标签查阅页（dsh 2026-10-23）
    if (u.pathname === '/tags' || u.pathname.startsWith('/tags/')) {
      if (req.method !== 'GET' && req.method !== 'HEAD') return sendJson(res, 405, { ok: false, error: 'method_not_allowed' });
      const rel = u.pathname === '/tags' || u.pathname === '/tags/' ? 'index.html' : u.pathname.slice('/tags/'.length);
      const safe = path.normalize(rel).replace(/^([.][.][\/])+/, '');
      const file = path.join(HERE, 'tags', safe);
      if (!file.startsWith(path.join(HERE, 'tags'))) return sendJson(res, 403, { ok: false, error: 'forbidden' });
      if (!fs.existsSync(file)) return sendJson(res, 404, { ok: false, error: 'not_found' });
      const ext = path.extname(file).toLowerCase();
      const type = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
        '.json': 'application/json; charset=utf-8', '.css': 'text/css; charset=utf-8' }[ext] || 'application/octet-stream';
      const body = fs.readFileSync(file);
      res.writeHead(200, { 'content-type': type, 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' });
      return res.end(req.method === 'HEAD' ? undefined : body);
    }
    sendJson(res, 404, { ok: false, error: 'not_found', message: '没有这个页面或接口。' });
  } catch { if (!res.headersSent) sendJson(res, 500, { ok: false, error: 'internal_error', message: '工作台暂时无法完成请求，请重试。' }); }
}
if (require.main === module) {
  const server = http.createServer(handler);
  server.requestTimeout = 20000; server.headersTimeout = 10000;
  server.listen(PORT, '127.0.0.1', () => {
    console.log(`宿舍机工作台: http://127.0.0.1:${PORT}`);
    require('./task-board-api.cjs').getService();
    // 作者：dsh 子代理（deepseek-flash）· 2026-10-31。监听之后再预热，不挡住启动；
    // 冷盘第一次计算要十几秒，把这段等待从用户点击挪到服务刚起来的时候。
    setImmediate(() => {
      const started = Date.now();
      computeWorkbench('/api/workbench').then(result => {
        console.log(`[workbench-cache] 预热${result ? '完成' : '未完成'} ${Date.now() - started} ms（${result ? result.source : 'n/a'}）`);
      }).catch(error => console.log(`[workbench-cache] 预热失败 ${error && error.message}`));
    });
  });
}
module.exports = { handler, getServices, probe, PORT };

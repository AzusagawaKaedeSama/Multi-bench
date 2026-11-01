// 作者：Codex（GPT-6）· 2026-10-21
// 本机工作台 API：固定适配器与数据目录，不接收命令或文件路径。
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
// 作者：WorkBuddy（智能设计助手）· 2026-10-31。远程放行策略集中在 remote-access.cjs。
const { isTrustedLocal, originMatches } = require('./remote-access.cjs');
const PYTHON = 'D:\\anaconda3\\python.exe';
const CONFIG_HELPER = 'D:\\travel-tmp\\xhs-slow-trial-20261021\\config_helper.py';
const DATA = 'E:\\Hermes\\shared\\workbench';
const REQUESTS = path.join(DATA, 'requests');
const AUDIT = path.join(DATA, 'events.jsonl');
const BODY_LIMIT = 64 * 1024;
const PYTHON_TEMP_ROOT = 'E:\\AgentData\\Work\\ReviewHubTemp';
const PROVIDERS = new Set(['hermes', 'codex', 'dsh', 'claude']);
const CHANGE_FIELDS = new Set(['name', 'schedule', 'prompt']);
class ApiError extends Error {
  constructor(status, code, message) { super(message); this.status = status; this.code = code; }
}
function json(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' });
  res.end(JSON.stringify(body));
}
function method(req, expected) {
  if (req.method !== expected) throw new ApiError(405, 'method_not_allowed', `请使用 ${expected} 请求。`);
}
function localWrite(req, port) {
  // 作者：WorkBuddy（智能设计助手）· 2026-10-31。能改数据的接口保持「仅本机」：
  // 手机只做查看与审阅标注，不允许远程派活或改配置。
  const host = (req.headers.host || '').toLowerCase();
  if (!isTrustedLocal(req)) throw new ApiError(403, 'invalid_host', '仅接受本机工作台请求。');
  if (!originMatches(req)) throw new ApiError(403, 'invalid_origin', '请从本机工作台页面提交修改。');
  if (!/^application\/json(?:\s*;|$)/i.test(req.headers['content-type'] || '')) throw new ApiError(415, 'json_required', '请提交 application/json 数据。');
}
function bodyJson(req) {
  return new Promise((resolve, reject) => {
    if (Number(req.headers['content-length'] || 0) > BODY_LIMIT) { req.resume(); return reject(new ApiError(413, 'body_too_large', '提交内容超过 64 KB。')); }
    let size = 0, chunks = [], done = false;
    const finish = (error, value) => { if (done) return; done = true; clearTimeout(timer); error ? reject(error) : resolve(value); };
    const timer = setTimeout(() => finish(new ApiError(408, 'body_timeout', '提交超时，请重试。')), 10000);
    req.on('data', chunk => {
      if (done) return; size += chunk.length;
      if (size > BODY_LIMIT) { chunks = []; return finish(new ApiError(413, 'body_too_large', '提交内容超过 64 KB。')); }
      chunks.push(chunk);
    });
    req.on('end', () => {
      if (done) return;
      try {
        const value = JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
        if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error('object required');
        finish(null, value);
      } catch { finish(new ApiError(400, 'invalid_json', '提交内容需要是 JSON 对象。')); }
    });
    req.on('error', () => finish(new ApiError(400, 'request_error', '提交中断，请重试。')));
    req.on('aborted', () => finish(new ApiError(400, 'request_aborted', '提交中断，请重试。')));
  });
}
function runPython(script, args, payload, timeout = 18000) {
  return new Promise((resolve, reject) => {
    const childTemp = fs.realpathSync.native(PYTHON_TEMP_ROOT);
    if (!/^[def]:[\\/]/i.test(childTemp)) throw new ApiError(503, 'invalid_temp_drive', '临时目录需使用 D、E 或 F 盘。');
    const child = spawn(PYTHON, ['-B', script, ...args], { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, TEMP: childTemp, TMP: childTemp, TMPDIR: childTemp, PYTHONIOENCODING: 'utf-8', PYTHONUTF8: '1' } });
    let out = '', bytes = 0, done = false;
    const end = (err, value) => { if (done) return; done = true; clearTimeout(timer); err ? reject(err) : resolve(value); };
    const timer = setTimeout(() => { child.kill(); end(new ApiError(503, 'adapter_timeout', '本机任务适配器响应超时。')); }, timeout);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      bytes += Buffer.byteLength(chunk, 'utf8');
      if (bytes > 2 * 1024 * 1024) { child.kill(); return end(new ApiError(503, 'adapter_output_limit', '任务适配器返回内容过多。')); }
      out += chunk;
    });
    child.stderr.on('data', () => {});
    child.on('error', () => end(new ApiError(503, 'adapter_unavailable', '本机任务适配器暂时无法启动。')));
    child.on('close', code => {
      if (done) return;
      let value;
      try { value = JSON.parse(out.replace(/^\uFEFF/, '').trim()); }
      catch { return end(new ApiError(503, 'adapter_invalid_response', '本机任务适配器返回内容无法读取。')); }
      if (code || value.ok === false) {
        const name = value.error || (code === 3 ? 'conflict' : code === 4 ? 'busy' : 'adapter_error');
        const status = name === 'conflict' ? 409 : name === 'busy' ? 423 : code === 2 ? 400 : 502;
        return end(new ApiError(status, name, String(value.message || '任务适配器未完成请求。').slice(0, 500)));
      }
      end(null, value);
    });
    child.stdin.on('error', () => {}); child.stdin.end(payload === undefined ? '' : JSON.stringify(payload));
  });
}
function readJson(filename, fallback) {
  try { return JSON.parse(fs.readFileSync(filename, 'utf8').replace(/^\uFEFF/, '')); } catch { return fallback; }
}
function atomicJson(filename, value) {
  fs.mkdirSync(path.dirname(filename), { recursive: true });
  const tmp = `${filename}.${process.pid}.${crypto.randomUUID()}.tmp`;
  try { fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' }); fs.renameSync(tmp, filename); }
  finally { try { fs.unlinkSync(tmp); } catch {} }
}
function audit(kind, details = {}) {
  try {
    fs.mkdirSync(DATA, { recursive: true });
    fs.appendFileSync(AUDIT, JSON.stringify({ author: 'Codex (GPT-6)', at: new Date().toISOString(), kind, ...details }) + '\n', 'utf8');
  } catch {}
}
function requests() {
  let names;
  try { names = fs.readdirSync(REQUESTS).filter(n => /^[0-9a-f-]+\.json$/i.test(n)); } catch { return []; }
  return names.slice(-500).map(n => readJson(path.join(REQUESTS, n), null)).filter(Boolean)
    .sort((a, b) => String(b.created_at).localeCompare(String(a.created_at))).slice(0, 50);
}
function ownChanges(input) {
  if (input === undefined) return {};
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new ApiError(400, 'invalid_changes', '修改字段需要是 JSON 对象。');
  const result = {};
  for (const [key, value] of Object.entries(input)) {
    if (!CHANGE_FIELDS.has(key)) throw new ApiError(400, 'invalid_field', `不支持修改字段：${String(key).slice(0, 40)}`);
    const limit = key === 'prompt' ? 16000 : key === 'name' ? 120 : 500;
    if (typeof value !== 'string' || !value.trim() || value.length > limit || value.includes('\0')) throw new ApiError(400, 'invalid_field', `${key} 内容为空或超出长度限制。`);
    if (key !== 'prompt' && /[\r\n]/.test(value)) throw new ApiError(400, 'invalid_field', `${key} 需要写在一行。`);
    result[key] = value.trim();
  }
  return result;
}
function safeActivity(runtime, queued) {
  const labels = { worker_started: '小红书取图进程已启动', window_started: '取图窗口已开始', note_reserved: '已开始处理一篇笔记',
    note_complete: '已保存笔记图片', window_quiet_complete: '取图窗口已结束', trial_complete: '试运行已结束', stopped: '取图已停止',
    stop_requested: '已请求停止取图', settings_saved: '已保存统一配置', automation_change_applied: '已更新定时任务',
    automation_change_queued: '定时任务修改已进入待同步列表', xhs_pause_requested: '已请求停止取图' };
  const events = (Array.isArray(runtime.events) ? runtime.events : []).map(e => ({ at: e.at, kind: e.kind,
    message: labels[e.kind] || '小红书任务状态已更新', ...(Number.isFinite(e.images) ? { images: e.images } : {}) }));
  try {
    const fd = fs.openSync(AUDIT, 'r');
    try {
      const size = fs.fstatSync(fd).size, length = Math.min(size, 64 * 1024), buffer = Buffer.alloc(length);
      fs.readSync(fd, buffer, 0, length, size - length);
      for (const line of buffer.toString('utf8').split('\n').slice(-60)) {
        try { const e = JSON.parse(line); events.push({ at: e.at, kind: e.kind, message: labels[e.kind] || '工作台状态已更新',
          ...(Number.isInteger(e.revision) ? { revision: e.revision } : {}) }); } catch {}
      }
    } finally { fs.closeSync(fd); }
  } catch {}
  for (const r of queued) if (r.status === 'queued') events.push({ at: r.created_at, kind: 'automation_change_queued', message: `${r.name || '定时任务'}的修改待代理同步` });
  return events.filter(e => e.at).sort((a, b) => String(b.at).localeCompare(String(a.at))).slice(0, 20);
}
function normalizeXhs(raw) {
  const rt = raw.runtime || {}, policy = rt.today_policy || raw.today_policy || {};
  const windows = (rt.windows || []).map((w, i) => ({ ...w, number: w.number || i + 1 }));
  const current = windows.find(w => w.id === (typeof rt.current_window === 'object' ? rt.current_window?.id : rt.current_window));
  return { status: rt.status || 'unavailable', paused: Boolean(rt.stop_requested || ['paused', 'stopped', 'halted'].includes(rt.status)),
    policy, policy_revision: rt.today_policy_revision ?? 0, settings_revision: raw.revision ?? raw.settings?.revision,
    effective_from: raw.effective_from || raw.settings?.xhs?.effective_from, next_policy: rt.next_policy || raw.next_policy,
    next_due_at: rt.next_due_at || null, trial_end_date: rt.lastDate || policy.trial_end_date,
    stop_requested: Boolean(rt.stop_requested), reason: rt.reason || null,
    today: { date: rt.date || new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Shanghai' }), state: rt.day_state || 'not_planned',
      attempts: rt.attempts_today || 0, complete: rt.complete_today || 0, images: rt.images_today || 0, window_count: windows.length,
      attempt_limit: rt.attempt_limit ?? windows.reduce((sum, w) => sum + (w.limit || policy.notes_per_window || 5), 0), windows },
    current_window: current ? { ...current, limit: current.limit || rt.window_attempt_limit || policy.notes_per_window || 5 } : null,
    ledger_available: rt.status !== undefined && rt.status !== 'not_initialized' };
}
function normalizeAutomation(item) {
  const fields = item.provider === 'hermes' ? ['name', 'schedule'] : item.provider === 'codex' ? ['name', 'schedule', 'prompt'] : [];
  const enabled = fields.length > 0 && item.editable !== false;
  return { ...item, schedule_spec: item.schedule,
    schedule: typeof item.schedule === 'object' ? (item.schedule.display || item.schedule.expr || item.schedule.at || '') : item.schedule,
    timezone: item.timezone || (typeof item.schedule === 'object' ? item.schedule.timezone : undefined) || 'Asia/Shanghai',
    capabilities: { edit: enabled, pause: enabled, resume: enabled, fields } };
}
function createWorkbenchHandler({ here, port, getServices, remote, getReviewPages = async () => [] }) {
  const adapter = path.join(here, 'automation_adapter.py');
  let inventoryCache = null, inventoryAt = 0, inventoryInFlight = null;
  const inventory = async (fresh = false) => {
    if (!fresh && inventoryCache && Date.now() - inventoryAt < 3000) return inventoryCache;
    if (inventoryInFlight) return inventoryInFlight;
    inventoryInFlight = runPython(adapter, [], { operation: 'inventory' }).then(value => { inventoryCache = value; inventoryAt = Date.now(); return value; })
      .finally(() => { inventoryInFlight = null; });
    return inventoryInFlight;
  };
  // 作者：workbench-ux · 2026-11-01。配置读取每次都起一个 python 子进程（实测 125–146 ms），
  // 而它只是一份设置快照，秒级重复读没有意义。加 3 秒缓存：页面点刷新时不再为它多等一轮子进程。
  // 保存配置成功后立刻失效，保证改完马上看到新值。
  let configCache = null, configAt = 0;
  const config = async ({ fresh = false } = {}) => {
    if (!fresh && configCache && Date.now() - configAt < 3000) return configCache;
    const value = await runPython(CONFIG_HELPER, ['read']);
    configCache = value; configAt = Date.now();
    return value;
  };
  return async function handleWorkbench(req, res, u) {
    if (!['/api/workbench', '/api/settings', '/api/xhs/pause', '/api/requests'].includes(u.pathname) && !u.pathname.startsWith('/api/automations/')) return false;
    try {
      if (u.pathname === '/api/workbench') {
        method(req, 'GET');
        // 作者：dsh 子代理（deepseek-flash）· 2026-10-31。待审清单只要待审：归档页由 /api/review-pages?scope=all 单独取。
        const [cfg, inv, services, reviewPages] = await Promise.allSettled([config(), inventory(), getServices(), getReviewPages({ includeArchived: false })]);
        const raw = cfg.status === 'fulfilled' ? cfg.value : {}, native = inv.status === 'fulfilled' ? inv.value : {}, pending = requests();
        const settings = raw.settings || { version: 1, revision: 0, unavailable: true };
        const agents = (native.agents || []).map(a => ({ ...a, available_settings_revision: settings.revision, shared_rule_effective_from: settings.xhs?.effective_from }));
        if (!agents.some(a => a.id === 'codex')) agents.push({ id: 'codex', name: 'Codex', entry: 'Codex 桌面聊天与原生自动化',
          status: 'native_app', status_detail: '原生任务配置已接入清单，修改请求由当前聊天同步',
          shared_rule_revision: 'XHS_COLLECTION_RULE_20261021', available_settings_revision: settings.revision,
          shared_rule_effective_from: settings.xhs?.effective_from });
        json(res, 200, { generated_at: new Date().toISOString(), services: services.status === 'fulfilled' ? services.value : [],
          review_pages: reviewPages.status === 'fulfilled' ? reviewPages.value : [], remote, agents,
          automations: (native.automations || []).map(normalizeAutomation), xhs: normalizeXhs(raw), settings, activity: safeActivity(raw.runtime || {}, pending), requests: pending,
          capabilities: native.capabilities || {}, sources: { settings: cfg.status === 'fulfilled' ? 'ready' : 'unavailable', automations: inv.status === 'fulfilled' ? 'ready' : 'unavailable' } });
        return true;
      }
      if (u.pathname === '/api/settings' && req.method === 'GET') { const value = await config(); json(res, 200, value.settings); return true; }
      if (u.pathname === '/api/settings') {
        method(req, 'PUT'); localWrite(req, port); const value = await bodyJson(req);
        if (Object.keys(value).some(k => !['revision', 'xhs'].includes(k))) throw new ApiError(400, 'invalid_field', '配置只接受 revision 和 xhs 字段。');
        if (!Number.isInteger(value.revision) || !value.xhs || typeof value.xhs !== 'object' || Array.isArray(value.xhs)) throw new ApiError(400, 'invalid_settings', '请提交配置版本和小红书规则。');
        const result = await runPython(CONFIG_HELPER, ['save'], value); configCache = null; configAt = 0; audit('settings_saved', { revision: result.revision, effective_from: result.effective_from });
        json(res, 200, result); return true;
      }
      if (u.pathname === '/api/xhs/pause') {
        method(req, 'POST'); localWrite(req, port); await bodyJson(req); const result = await runPython(CONFIG_HELPER, ['pause'], {});
        audit('xhs_pause_requested'); configCache = null; configAt = 0; json(res, 200, result); return true;
      }
      if (u.pathname === '/api/requests') { method(req, 'GET'); json(res, 200, requests()); return true; }
      const match = /^\/api\/automations\/([a-z]+)\/([^/]+)\/change$/.exec(u.pathname);
      if (!match) throw new ApiError(404, 'not_found', '没有这个任务接口。');
      method(req, 'POST'); localWrite(req, port); const provider = match[1]; let id;
      try { id = decodeURIComponent(match[2]); } catch { throw new ApiError(400, 'invalid_id', '任务编号无效。'); }
      if (!PROVIDERS.has(provider) || id.length > 200 || /[\0\\/]/.test(id)) throw new ApiError(400, 'invalid_id', '任务编号无效。');
      const input = await bodyJson(req);
      if (Object.keys(input).some(k => !['action', 'changes'].includes(k))) throw new ApiError(400, 'invalid_field', '任务修改只接受 action 和 changes 字段。');
      if (!['pause', 'resume', 'update'].includes(input.action)) throw new ApiError(400, 'invalid_action', '请选择暂停、恢复或修改。');
      const changes = ownChanges(input.changes);
      if (input.action === 'update' && !Object.keys(changes).length) throw new ApiError(400, 'empty_changes', '请填写要修改的内容。');
      if (input.action !== 'update' && Object.keys(changes).length) throw new ApiError(400, 'invalid_changes', '暂停或恢复时无需提交修改字段。');
      const native = await inventory(true), item = (native.automations || []).find(a => a.provider === provider && a.id === id);
      if (!item) throw new ApiError(404, 'automation_not_found', '当前清单中没有这个定时任务。');
      if (provider === 'hermes') {
        const result = await runPython(adapter, [], { operation: 'change', provider, id, action: input.action, changes });
        inventoryAt = 0; audit('automation_change_applied', { provider, id, action: input.action }); json(res, 200, result); return true;
      }
      if (provider !== 'codex') throw new ApiError(422, 'provider_unavailable', '这个客户端暂未接通本机任务修改接口。');
      const pending = requests().find(r => r.status === 'queued' && r.provider === provider && r.automation_id === id && r.action === input.action && JSON.stringify(r.changes || {}) === JSON.stringify(changes));
      if (pending) { json(res, 202, { ok: true, status: 'queued', request: pending }); return true; }
      const request = { author: 'Codex (GPT-6)', version: 1, id: crypto.randomUUID(), provider, automation_id: id, name: item.name,
        action: input.action, changes, status: 'queued', created_at: new Date().toISOString(), sync_method: 'Codex 原生 automation_update', message: '已保存修改请求，等待当前聊天通过原生工具同步。' };
      atomicJson(path.join(REQUESTS, `${request.id}.json`), request); audit('automation_change_queued', { provider, id, request_id: request.id, action: input.action });
      json(res, 202, { ok: true, status: 'queued', request }); return true;
    } catch (error) {
      if (!res.headersSent) json(res, error instanceof ApiError ? error.status : 500, { ok: false, error: error instanceof ApiError ? error.code : 'internal_error', message: error instanceof ApiError ? error.message : '工作台暂时无法完成请求，请重试。' });
      return true;
    }
  };
}
module.exports = { createWorkbenchHandler, normalizeXhs, ApiError, localWrite, ownChanges };

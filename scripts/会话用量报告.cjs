#!/usr/bin/env node
/**
 * 会话用量报告.cjs —— 汇总本机会话的 token / LLM 时间 / 模型分布。
 * 数据源: ~/.dsh/storages/session_projcache/sessions/*.json
 * 输出: 0-工作流/用量/用量报告_<YYYYMMDD>.md
 */
const fs = require('fs');
const path = require('path');
const os = require('os');

const OUT_DIR = path.join(__dirname, '用量');
const SRC = path.join(os.homedir(), '.dsh', 'storages', 'session_projcache', 'sessions');
const WS = path.join(os.homedir(), '.dsh', 'storages', 'workspace.json');

const rows = [];
for (const f of fs.readdirSync(SRC).filter(f => f.endsWith('.json'))) {
  try {
    const o = JSON.parse(fs.readFileSync(path.join(SRC, f), 'utf8'));
    const R = (o.record || {}).rows || {};
    const g = k => R[k] && R[k].val;
    const tu = g('tokenUsage') || { totals: {} };
    const ss = g('sessionStats') || {};
    const ms = g('modelSelection') || {};
    rows.push({
      id: f.replace(/\.json$/, '').replace(/^session-/, ''),
      cwd: ((o.record || {}).identity || {}).cwd || '',
      createdAt: ((o.record || {}).identity || {}).createdAt || 0,
      title: (g('title') || '').replace(/\s+/g, ' ').slice(0, 60),
      model: (ms.lastUsed || {}).model || '未知',
      inTok: tu.totals.uncachedInputTokens || 0,
      outTok: tu.totals.outputTokens || 0,
      cacheRead: tu.totals.cacheReadTokens || 0,
      turns: ss.turns || 0,
      llmMs: ss.llmMs || 0,
      toolMs: ss.toolMs || 0,
    });
  } catch { /* 跳过坏文件 */ }
}
if (!rows.length) { console.error('✖ 未找到会话索引:', SRC); process.exit(1); }

let topIds = new Set();
try {
  const ws = JSON.parse(fs.readFileSync(WS, 'utf8'));
  for (const w of Object.values((ws.tables || {}).workspaces || {}))
    for (const id of w.sessionIds || []) topIds.add(id.replace(/^session-/, ''));
} catch { }

const sum = (a, k) => a.reduce((x, r) => x + r[k], 0);
const fmt = n => n.toLocaleString('en-US');
const hours = ms => (ms / 3600000).toFixed(1);

const byDay = {}, byModel = {}, byTop = {};
for (const r of rows) {
  const d = new Date(r.createdAt).toISOString().slice(0, 10);
  const bucket = (r.createdAt && topIds.has(r.id)) ? byTop : byDay; // 占位，后面分组
  (byDay[d] = byDay[d] || []).push(r);
  (byModel[r.model] = byModel[r.model] || []).push(r);
}

const L = [];
const today = new Date();
const dstr = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
L.push(`# 会话用量报告（${dstr}）`, '');
L.push(`> 数据源：\`~/.dsh/storages/session_projcache/sessions/\`（共 ${rows.length} 个会话记录）。`);
L.push('> 含义：LLM 时间 = 模型计算时间；不等于人的工时。用于**看趋势、证明提效**，不要用于精确计费。', '');
L.push('## 总计', '');
L.push('| 指标 | 数值 |', '|---|---|');
L.push(`| 会话记录数 | ${rows.length} |`);
L.push(`| 未缓存输入 tokens | ${fmt(sum(rows, 'inTok'))} |`);
L.push(`| 输出 tokens | ${fmt(sum(rows, 'outTok'))} |`);
L.push(`| 缓存读取 tokens | ${fmt(sum(rows, 'cacheRead'))} |`);
L.push(`| LLM 时间合计 | ${hours(sum(rows, 'llmMs'))} 小时 |`);
L.push(`| 工具执行时间合计 | ${hours(sum(rows, 'toolMs'))} 小时 |`);
L.push(`| 总轮次 | ${fmt(sum(rows, 'turns'))} |`, '');
L.push('## 按日', '');
L.push('| 日期 | 会话数 | 输出 tokens | LLM 小时 |', '|---|---|---|---|');
for (const d of Object.keys(byDay).sort())
  L.push(`| ${d} | ${byDay[d].length} | ${fmt(sum(byDay[d], 'outTok'))} | ${hours(sum(byDay[d], 'llmMs'))} |`);
L.push('', '## 按模型', '');
L.push('| 模型 | 会话数 | 输出 tokens | LLM 小时 |', '|---|---|---|---|');
for (const m of Object.keys(byModel).sort((a, b) => byModel[b].length - byModel[a].length))
  L.push(`| ${m} | ${byModel[m].length} | ${fmt(sum(byModel[m], 'outTok'))} | ${hours(sum(byModel[m], 'llmMs'))} |`);
L.push('', '## 消耗最高的 15 个会话', '');
L.push('| 时间 | 模型 | 轮次 | LLM 分钟 | 输出 tokens | 标题 |', '|---|---|---|---|---|---|');
for (const r of [...rows].sort((a, b) => (b.outTok + b.inTok) - (a.outTok + a.inTok)).slice(0, 15)) {
  const t = new Date(r.createdAt).toISOString().slice(5, 16).replace('T', ' ');
  L.push(`| ${t} | ${r.model} | ${r.turns} | ${(r.llmMs / 60000).toFixed(0)} | ${fmt(r.outTok)} | ${r.title.replace(/\|/g, '/')} |`);
}
L.push('', '## 长会话（LLM 时间 > 2 小时）', '');
L.push('| 时间 | 模型 | LLM 小时 | 标题 |', '|---|---|---|---|');
for (const r of [...rows].sort((a, b) => b.llmMs - a.llmMs).filter(r => r.llmMs > 7200000).slice(0, 15))
  L.push(`| ${new Date(r.createdAt).toISOString().slice(5, 16).replace('T', ' ')} | ${r.model} | ${hours(r.llmMs)} | ${r.title.replace(/\|/g, '/')} |`);
L.push('');

fs.mkdirSync(OUT_DIR, { recursive: true });
// 机器可读汇总（供 收尾.cjs 追加成本曲线）
fs.writeFileSync(path.join(OUT_DIR, '最新统计.json'), JSON.stringify({
  generatedAt: new Date().toISOString(),
  sessions: rows.length,
  inTok: sum(rows, 'inTok'), outTok: sum(rows, 'outTok'), cacheRead: sum(rows, 'cacheRead'),
  llmMs: sum(rows, 'llmMs'), toolMs: sum(rows, 'toolMs'), turns: sum(rows, 'turns'),
  byDay: Object.fromEntries(Object.keys(byDay).sort().map((d) => [d, {
    sessions: byDay[d].length, outTok: sum(byDay[d], 'outTok'), llmMs: sum(byDay[d], 'llmMs'),
  }])),
}, null, 2), 'utf8');
const out = path.join(OUT_DIR, `用量报告_${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}.md`);
fs.writeFileSync(out, L.join('\n'), 'utf8');
console.log('✔ 已生成:', out);
console.log(`  会话 ${rows.length} / 输出 tokens ${fmt(sum(rows, 'outTok'))} / LLM ${hours(sum(rows, 'llmMs'))} 小时`);

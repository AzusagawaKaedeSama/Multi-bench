#!/usr/bin/env node
/**
 * 项目总览.cjs —— 扫描 D:\2-视频制作 下的项目目录，重建根 00_从这里开始.md。
 * 只读扫描，不修改任何项目文件。
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const OUT = path.join(ROOT, '00_从这里开始.md');
const SKIP = new Set(['0-工作流', 'Z-模板文件夹', 'Z-图片作品合集', 'Z-图形素材', 'Z-腾讯会议', '新建文件夹', '参考工程打包', '修复']);

function safeRead(p) { try { return fs.readFileSync(p, 'utf8'); } catch { return null; } }
function exists(p) { try { fs.statSync(p); return true; } catch { return false; } }

function firstHeading(md) {
  if (!md) return null;
  const m = md.match(/^#\s+(.+)$/m);
  return m ? m[1].trim() : null;
}

const projects = [];
for (const e of fs.readdirSync(ROOT, { withFileTypes: true })) {
  if (!e.isDirectory() || SKIP.has(e.name) || e.name.startsWith('.')) continue;
  if (!/^\d{6}-/.test(e.name)) continue;
  // 已归入临时收件箱的辅助任务，其旧位置仅用于兼容现有工程和脚本。
  if (exists(path.join(ROOT, '0-临时inbox', e.name))) continue;
  const dir = path.join(ROOT, e.name);
  const p = { name: e.name, dir };

  p.init = exists(path.join(dir, '00_打开预览.cmd')) ? '00_打开预览.cmd'
    : exists(path.join(dir, '00_打开工作台.cmd')) ? '00_打开工作台.cmd' : null;
  p.rules = exists(path.join(dir, '00_工作台与目录规则.md')) ? '00_工作台与目录规则.md' : null;
  p.entry = exists(path.join(dir, '00_从这里看.md')) ? '00_从这里看.md' : null;

  // 工作台清单
  const maniPath = path.join(dir, '工作台清单.json');
  const mani = safeRead(maniPath);
  if (mani) {
    try {
      const j = JSON.parse(mani);
      p.rev = j.revision;
      p.updatedAt = j.updatedAt;
      p.master = j.master_delivery;
      const st = {};
      for (const pt of j.patches || []) st[pt.status] = (st[pt.status] || 0) + 1;
      p.patchStat = st;
      p.patchCount = (j.patches || []).length;
    } catch { }
  }
  // 冻结交付
  const curPath = path.join(dir, '文档', 'CURRENT_DELIVERY.json');
  const cur = safeRead(curPath);
  if (cur) { try { p.frozen = JSON.parse(cur); } catch { } }
  // 交付包
  const fo = path.join(dir, '最终输出');
  if (exists(fo)) {
    p.bundles = fs.readdirSync(fo).filter(n => n.startsWith('交付包_')).sort();
  }
  // 最近活动
  let latest = 0, latestName = '';
  const stack = [dir];
  let scanned = 0;
  while (stack.length) {
    const d = stack.pop();
    let entries;
    try { entries = fs.readdirSync(d, { withFileTypes: true }); } catch { continue; }
    for (const it of entries) {
      if (it.name === 'node_modules' || it.name === '.git' || it.name.startsWith('.pnpm')) continue;
      const full = path.join(d, it.name);
      if (it.isDirectory() && full.split(path.sep).length - dir.split(path.sep).length < 7) stack.push(full);
      else {
        scanned++;
        try { const m = fs.statSync(full).mtimeMs; if (m > latest) { latest = m; latestName = full; } } catch { }
      }
    }
  }
  p.latest = latest; p.latestName = latestName;
  const rulesMd = safeRead(path.join(dir, '00_工作台与目录规则.md'));
  p.desc = firstHeading(rulesMd) || firstHeading(safeRead(path.join(dir, 'AGENTS.md'))) || '';
  projects.push(p);
}
projects.sort((a, b) => b.name.localeCompare(a.name));

const L = [];
const now = new Date();
const dstr = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')} ${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
L.push('# 从这里开始（视频制作）', '');
L.push(`> 本文件由 \`0-工作流/项目总览.cjs\` 于 ${dstr} 生成。**新会话先读这里，再进项目。**`);
L.push('> 规则：`AGENTS.md`（根纪律）→ `0-工作流/README.md`（标准 v2）→ 项目内 `00_看这里.md`；历史项目沿用原入口。', '');
L.push('## 临时任务', '');
L.push('生图测试、素材收集归档、配音调研和空间整理统一从 [临时收件箱](0-临时inbox/00_看这里.md) 进入。', '');
L.push('## 项目一览', '');
L.push('| 项目 | 入口 | 当前状态 | 冻结交付 | 最近改动 |', '|---|---|---|---|---|');
for (const p of projects) {
  const entry = [p.init, p.rules].filter(Boolean).map(x => '`' + x + '`').join(' + ') || (p.entry ? '`' + p.entry + '`' : '—');
  let status = '';
  if (p.master) status = `预览 ${p.master}`;
  if (p.entry) status = status ? status + " · 成片索引✓" : "成片索引✓";
  if (p.rev !== undefined) status += `${status ? ' · ' : ''}清单 r${p.rev}`;
  if (p.patchStat) {
    const order = ['candidate', 'needs-fix', 'rejected', 'ok'];
    const parts = Object.entries(p.patchStat).sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0])).map(([k, v]) => `${k}×${v}`);
    status += `${status ? ' · ' : ''}${parts.join(' ')}`;
  }
  // 冻结交付
  let frozen = '—';
  if (p.frozen && typeof p.frozen === 'object') {
    const v = p.frozen.version || p.frozen.current || p.frozen.frozen_delivery || '';
    const rv = p.frozen.recommended_video ? path.basename(p.frozen.recommended_video) : '';
    frozen = [v, rv].filter(Boolean).join(' → ') || '（见文档/CURRENT_DELIVERY.json）';
  } else if (p.frozen) frozen = String(p.frozen).slice(0, 40);
  else if (p.bundles && p.bundles.length) frozen = p.bundles[p.bundles.length - 1];
  const lt = p.latest ? new Date(p.latest) : null; const pad = (n) => String(n).padStart(2, "0"); const latest = lt ? (lt.getFullYear() + "-" + pad(lt.getMonth() + 1) + "-" + pad(lt.getDate()) + " " + pad(lt.getHours()) + ":" + pad(lt.getMinutes())) : "—";
  L.push(`| **${p.name}** | ${entry} | ${status || '—'} | ${frozen} | ${latest} |`);
}
L.push('');
L.push('## 交付包明细', '');
for (const p of projects) {
  if (!(p.bundles && p.bundles.length)) continue;
  L.push(`- **${p.name}**：${p.bundles.map(b => '`最终输出/' + b + '/`').join('、')}（只增不改，新版本开新包）`);
}
L.push('');
L.push('## 找东西的唯一路径', '');
L.push('1. 看当前预览 → 双击项目根 `00_打开预览.cmd`（或 `00_打开工作台.cmd`）。');
L.push('2. 知道"现在是什么版本、还没修好什么" → 项目根 `00_工作台与目录规则.md`。');
L.push('3. 取冻结交付 → `最终输出/交付包_vNNN/`。');
L.push('');
L.push('> 不要用最大版本号或最新修改时间猜当前状态；以 `工作台清单.json` / `文档/CURRENT_DELIVERY.json` 为准。');

fs.writeFileSync(OUT, L.join('\n'), 'utf8');
console.log(`✔ 已生成: ${OUT}`);
console.log(`  扫描到 ${projects.length} 个项目: ${projects.map(p => p.name).join(', ')}`);

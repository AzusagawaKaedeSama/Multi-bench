// 作者：WorkBuddy 智能设计助手 · 2026-10-30
// 用法：node publish-to-workbench.cjs <审阅页.html 绝对路径> [显示名] [一句话说明] [分组]
// 作用：把一份 HTML 审阅材料接入宿舍机工作台（3900），让它出现在「创作与审阅」里并可点开。
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const HUB = 'E:/AgentData/ReviewHub';
const REGISTRY = 'D:/2-视频制作/0-工作流/审阅服务/审阅页清单.txt';
const CATALOG = path.join(HUB, 'project-catalog', 'deliveries.json');
const REVIEW_PORT = 3955;

function fail(message) { console.error('✗ ' + message); process.exit(1); }

// 1) 校验输入
const target = process.argv[2];
if (!target) fail('请给出审阅页的绝对路径。');
const physical = fs.realpathSync.native(target);
if (!/^[DEFI]:\\/i.test(physical)) fail('文件需要放在 D / E / F 盘。');
if (!fs.statSync(physical).isFile()) fail('这不是一个文件。');
if (!/\.html?$/i.test(physical)) fail('审阅材料需要是 .html 或 .htm。');

const name = process.argv[3] || path.win32.basename(physical, path.win32.extname(physical));
const desc = process.argv[4] || '逐项查看与确认，勾选状态自动保存在本机浏览器。';
const kind = process.argv[5] || 'operations_record';
if (!['published_work', 'draft_work', 'asset_record', 'research_record', 'operations_record'].includes(kind)) {
  fail('分组只能是 published_work / draft_work / asset_record / research_record / operations_record。');
}

// 2) 与工作台一致的审阅页 ID（sha256(规范化路径) 前 16 位）
const canonical = path.win32.normalize(physical).toLowerCase();
const reviewId = 'review-' + createHash('sha256').update(canonical).digest('hex').slice(0, 16);

// 3) 写进审阅页清单（工作台每次请求重读，无需重启）
const registryLine = physical.replace(/\\/g, '/').replace(/^([A-Z]):\//i, '$1/');
const registryRaw = fs.readFileSync(REGISTRY, 'utf8');
if (!registryRaw.split(/\r?\n/).some(l => l.trim() === registryLine)) {
  const prefix = registryRaw.endsWith('\n') ? '' : '\r\n';
  fs.appendFileSync(REGISTRY, prefix + registryLine + '\r\n', 'utf8');
  console.log('✓ 已写入审阅页清单');
} else {
  console.log('· 审阅页清单中已存在，跳过');
}

// 4) 登记进作品目录，让 /results/<id> 详情页可查
const urlPath = registryLine.split('/').map(encodeURIComponent).join('/');
const reviewUrl = `http://127.0.0.1:${REVIEW_PORT}/${urlPath}`;
const familyId = 'review-' + createHash('sha256').update(physical).digest('hex').slice(0, 12);

const data = JSON.parse(fs.readFileSync(CATALOG, 'utf8'));
const now = new Date().toISOString();
const raw = fs.readFileSync(physical);
const entry = {
  id: familyId,
  name,
  aliases: [],
  kind,
  latest_version: 'v0001',
  recommended_version: null,
  versions: [{
    version: 'v0001',
    parent_version: null,
    completed_at: now,
    completed_time_source: physical,
    source_manifest: physical,
    source_thread: null,
    review_id: reviewId,
    review_url: reviewUrl,
    summary: desc,
    folder: path.win32.dirname(physical),
    files: [{
      path: physical,
      title: name,
      role: 'review',
      id: 'file-' + createHash('sha256').update(physical).digest('hex').slice(0, 16),
      sha256: createHash('sha256').update(raw).digest('hex'),
      bytes: raw.length,
    }],
  }],
};
data.families = data.families.filter(f => (f && f.id) !== familyId);
data.families.push(entry);
data.updated_at = now;
data.revision = (Number(data.revision) || 0) + 1;
fs.writeFileSync(CATALOG, JSON.stringify(data, null, 2), 'utf8');
console.log('✓ 已登记进作品目录');

console.log('');
console.log('工作台入口：');
console.log('  审阅页列表  http://127.0.0.1:3900/#creative');
console.log('  记录详情    http://127.0.0.1:3900/results/' + familyId);
console.log('  审阅页本体  ' + reviewUrl);
console.log('  审阅编号    ' + reviewId);

#!/usr/bin/env node
/**
 * 新项目.cjs —— v2：复制 D:\\2-视频制作\\Z-模板文件夹（A–Z 目录），再放入 0-工作流/模板/项目骨架 的 AGENTS.md 与 00_看这里.md。
 * 用法: node 0-工作流/新项目.cjs "260915-项目名" "一句话说明"
 * 仅为体量较大、可直接发表到用户自媒体账号的独立完整作品建项目；完整漫展长图模板可作特例。
 * 调用前按根 AGENTS.md 的「项目建目录规则」判断；素材收集归档、调研、测试、空间整理用已有相关项目或 0-临时inbox。
 * 已存在同名目录时拒绝执行（只增不改）。
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const TPL = path.join(__dirname, '模板', '项目骨架');
const AZ = path.join(ROOT, 'Z-模板文件夹');

const args = process.argv.slice(2);
if (args.length < 1) {
  console.error('用法: node 0-工作流/新项目.cjs "<项目名>" "<说明>"');
  console.error('示例: node 0-工作流/新项目.cjs "261011-新片名" "计划发布到自媒体账号的完整原创宣传片"');
  console.error('适用: 体量较大、可直接发表的独立完整作品；完整漫展长图模板可作特例。');
  console.error('素材收集归档、调研、测试、空间整理请用已有相关项目或 0-临时inbox。');
  process.exit(1);
}
const name = args[0].trim();
const desc = (args[1] || '').trim();
const target = path.join(ROOT, name);

if (!/^\d{6}-/.test(name)) console.warn(`⚠ 项目名建议 YYMMDD-名称 格式，当前: ${name}`);
if (fs.existsSync(target)) { console.error(`✖ 已存在，不覆盖: ${target}`); process.exit(1); }
if (!fs.existsSync(TPL) || !fs.existsSync(AZ)) { console.error(`✖ 模板缺失: ${TPL} 或 ${AZ}`); process.exit(1); }

const today = new Date();
const ymd = `${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}`;
const fill = (s) => s
  .replaceAll('{{项目名}}', name)
  .replaceAll('{{说明}}', desc || '（待补）')
  .replaceAll('{{日期}}', `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`)
  .replaceAll('{{日期数字}}', ymd);

function copyDir(src, dst) {
  fs.mkdirSync(dst, { recursive: true });
  for (const e of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, e.name), d = path.join(dst, e.name);
    if (e.isDirectory()) copyDir(s, d);
    else {
      const buf = fs.readFileSync(s);
      if (/\.(md|json|cmd|txt)$/i.test(e.name)) {
        fs.writeFileSync(d, fill(buf.toString('utf8')), 'utf8');
      } else fs.writeFileSync(d, buf);
    }
  }
}
copyDir(AZ, target);
copyDir(TPL, target);
console.log(`✔ 已创建项目: ${target}`);
console.log('  骨架: A–H、Z 九个字母目录 + AGENTS.md + 00_看这里.md');
console.log('  下一步: 在 AGENTS.md 写清片子目标，成片放 Z 并更新 00_看这里.md 第一行。');
console.log('  改完目录后运行: node 0-工作流/项目总览.cjs');

// 可选：--workbench 时接入 Remotion 对比台（逐帧复刻对比用，产物放 E）
const {spawnSync: runSync} = require('child_process');
const WANT = process.argv.includes('--workbench') || process.argv.includes('-w');
const chainTarget = process.argv[2];
if (WANT && chainTarget) {
  console.log('— 继续接入共享工作台 —');
  const r = runSync(process.execPath, [require('path').join(__dirname, '工作台', '接入项目.cjs'), chainTarget], {
    stdio: 'inherit', env: {...process.env, NODE_OPTIONS: ''},
  });
  if (r.status !== 0) process.exitCode = r.status ?? 1;
}

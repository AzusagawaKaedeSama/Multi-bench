#!/usr/bin/env node
/**
 * D 盘迁移引擎 — 按清单把选定目录迁移到目标盘
 * 作者：dsh（DeepSeek-V4.1-Flash）· 2026-10-21
 *
 * 规则：
 *  - 复制 → 逐文件 SHA-256 源/目标比对 → 全项目通过 → 才删除源
 *  - 任一步失败：保留源，记录失败，不影响其他项目
 *  - 保留 mtime
 *  - 生成 events.jsonl 执行日志
 * 用法：
 *   node migrate.cjs --list <清单json> --target E:\_Archive_2-视频制作_20261021 [--apply]
 *   不带 --apply 只做预演（dry-run）
 */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function parseArgs(argv) {
  const a = { apply: false };
  for (let i = 2; i < argv.length; i++) {
    const k = argv[i];
    if (k === '--apply') a.apply = true;
    else if (k === '--list') a.list = argv[++i];
    else if (k === '--target') a.target = argv[++i];
    else if (k === '--log') a.log = argv[++i];
  }
  return a;
}

function sha256(file) {
  const h = crypto.createHash('sha256');
  const fd = fs.openSync(file, 'r');
  const buf = Buffer.alloc(1024 * 1024);
  try {
    let n;
    while ((n = fs.readSync(fd, buf, 0, buf.length, null)) > 0) {
      h.update(buf.subarray(0, n));
    }
  } finally { fs.closeSync(fd); }
  return h.digest('hex');
}

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.isFile()) out.push(p);
  }
  return out;
}

function human(bytes) {
  if (bytes >= 1024 ** 3) return (bytes / 1024 ** 3).toFixed(2) + ' GiB';
  if (bytes >= 1024 ** 2) return (bytes / 1024 ** 2).toFixed(1) + ' MiB';
  if (bytes >= 1024) return (bytes / 1024).toFixed(0) + ' KiB';
  return bytes + ' B';
}

const args = parseArgs(process.argv);
if (!args.list || !args.target) {
  console.error('用法: node migrate.cjs --list <清单json> --target <目标目录> [--apply]');
  process.exit(2);
}

const plan = JSON.parse(fs.readFileSync(args.list, 'utf8'));
const items = plan.items || [];
const targetRoot = path.resolve(args.target);
const logPath = args.log || path.join(path.dirname(args.list), 'events.jsonl');
const logStream = fs.createWriteStream(logPath, { flags: 'a' });

function emit(obj) {
  logStream.write(JSON.stringify({ at: new Date().toISOString(), ...obj }) + '\n');
}

console.log('=== D 盘迁移 ' + (args.apply ? '【正式执行】' : '【预演 dry-run】') + ' ===');
console.log('目标: ' + targetRoot);
console.log('项目数: ' + items.length);
console.log('');

let totalBytes = 0, totalFiles = 0, okProjects = 0, failProjects = 0;
const failures = [];

for (const item of items) {
  const src = path.resolve(item.path);
  const name = path.basename(src);
  const dst = path.join(targetRoot, name);

  if (!fs.existsSync(src)) {
    console.log('SKIP  ' + name + ' — 源不存在');
    emit({ event: 'skip', project: name, reason: 'source-missing' });
    continue;
  }

  const files = walk(src);
  const bytes = files.reduce((s, f) => s + fs.statSync(f).size, 0);
  totalBytes += bytes; totalFiles += files.length;

  console.log('--- ' + name);
  console.log('    ' + files.length + ' 个文件, ' + human(bytes));

  if (!args.apply) {
    console.log('    [预演] 将迁移到 ' + dst);
    okProjects++;
    continue;
  }

  // 目标已存在则跳过（不覆盖）
  if (fs.existsSync(dst)) {
    console.log('    SKIP 目标已存在: ' + dst);
    emit({ event: 'skip', project: name, reason: 'target-exists', target: dst });
    continue;
  }

  let projectOk = true;
  const failedFiles = [];
  try {
    fs.mkdirSync(dst, { recursive: true });
    for (const f of files) {
      const rel = path.relative(src, f);
      const out = path.join(dst, rel);
      fs.mkdirSync(path.dirname(out), { recursive: true });
      fs.copyFileSync(f, out);
      const st = fs.statSync(f);
      fs.utimesSync(out, st.atime, st.mtime);
    }
    // 逐文件读回哈希比对
    let verified = 0;
    for (const f of files) {
      const rel = path.relative(src, f);
      const out = path.join(dst, rel);
      const a = sha256(f), b = sha256(out);
      if (a !== b) { projectOk = false; failedFiles.push(rel); }
      else verified++;
    }
    if (projectOk) {
      console.log('    ✓ 复制并读回校验通过 (' + verified + '/' + files.length + ')');
      // 全部通过后才退役源
      fs.rmSync(src, { recursive: true, force: true });
      console.log('    ✓ 源已退役');
      emit({ event: 'migrated', project: name, source: src, target: dst,
             files: files.length, bytes, verified });
      okProjects++;
    } else {
      console.log('    ✗ 哈希不一致 ' + failedFiles.length + ' 个，保留源');
      emit({ event: 'failed', project: name, reason: 'hash-mismatch',
             failedFiles: failedFiles.slice(0, 50), count: failedFiles.length });
      failProjects++;
      failures.push({ project: name, reason: 'hash-mismatch', count: failedFiles.length });
    }
  } catch (e) {
    console.log('    ✗ 异常: ' + e.message + '（保留源）');
    emit({ event: 'failed', project: name, reason: 'exception', error: e.message });
    failProjects++;
    failures.push({ project: name, reason: e.message });
  }
}

console.log('');
console.log('=== 汇总 ===');
console.log('项目: 成功 ' + okProjects + ' / 失败 ' + failProjects);
console.log('文件: ' + totalFiles + ' 个, ' + human(totalBytes));
if (failures.length) {
  console.log('失败详情:');
  failures.forEach(f => console.log('  - ' + f.project + ': ' + f.reason));
}
emit({ event: 'summary', okProjects, failProjects, totalFiles, totalBytes,
       target: targetRoot, applied: args.apply });
logStream.end();

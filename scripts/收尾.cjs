#!/usr/bin/env node
/**
 * 收尾.cjs —— 视频项目收尾的固定动作（供 wrapup skill 调用，也可手动运行）。
 * 做三件事：刷新用量报告 → 重建项目总览 → 追加成本曲线 → 打印收尾检查单。
 * 只写入 0-工作流/ 下的统计文件，不改任何项目内容。
 */
const fs = require('fs');
const path = require('path');
const {spawnSync} = require('child_process');

const HERE = __dirname;
const OUT = path.join(HERE, '用量');
const ENV = {...process.env, NODE_OPTIONS: ''};
const run = (script) => {
  const r = spawnSync(process.execPath, [path.join(HERE, script)], {stdio: 'inherit', env: ENV});
  if (r.status !== 0) console.error(`✖ ${script} 退出码 ${r.status}`);
  return r.status === 0;
};

console.log('=== 1/3 刷新会话用量报告 ===');
const okUsage = run('会话用量报告.cjs');
console.log('\n=== 2/3 重建根项目总览 ===');
const okOverview = run('项目总览.cjs');

console.log('\n=== 3/3 追加成本曲线 ===');
let snap = null;
try { snap = JSON.parse(fs.readFileSync(path.join(OUT, '最新统计.json'), 'utf8')); } catch { }
const csvPath = path.join(OUT, '成本曲线.csv');
if (snap) {
  const h = (ms) => (ms / 3600000).toFixed(1);
  const row = [snap.generatedAt, snap.sessions, snap.inTok, snap.outTok, h(snap.llmMs), h(snap.toolMs)].join(',');
  if (!fs.existsSync(csvPath)) {
    fs.writeFileSync(csvPath, '记录时间,会话数,未缓存输入tokens,输出tokens,LLM小时,工具小时\n' + row + '\n', 'utf8');
    console.log('已新建 ' + path.relative(HERE, csvPath));
  } else {
    const prev = fs.readFileSync(csvPath, 'utf8').trim().split('\n');
    const last = prev[prev.length - 1].split(',');
    const dOut = Number(snap.outTok) - Number(last[3] || 0);
    const dLlm = (Number(h(snap.llmMs)) - Number(last[4] || 0)).toFixed(1);
    fs.appendFileSync(csvPath, row + '\n', 'utf8');
    console.log(`已追加。较上次：输出 tokens +${dOut.toLocaleString('en-US')}，LLM +${dLlm} 小时`);
  }
  console.log(`累计：${snap.sessions} 会话 · 输出 ${snap.outTok.toLocaleString('en-US')} tokens · LLM ${h(snap.llmMs)} 小时`);
} else {
  console.error('未找到 最新统计.json，成本曲线未更新（用量报告是否成功？）');
}

console.log(`
=== 收尾检查单（逐条确认，不要跳过）===

通用
  [ ] 未提交/未落盘的改动？（无 git 时列出改过的文件并标注"凭记忆"）
  [ ] 动过的服务/进程是否恢复原状或已记录？
  [ ] 只存在于对话里、没落盘的结论？
  [ ] 未验证假设是否单列一节？
  [ ] 失败任务 / 取消的作业是否记录（明确不自动重试）？

视频项目专属
  [ ] wrapup handoff 已生成（含"先读哪个文件、先跑哪条命令"）
  [ ] 项目 00_工作台与目录规则.md 的"仍未解决的大问题"已更新
  [ ] 预览仍在 预览/当前/，工作台清单.json 与实际发布一致
  [ ] 冻结交付只增不改，没有在旧交付包里继续改
  [ ] 通用经验已回流：根 AGENTS.md / 0-工作流/README.md / 项目 AGENTS.md
  [ ] 若本片结束：确认最终交付路径与验收状态（未审就写未审）
`);

if (!okUsage || !okOverview) process.exitCode = 1;

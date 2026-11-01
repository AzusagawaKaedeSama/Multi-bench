'use strict';



// Author: Codex (GPT-6), 2026-10-21. Read-only cards for registered review pages.

const fs = require('node:fs/promises');

const sync = require('node:fs');

const { randomUUID } = require('node:crypto');

const LIFECYCLE = 'E:/AgentData/ReviewHub/review-lifecycle.json';

const GRACE_MS = 24*60*60*1000;

const path = require('node:path');

const { createHash } = require('node:crypto');



const DEFAULT_REGISTRY = 'D:/2-视频制作/0-工作流/审阅服务/审阅页清单.txt';

const ALLOWED_DRIVES = new Set(['D', 'E', 'F']);

const MAX_REGISTRY_BYTES = 1024 * 1024;



function registeredPath(line) {

  let decoded;

  try { decoded = decodeURIComponent(line.trim()); } catch { return null; }

  decoded = decoded.replace(/\\/g, '/').replace(/^\//, '').replace(/^([DEF]):\//i, '$1/');

  const match = /^([DEF])\/(.+)$/i.exec(decoded);

  if (!match) return null;

  const segments = match[2].split('/');

  // Reject traversal and Windows aliases before normalization or filesystem access.

  if (segments.some(segment => segment === '..' || /[\u0000-\u001f<>:"|?*]/.test(segment) || /[. ]$/.test(segment))) return null;

  const drive = match[1].toUpperCase();

  const absolute = path.win32.normalize(`${drive}:/${match[2]}`);

  if (!/\.html?$/i.test(absolute)) return null;

  if (path.win32.parse(absolute).root.toUpperCase() !== `${drive}:\\`) return null;

  return absolute;

}



function allowedPhysicalPath(absolute) {

  const match = /^([A-Z]):\\/i.exec(absolute);

  return !!match && ALLOWED_DRIVES.has(match[1].toUpperCase());

}



async function readPrefix(file, limit) {

  const handle = await fs.open(file, 'r');

  try {

    const buffer = Buffer.alloc(limit);

    const { bytesRead } = await handle.read(buffer, 0, limit, 0);

    return buffer.subarray(0, bytesRead).toString('utf8');

  } finally { await handle.close(); }

}



function htmlText(value) {

  const entities = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

  return value.replace(/<[^>]*>/g, '').replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos|nbsp);/gi, (whole, entity) => {

    if (entity[0] !== '#') return entities[entity.toLowerCase()] ?? whole;

    const hex = entity[1].toLowerCase() === 'x';

    const number = Number.parseInt(entity.slice(hex ? 2 : 1), hex ? 16 : 10);

    return number > 0 && number <= 0x10ffff && !(number >= 0xd800 && number <= 0xdfff) ? String.fromCodePoint(number) : '';

  }).replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 160);

}



function pageTitle(prefix) {

  const source = prefix.replace(/<!--[\s\S]*?(?:-->|$)/g, '').replace(/<(script|style)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, '');

  const title = /<title\b[^>]*>([\s\S]*?)<\/title\s*>/i.exec(source);

  return title ? htmlText(title[1]) : '';

}






// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 审阅页命名：一个字的前缀 + 全角竖线 + 精炼标题，整串不超过 20 个单位（10 个全角字）。
// 规则与理由见 D:/2-视频制作/0-工作流/工作台原生壳/审阅页命名规则.md
// 前缀：视(视频) 图(图) 素(素材) 研(研究) 维(维护) 库(常驻库)
const REVIEW_NAME_RULES = [
  ['/贴图绑骨_v002/独立动画审阅.html', '视｜三动作独立绑骨', -10, { featured: true, tag: '动作预览', desc: '分束头发跟随；美波里肩手上托、帕秋莉弯指扭身、本田落地压缩。' }],
  ['/remaining25_gpu_v002/review.html', '图｜初华候选', 0, { featured: true, dynamicCandidates: true, desc: '25集截图，按光影、比例和重复镜头整理，选择自动保存。' }],
  ['/am01_full_v001/review.html', '视｜第1集AI精选', 1, { featured: true, desc: '查看第1集截图与已有人工标注，选择自动保存。' }],
  ['/scene15_review_20261020_v001/review.html', '图｜场景生图对照', 2, { desc: '对比不同LoRA与生成参数的场景表现，逐张评分。' }],
  ['/丰川祥子_截图并集审阅_预填.html', '素｜祥子截图并集', 3, { desc: '核对机器预填的人物判断，逐张纠错。' }],
  ['/若叶睦_截图并集审阅.html', '素｜睦截图并集', 4, { desc: '查看截图并集，核对人物与遗漏。' }],
  ['/睦错图归类.html', '素｜睦错图归类', 5, { desc: '重新归类混淆人物与错图，标注自动保存。' }],
  ['/阶段1_并集与审阅页/初华截图并集审阅.html', '素｜初华截图并集', 6, { desc: '查看初华截图并集，核对人物与遗漏。' }],
  ['/阶段2_补扫/补扫审阅.html', '素｜初华补扫', 7, { desc: '核对第二轮新补充的截图，标注自动保存。' }],
  ['/portfolio-review-20261021/index.html', '图｜视频关键帧', 8, { desc: '逐张审阅视频关键帧，筛选作品展示画面。' }],
  ['/reference-desks/jailbreaks-20261031/index.html', '库｜提示词参考台', -6, { featured: true, tag: '提示词参考', desc: '38 份系统提示词：七种形态家族、六段通用骨架、18 个构件矩阵、全文检索与逐对对比。' }],
  ['/deliveries/agent-ecosystem-review-20261030/v0001_偏好提炼与底模与清理.html', '维｜偏好与底模与清理', -5, { featured: true, tag: '待你审阅', desc: '12 个主题的硬约束、六个 Krea2 底模的真实精度取舍、C 盘清理清单，逐项勾选。' }],
  ['/reference-desks/xhs.html', '库｜小红书审阅台', 9],
  ['/reference-desks/bilibili.html', '库｜B站审阅台', 10],
  ['/reference-desks/pinterest.html', '库｜Pinterest审阅台', 11],
  ['/reference-desks/pixiv.html', '库｜Pixiv审阅台', 12],
  ['/reference-desks/games.html', '库｜二游素材总库', 13],
  ['/0-设计资产库/动作参考库/00_动作参考浏览.html', '库｜动作参考库', 14],
  ['/reference_annotation_20261022/review/review.html', '库｜参考图全库', 15],
  ['/261021-选哪个？/e-aeproutput/v0010_美波里自然牵动审阅.html', '视｜美波里自然牵动', 16],
  ['/ournotes-ssr-animation/v0001_ssr动画参考库.html', '库｜SSR卡面参考', 17],
  ['/deliveries/20261027-catalog-redesign/v0001_工作台目录与资产管理.html', '研｜工作台资产管理', 18],
  ['/experiments/20261027-mygo-mujica-moss-v0001/v0001_moss_checkpoint_review.html', '研｜MyGO角色LoRA', 19],
  ['/v0002_梗素材索引/梗素材库_v0002审阅.html', '素｜梗素材库v0002', 20],
  ['/deliveries/workflow-distillation-20261028/v0001_技能与工作流.html', '研｜技能盘点与复用', 21],
  ['/deliveries/reference-research-skill-20261028/v0001_创作参考调查.html', '研｜创作参考调查', 22],
  ['/deliveries/bili-ai-workflows-20261029/v0001_ai收藏研究与实测.html', '研｜B站AI工作流', 23],
  ['/4-b站下载/1-批量参考学习/v0001_b站批量参考学习_视频索引.html', '素｜B站批量参考', 24],
  ['/261030-多组mujica&mygo图片视频/e-aeproutput/v0001_图集与视频审阅.html', '视｜Mujica图集与H3', 25],
  ['/deliveries/character-voice-workbench/v0014_角色配音工作台.html', '维｜角色配音试听', 26],
  ['/v0040_全片使用原图/原图浏览.html', '素｜剪切线全片原图', 27],
  ['/deliveries/agent-ecosystem-review-20261030/v0002_清理结果与真人规则清单.html', '维｜清理与规则清单', 28],
  ['/deliveries/agent-ecosystem-review-20261030/v0004_勾选即删除.html', '维｜C盘与底模清理', 29],
  ['/deliveries/edgerunners-op-style-analysis/v0001_边缘行者op视觉拆解审阅.html', '研｜边缘行者OP拆解', 30],
  ['/deliveries/moss-k2-reference-workflow/v0001_moss-k2参考图工作流拆解审阅.html', '研｜参考图驱动拆解', 31],
  ['/deliveries/garment-lib-edgerunners/v0001_服饰库v001与边缘行者走查审阅.html', '素｜服饰库边缘行者', 32],
  ['/1-ai生图工作流/游戏资产/审阅页/v0001_游戏资产解包盘点.html', '素｜游戏资产解包', 33],
  // 作者：workbench-ux · 2026-11-01。图片门禁确认页改成走 3958 服务（同源），
  // 原来给的是本地文件路径 → 3900 页面打开后 fetch http://127.0.0.1:3958 会撞 CORS / 连不上。
  // 作者：workbench-ux · 2026-11-01。门禁确认也走查看器（同一套放大/复制/评分）；
  // 实时全量写回 pending.json 的入口仍是 http://127.0.0.1:3958/，见图片门禁 README。
  ['/0-工作流/图片门禁/确认页.html', '维｜出图确认门禁', 34, {
    url: 'http://127.0.0.1:3955/view?doc=docs/gate-pending.json',
    desc: '逐张确认出图：点图放大，通过 / 打回记到审阅存档；实时全量入口在 3958。',
    tag: '门禁',
  }],
  ['/1-自媒体工坊/复盘.html', '研｜账号复盘', 35],
  ['/1-自媒体工坊/个人展示.html', '图｜个人作品集', 36],
  // 作者：lead · 2026-11-01。LoRA 质感实验逐张打分页（走 viewer，逐项 1–5 分，点一下即存）。
  ['/审阅服务/viewer/ref01-lora-ab.html', '图｜ref01LoRA打分', 39, {
    url: 'http://127.0.0.1:3955/view?doc=docs/ref01-lora-ab.json',
    tag: 'LoRA对照',
    desc: '25 张同 seed 出图（16 单 LoRA + 9 组叠加），每张单独打分，点一下即存。',
  }],
  // 作者：lead · 2026-11-01。MOSS 原图风格反推的 19 张样张审阅页，走 3955 审阅服务。
  // 作者：workbench-ux · 2026-11-01。入口改成 3955 的查看器短链（页面统一到 viewer，一份实现）。
  ['/261101-moss风格反推与人像重绘/e-出图样张/moss风格族样张审阅.html', '图｜MOSS风格反推', 37, {
    tag: '生图样张',
    desc: '19 张样张覆盖 14 个风格族，逐张 1–5 分打分并写修改意见。',
    url: 'http://127.0.0.1:3955/view?doc=docs/moss-style.json',
  }],
  // 作者：opencode-provider · 2026-11-01。三张参考图的复现对照（左原图右出图），按 lead 要求命名。
  ['/261101-风格参考复现/e-出图样张/风格参考复现审阅.html', '图｜风格参考复现', 38, {
    tag: '复现对照',
    desc: '三张参考图各两版：左原图右出图，比发色瞳色、构图重心、光位、材质，第三张单列 UI 装饰层。',
  }],
  // 作者：opencode-provider · 2026-11-01。ref01 五组写实 LoRA 同 seed 对照。
  ['/261101-风格参考复现/e-出图样张/ref01写实lora对照审阅.html', '图｜ref01写实对照', 39, {
    tag: 'LoRA对照',
    desc: '5 组同 seed：无 LoRA / Realism0.6 / A2R0.8 / 组合 / detail0.5，含脸、耳颈、布料三处 1:1 裁切。',
  }],
  // 作者：opencode-provider · 2026-11-01。ref01 质感 A/B：14 个社区 LoRA 同 seed。
  ['/261101-风格参考复现/e-出图样张/ref01质感ab审阅.html', '图｜ref01质感AB', 40, {
    tag: 'LoRA对照',
    desc: '15 张同 seed（基线 + 14 个社区 LoRA）+ 推荐组合，含 15 格联系表与 3×15 裁切条。',
  }],
  // 作者：opencode-provider · 2026-11-01。画风矩阵试片：MMD / CG / 半厚涂 / 赛璐璐。
  ['/0-工作流/审阅服务/viewer/ref01-style-matrix.html', '图｜画风矩阵试片', 41, {
    tag: '画风矩阵',
    desc: '10 张：MMD 手办 / 游戏 CG / 半厚涂 / 赛璐璐，初华与立希各一组角色 LoRA，末两张原创成年质感研究。',
    url: 'http://127.0.0.1:3955/view?doc=docs/ref01-style-matrix.json',
  }],
  // 作者：opencode-provider · 2026-11-01。热门角色 tag 试片：MMD 手办 / 三画风对照 / NSFW 成年角色。
  ['/0-工作流/审阅服务/viewer/char-tags-matrix.html', '图｜热门角色试片', 42, {
    tag: '角色tag',
    desc: '10 张：4 个热门角色 MMD 手办、初音三画风对照、3 张成年角色 NSFW；tag 来自作品角色标签库。',
    url: 'http://127.0.0.1:3955/view?doc=docs/char-tags-matrix.json',
  }],
  // 作者：opencode-provider · 2026-11-01。特征标签数量对照：0/3/6/8 个 + 只有特征。
  ['/0-工作流/审阅服务/viewer/char-feature-count.html', '图｜特征标签数量', 43, {
    tag: '标签对照',
    desc: '5 张：hatsune_miku + 0/3/6/8 个特征标签，以及一组只有特征不给角色 tag。',
    url: 'http://127.0.0.1:3955/view?doc=docs/char-feature-count.json',
  }],
  // 作者：opencode-provider · 2026-11-01。多参考图验证：4-bit 编码器 8 组全部 OOM。
  ['/0-工作流/审阅服务/viewer/multiref-w4a8.html', '图｜多参考图验证', 44, {
    tag: '显存实验',
    desc: 'E0–E6 共 8 组：w4a8 与 int8、1/2/3 张参考、896/768/640、CLIP 放 CPU，全部 OOM，含峰值显存表。',
    url: 'http://127.0.0.1:3955/view?doc=docs/multiref-w4a8.json',
  }],
];

const DEFAULT_REVIEW_DESC = '逐张查看与标注，结果自动保存。';

/** 按全角宽度截断到 20 个单位（汉字 2、ASCII 1）。没命中规则的名字也要过这一关。 */
function clampReviewName(value) {
  const text = String(value || '').trim();
  let units = 0;
  for (const ch of text) units += ch.charCodeAt(0) <= 0x7f ? 1 : 2;
  if (units <= 20) return text;

  let acc = 0;
  let cut = '';
  for (const ch of text) {
    const w = ch.charCodeAt(0) <= 0x7f ? 1 : 2;
    if (acc + w > 18) break;
    acc += w;
    cut += ch;
  }
  return cut + '…';
}

function humanMetadata(absolute, title) {
  const key = absolute.replace(/\\/g, '/').toLowerCase();

  for (const [suffix, name, order, extra] of REVIEW_NAME_RULES) {
    if (key.endsWith(suffix)) {
      return { name, desc: DEFAULT_REVIEW_DESC, featured: false, order, ...(extra || {}) };
    }
  }

  return {
    name: clampReviewName(title || path.win32.basename(absolute, path.win32.extname(absolute))),
    desc: DEFAULT_REVIEW_DESC,
    featured: false,
    order: 100,
  };
}

async function candidateLimit(physical) {

  try {

    const catalog = path.win32.join(path.win32.dirname(physical), 'review_data', 'catalog.json');

    const catalogPhysical = await fs.realpath(catalog);

    if (!allowedPhysicalPath(catalogPhysical)) return null;

    const stat = await fs.stat(catalogPhysical);

    if (!stat.isFile() || stat.size > MAX_REGISTRY_BYTES) return null;

    const count = JSON.parse(await fs.readFile(catalogPhysical, 'utf8')).curation_default_limit;

    return Number.isInteger(count) && count > 0 && count <= 100000 ? count : null;

  } catch { return null; }

}



async function getReviewPages({ registryPath = DEFAULT_REGISTRY, port = 3955, lifecyclePath = LIFECYCLE, includeArchived = true, now = Date.now() } = {}) {

  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new TypeError('Invalid review service port');

  let registry;

  try {

    const stat = await fs.stat(registryPath);

    if (!stat.isFile() || stat.size > MAX_REGISTRY_BYTES) return [];

    registry = await fs.readFile(registryPath, 'utf8');

  } catch (error) {

    if (error.code === 'ENOENT') return [];

    throw error;

  }

  const seen = new Set();

  const result = [];

  const lines = registry.replace(/^\uFEFF/, '').split(/\r?\n/);

  for (let index = 0; index < lines.length; index++) {

    const line = lines[index].trim();

    if (!line || line.startsWith('#')) continue;

    const absolute = registeredPath(line);

    if (!absolute) continue;

    let physical = absolute;

    let stat = null;

    let available = false;

    try {

      physical = await fs.realpath(absolute);

      if (!allowedPhysicalPath(physical) || !/\.html?$/i.test(physical)) continue;

      stat = await fs.stat(physical);

      if (!stat.isFile()) continue;

      available = true;

    } catch (error) {

      if (!['ENOENT', 'ENOTDIR', 'EACCES', 'EPERM'].includes(error.code)) throw error;

    }

    const canonical = path.win32.normalize(physical).toLowerCase();

    if (seen.has(canonical)) continue;

    seen.add(canonical);

    let title = '';

    if (available) {

      try { title = pageTitle(await readPrefix(physical, 64 * 1024)); }

      catch { available = false; }

    }

    const metadata = humanMetadata(absolute, title);

    if (metadata.dynamicCandidates && available) {

      const count = await candidateLimit(physical);

      if (count !== null) metadata.name = `图｜初华${count}张候选`;

    }

    const urlPath = absolute.replace(/\\/g, '/').replace(/^([A-Z]):\//i, '$1/');

    result.push({

      id: `review-${createHash('sha256').update(canonical).digest('hex').slice(0, 16)}`,

      name: clampReviewName(metadata.name),

      page_path: physical,

      folder_path: path.win32.dirname(physical),

      resource_url: '/open/review-' + createHash('sha256').update(canonical).digest('hex').slice(0,16),

      desc: metadata.desc,

      // 规则里显式给了 url（例如图片门禁确认页跑在 3958 自己的服务上）就用它，
      // 否则按 3900 的静态路径映射生成。
      url: metadata.url || `http://127.0.0.1:${port}/${urlPath.split('/').map(encodeURIComponent).join('/')}`,

      available,

      featured: metadata.featured,

      tag: metadata.tag || null,

      updated_at: stat ? stat.mtime.toISOString() : null,

      _order: metadata.order,

      _registryIndex: index,

    });

  }

  const lifecycle=readLifecycle(lifecyclePath);

  return result.sort((a,b)=>a._order-b._order||a._registryIndex-b._registryIndex).map(({_order,_registryIndex,...entry})=>({...entry,...lifecycleView(lifecycle.entries[entry.id],now)})).filter(p=>includeArchived||p.review_status!=='archived');

}



// 作者：Codex（GPT-6）· 2026-10-22。完成时间只来自明确的完成操作。

function readLifecycle(file=LIFECYCLE){

 try{const data=sync.readFileSync(file,'utf8');if(Buffer.byteLength(data)>1024*1024)throw Error('审阅状态文件超过上限');const v=JSON.parse(data);if(v.version!==1||!v.entries||typeof v.entries!=='object'||Array.isArray(v.entries))throw Error('审阅状态文件格式需要检查');return v;}

 catch(e){if(e.code==='ENOENT')return{author:'Codex (GPT-6)',version:1,entries:{}};throw e;}

}

function lifecycleView(record,now=Date.now()){

 const r=record||{},completed=Date.parse(r.completed_at||''),manual=Date.parse(r.archived_at||'');

 const due=r.state==='completed'&&Number.isFinite(completed)?completed+GRACE_MS:null;

 const archived=r.state==='archived'||due!==null&&now>=due;

 return{review_status:archived?'archived':r.state==='completed'?'completed':'active',status_revision:r.revision||0,completed_at:Number.isFinite(completed)?new Date(completed).toISOString():null,archive_due_at:due===null?null:new Date(due).toISOString(),archived_at:archived?(Number.isFinite(manual)?new Date(manual).toISOString():new Date(due??now).toISOString()):null};

}

async function changeReviewStatus(input,options={}){

 if(!input||!/^review-[a-f0-9]{16}$/.test(input.id||'')||!['complete','archive','reopen'].includes(input.action)||!Number.isInteger(input.revision)||input.revision<0||Object.keys(input).some(k=>!['id','action','revision'].includes(k))){const e=Error('请提交有效项目、操作和状态版本');e.status=400;throw e;}

 const pages=await getReviewPages({...options,includeArchived:true});
 if(!pages.some(p=>p.id===input.id)){
  // 本人已发布记录使用同一人工归档账本；原帖型记录也有稳定审阅ID。
  const catalog=options.catalogEntries?await options.catalogEntries():require('./catalog-store.cjs').getCatalog({scope:'all',kind:'published_work',limit:1000,include_versions:true}).families;
  const registered=catalog.some(f=>f.kind==='published_work'&&/^published-[a-f0-9]{24}$/.test(f.id)&&f.versions?.some(v=>v.review_id===input.id&&v.publication_status==='published'&&v.publication_evidence?.some(p=>['own_published_note','official_export'].includes(p.confirmation_method))));
  if(!registered){const e=Error('审阅项目未登记');e.status=404;throw e;}
 }

 const file=options.lifecyclePath||LIFECYCLE,all=readLifecycle(file),old=all.entries[input.id]||{};

 if((old.revision||0)!==input.revision){const e=Error('状态已被更新，请刷新后再操作');e.status=409;throw e;}

 const at=new Date(options.now??Date.now()).toISOString(),view=lifecycleView(old,options.now??Date.now());

 let next={author:'Codex (GPT-6)',revision:(old.revision||0)+1,updated_at:at,state:'active',archive_reason:null};

 if(input.action==='complete')next={...next,state:'completed',completed_at:view.completed_at||at};

 if(input.action==='archive')next={...next,state:'archived',completed_at:view.completed_at||at,archived_at:at,archive_reason:'manual'};

 all.entries[input.id]=next;sync.mkdirSync(path.dirname(file),{recursive:true});const tmp=file+'.'+randomUUID()+'.tmp';

 try{sync.writeFileSync(tmp,JSON.stringify(all,null,2)+'\n',{encoding:'utf8',flag:'wx'});sync.renameSync(tmp,file);}finally{if(sync.existsSync(tmp))sync.unlinkSync(tmp);}

 return{ok:true,id:input.id,...lifecycleView(next,options.now??Date.now())};

}

module.exports = { getReviewPages,changeReviewStatus,lifecycleView,registeredPath,GRACE_MS };




// 作者：Codex（GPT-6）· 2026-10-27
// SQLite 查询目录；已有 JSON 写入者继续运行，媒体及人工标注保留原位。
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');

const SCHEMA = 3;
const IMPORTER_VERSION = '20261027.11';
const GRACE_MS = 24 * 60 * 60 * 1000;
const KINDS = Object.freeze({published_work:'已发布作品',draft_work:'创作项目',asset_record:'素材新增记录',research_record:'自研记录',operations_record:'运行维护记录'});
const MAX_SOURCE_BYTES = 12 * 1024 * 1024;
const cap = (v, max=2000) => typeof v === 'string' ? v.slice(0,max) : '';
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const absolute = v => typeof v === 'string' && (/^[a-z]:[\\/]/i.test(v) || path.isAbsolute(v));
const iso = value => { const n=Date.parse(value||''); return Number.isFinite(n)?new Date(n).toISOString():null; };
const parse = (v,fallback=null) => {try{return JSON.parse(v);}catch{return fallback;}};
function safeUrl(value, published=false) {
  try {const u=new URL(cap(value,8192));if(!['http:','https:'].includes(u.protocol)||u.username||u.password)return null;
    if(published){if(['localhost','127.0.0.1','::1'].includes(u.hostname))return null;u.search='';u.hash='';}
    return u.href;
  } catch {return null;}
}
function allowed(source, keys) {const out={};for(const k of keys)if(source&&source[k]!==undefined)out[k]=source[k];return out;}
function cleanFile(f) {
  if(!f||typeof f!=='object'||!absolute(f.path))return null;
  const result=allowed(f,['id','path','title','kind','sha256','bytes','source_path','people','role','status','description']);
  result.id=cap(result.id||'file-'+hash(f.path).slice(0,16),128);result.path=cap(f.path,32768);
  result.title=cap(f.title||path.win32.basename(f.path),500);result.kind=cap(f.kind||'file',50);
  if(!/^[a-f0-9]{64}$/i.test(result.sha256||''))delete result.sha256;
  if(!Number.isFinite(result.bytes)||result.bytes<0)delete result.bytes;
  if(!absolute(result.source_path))delete result.source_path;
  return result;
}
function cleanVersion(v) {
  const out=allowed(v,['version','parent_version','completed_at','completed_time_source','source_manifest','source_thread','review_id','summary','folder','registered_at','source_folder','author']);
  out.version=cap(v.version,80);out.review_url=safeUrl(v.review_url);out.summary=cap(v.summary,6000);
  out.files=(Array.isArray(v.files)?v.files:[]).slice(0,10000).map(cleanFile).filter(Boolean);
  return out;
}
function reviewView(record={},now=Date.now()) {
  const done=iso(record.completed_at),manual=iso(record.archived_at);
  const due=record.state==='completed'&&done?Date.parse(done)+GRACE_MS:null;
  const archived=record.state==='archived'||due!==null&&now>=due;
  return {scope:archived?'archived':'active',review_state:archived?'archived':record.state==='completed'?'completed':'active',review_status:archived?'archived':record.state==='completed'?'completed':'active',status_revision:record.revision||0,review_completed_at:done,archive_due_at:due===null?null:new Date(due).toISOString(),archived_at:archived?(manual||new Date(due??now).toISOString()):null,archive_reason:archived?(record.state==='archived'?(record.archive_reason||'manual'):'review_completed_24h'):null};
}
function cleanPublication(post, root, policy) {
  if(!post||typeof post!=='object'||post.status!=='published')return null;
  const url=safeUrl(post.url||post.publication_url,true),time=iso(post.published_at||post.publication_time||post.observed_at);
  const explicit=post.confirmed===true||post.publication_confirmed===true;
  const platform=cap(post.platform||'xiaohongshu',100),accountId=String(post.account_id||post.author_id||'');
  const accounts=Array.isArray(policy.accounts)?policy.accounts:[{...policy,platform:'xiaohongshu'}];
  const account=accounts.find(a=>a.platform===platform&&String(a.account_id)===accountId&&a.account_confirmed===true);
  const historical=post.source==='official_export'&&post.visibility==='historical_public'&&post.scope==='archived'&&!!post.evidence?.validation_sha256;
  const collaborator=post.platform==='bilibili'&&post.publication_role==='collaborator'&&String(post.primary_author?.id)===String(post.author_id)&&post.evidence?.method==='official_api'&&post.collaboration?.staff?.some(s=>String(s.mid||s.id)===accountId)&&!!post.collaboration?.evidence?.sha256;
  const importer=(post.source==='own_published_note'&&post.visibility==='public'||historical)&&!!account&&(String(post.author_id)===accountId||collaborator)&&!!iso(post.observed_at);
  if(!url||!time||!(explicit||importer))return null;
  const media=(Array.isArray(post.media)?post.media:[]).slice(0,100).map(m=>({type:['image','video'].includes(m.type)?m.type:null,order:Number.isSafeInteger(m.order)?m.order:null,width:Number.isFinite(m.width)?m.width:null,height:Number.isFinite(m.height)?m.height:null,platform_copy:m.platform_copy===true,storage_role:cap(m.storage_role,100),bvid:cap(m.bvid,40),cid:Number.isSafeInteger(m.cid)?m.cid:null,page:Number.isSafeInteger(m.page)?m.page:null,part:cap(m.part,500),duration:Number.isFinite(m.duration)?m.duration:null,url:safeUrl(m.url,true),path:absolute(m.path)?cap(m.path,32768):null,sha256:/^[a-f0-9]{64}$/i.test(m.sha256||'')?m.sha256:null,match_method:cap(m.match_method,100),selected:m.selected===true,selected_original_path:absolute(m.selected_original_path)?cap(m.selected_original_path,32768):null,associations:(Array.isArray(m.associations)?m.associations:[]).slice(0,100).map(a=>allowed(a,['path','family_id','project_id','version','sha256','id','match_method','match_evidence','confirmed_at','validation_path','validation_sha256','sha256_verified_at','current_check','observed_bytes','observed_mtime_ns'])),matched_files:(Array.isArray(m.matched_files)?m.matched_files:[]).filter(absolute).slice(0,100)}));
  return {id:cap(post.publication_key||post.id||'publication-'+hash(url).slice(0,16),128),post_id:cap(post.post_id||post.id,128),publication_key:cap(post.publication_key,200),url,published_at:iso(post.published_at||post.publication_time),observed_at:iso(post.observed_at),confirmed_at:time,confirmed:true,confirmation_method:importer?(historical?'official_export':'own_published_note'):'explicit_confirmation',source:importer?(historical?'official_export':'own_published_note'):'explicit_confirmation',author_id:String(post.author_id||accountId),primary_author:post.primary_author?{id:cap(String(post.primary_author.id),100),name:cap(post.primary_author.name,200)}:null,publication_role:collaborator?'collaborator':'primary_author',collaboration:collaborator?{account_role:cap(post.collaboration.account_role,200),staff:(post.collaboration.staff||[]).slice(0,100).map(s=>({mid:cap(String(s.mid||s.id),100),name:cap(s.name,200),title:cap(s.title,200)})),evidence:allowed(post.collaboration.evidence,['method','path','sha256','dom_path','dom_sha256'])}:null,visibility:historical?'historical_public':post.visibility==='public'?'public':null,identity_observed:post.identity_observed===true,status:'published',platform:cap(post.platform||new URL(url).hostname,100),account_id:accountId,account_name:cap(post.account_name||account?.account_name,200),title:cap(post.title,500),edited_at_display:cap(post.edited_at_display,200),published_at_display:cap(post.published_at_display,200),source_dom:absolute(post.source_dom)?post.source_dom:null,source_api:absolute(post.source_api)?post.source_api:null,api_type:cap(post.api_type,100),body:post.body_observed===true?cap(post.body,100000):null,body_observed:post.body_observed===true,media_observed:post.media_observed===true,completeness:['metadata_only','media_pending','complete'].includes(post.completeness)?post.completeness:'complete',local_association:post.local_association==='matched'?'matched':'pending',declared_media_count:Number.isSafeInteger(post.declared_media_count)?post.declared_media_count:null,content_type:cap(post.content_type,80),cover_url:safeUrl(post.cover_url,true),evidence:post.evidence?{method:cap(post.evidence.method,100),page_url:safeUrl(post.evidence.page_url,true),observed_at:iso(post.evidence.observed_at),...allowed(post.evidence,['source_list_path','source_list_sha256','validation_path','validation_sha256','account_path','archive_verified_at','source_dom','source_api'])}:null,copy_style_explicit:(Array.isArray(post.copy_style_explicit)?post.copy_style_explicit:[]).slice(0,30).map(x=>cap(x,200)),record_directory:absolute(post.record_directory)?post.record_directory:null,text_path:absolute(post.text_path)?post.text_path:null,review_id:cap(post.review_id,128),scope:post.scope==='archived'?'archived':'active',archived_at:iso(post.archived_at),archive_reason:cap(post.archive_reason,200),family_id:cap(post.family_id||post.family||post.project_id,128),version:cap(post.version,80),whole_version_confirmed:explicit&&post.whole_version_confirmed===true,media};
}
const creativeMedia=f=>['image','video'].includes(f.kind)||/\.(?:png|jpe?g|webp|gif|avif|mp4|mov|m4v|mkv|webm)$/i.test(f.path);
const mediaMatchesFile=(m,f)=>(m.sha256&&m.sha256===f.sha256)||m.path===f.path||m.matched_files.includes(f.path)||m.matched_files.includes(f.source_path);
function matchingPublication(pub,family,version) {
  if(pub.family_id&&pub.version)return pub.family_id===family.id&&pub.version===version.version;
  return pub.media.some(m=>version.files.some(f=>mediaMatchesFile(m,f)));
}
function publishedFiles(family,version,publications,creative){
  if(!creative)return {published_files:[],published_file_count:0,published_file_match_status:'not_applicable'};
  const whole=publications.some(p=>p.whole_version_confirmed&&p.family_id===family.id&&p.version===version.version);
  const files=version.files.filter(f=>creativeMedia(f)&&(whole||publications.some(p=>p.media.some(m=>mediaMatchesFile(m,f)))));
  return {published_files:files,published_file_count:files.length,published_file_match_status:whole?'whole_version_confirmed':files.length?'matched':publications.length?'unmatched':'unverified'};
}
function classify(family,version,policy,pubs) {
  if(pubs.some(p=>(family.publication_record===true&&p.id===family.publication_id)||(['own_published_note','official_export'].includes(p.confirmation_method)&&family.id==='published-'+hash(p.publication_key||p.id).slice(0,24))))return 'published_work';
  let kind=policy.families?.[family.id]?.kind||family.kind||family.category;
  if(kind==='published_work')kind='draft_work';
  if(!Object.hasOwn(KINDS,kind))kind=family.work_type==='image_collection'||family.work_type==='video'?'draft_work':'operations_record';
  // 发布确认只提升明确的创作项目，素材视频及训练预览不会因媒体后缀进入作品。
  if(kind==='draft_work'&&version.files.some(creativeMedia)&&pubs.some(p=>matchingPublication(p,family,version)))return 'published_work';
  return kind;
}
function publicationFamilies(families,pubs,policy){
  const result=[...families];
  for(const pub of pubs){
    if(!pub.account_id||!['xiaohongshu','bilibili'].includes(pub.platform)||!['own_published_note','official_export'].includes(pub.confirmation_method))continue;
    // Existing creative deliveries retain their IDs. A real post without a
    // current creative delivery gets its own stable, post-addressed record.
    const existing=families.find(f=>{
      const latest=(f.versions||[]).find(v=>v.version===f.latest_version);
      return latest&&classify(f,cleanVersion(latest),policy,[pub])==='published_work';
    });
    // New multi-account imports remain addressed by platform/account/post even
    // after a local source is matched. Their archive ID must never migrate to
    // a candidate batch's unrelated review ID.
    if(existing&&!pub.publication_key)continue;
    const id='published-'+hash(pub.publication_key||pub.id).slice(0,24),version='v0001';
    pub.family_id=id;pub.version=version;
    if(result.some(f=>f.id===id))continue;
    const files=[],seen=new Set();
    for(const media of pub.media){
      const paths=media.matched_files.length?[media.matched_files.includes(media.selected_original_path)?media.selected_original_path:media.matched_files[0]]:media.path?[media.path]:[];
      for(const p of paths){if(seen.has(p))continue;seen.add(p);const association=media.associations.find(a=>a.path===p);
        files.push({id:'file-'+hash(p).slice(0,16),path:p,title:path.win32.basename(p),kind:media.type||(/\.(?:mp4|mkv|mov|webm)$/i.test(p)?'video':'image'),sha256:association?.sha256||media.sha256||undefined,role:media.platform_copy&&!media.matched_files.length?'published_platform_copy':'published_media'});
      }
    }
    if(pub.text_path)files.push({id:'file-'+hash(pub.text_path).slice(0,16),path:pub.text_path,title:'已发布文案',kind:'document',role:'published_caption'});
    result.push({id,name:pub.title||pub.account_name+' · '+pub.post_id,aliases:[pub.account_name,pub.platform,pub.post_id].filter(Boolean),kind:'draft_work',publication_record:true,publication_id:pub.id,latest_version:version,versions:[{version,completed_at:pub.published_at||pub.observed_at,review_id:pub.review_id||'review-'+hash(pub.id).slice(0,16),review_url:pub.url,folder:pub.record_directory||(pub.text_path?path.win32.dirname(pub.text_path):undefined),summary:pub.account_name+' · '+pub.platform+' · '+(pub.publication_role==='collaborator'?'合作投稿 · '+(pub.collaboration?.account_role||'参与制作')+' · ':'')+(pub.body_observed?'原文已同步':'发布列表记录')+' · '+(pub.media_observed?'发布媒体已记录':'媒体待关联'),source_manifest:pub.text_path,files}]});
  }
  return result;
}
function readSource(file, fallback) {
  try {const before=fs.statSync(file);if(before.size>MAX_SOURCE_BYTES)throw Error('目录源文件超过大小上限：'+path.basename(file));
    const bytes=fs.readFileSync(file),after=fs.statSync(file);
    if(before.size!==after.size||before.mtimeMs!==after.mtimeMs)throw Error('目录源文件正在写入：'+path.basename(file));
    return {path:file,size:after.size,mtime_ms:after.mtimeMs,sha256:hash(bytes),value:JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/,''))};
  } catch(e) {if(e.code==='ENOENT')return {path:file,size:0,mtime_ms:0,sha256:'missing',value:fallback};throw e;}
}
function createCatalogStore(options={}) {
  const baseDir=path.resolve(options.baseDir||__dirname);
  const databaseDir=path.resolve(options.databaseDir||path.join(baseDir,'database'));
  const dbPath=path.resolve(options.dbPath||path.join(databaseDir,'catalog.sqlite3'));
  const exportRoot=path.resolve(options.exportRoot||path.join(databaseDir,'exports'));
  const sourcePaths={deliveries:path.join(baseDir,'project-catalog','deliveries.json'),projects:path.join(baseDir,'project-catalog','catalog.json'),lifecycle:path.join(baseDir,'review-lifecycle.json'),publications:path.join(baseDir,'project-catalog','publications.json'),publication_policy:path.join(baseDir,'project-catalog','publication-policy.json'),policy:path.join(databaseDir,'catalog-policy.json'),...options.sources};
  fs.mkdirSync(path.dirname(dbPath),{recursive:true});
  let db=new DatabaseSync(dbPath);
  db.exec('PRAGMA busy_timeout=2000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;');
  const oldVersion=Number(db.prepare('PRAGMA user_version').get().user_version);
  if(oldVersion>SCHEMA)throw Error('目录数据库版本比当前程序新');
  if(oldVersion<SCHEMA){
    if(fs.statSync(dbPath).size>4096&&oldVersion>0){const backup=dbPath+'.before-v'+SCHEMA+'-'+Date.now();db.exec("VACUUM INTO '"+backup.replace(/'/g,"''")+"'");}
    db.exec(`BEGIN IMMEDIATE;
      CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sources(name TEXT PRIMARY KEY,path TEXT NOT NULL,sha256 TEXT NOT NULL,mtime_ms REAL NOT NULL,size INTEGER NOT NULL,imported_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS families(id TEXT PRIMARY KEY,name TEXT NOT NULL,kind TEXT NOT NULL,latest_version TEXT, recommended_version TEXT, aliases_json TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS versions(family_id TEXT NOT NULL,version TEXT NOT NULL,parent_version TEXT,completed_at TEXT,review_id TEXT,data_json TEXT NOT NULL,PRIMARY KEY(family_id,version),FOREIGN KEY(family_id) REFERENCES families(id) ON DELETE CASCADE);
      CREATE TABLE IF NOT EXISTS files(family_id TEXT NOT NULL,version TEXT NOT NULL,id TEXT NOT NULL,path TEXT NOT NULL,sha256 TEXT,bytes INTEGER,data_json TEXT NOT NULL,PRIMARY KEY(family_id,version,id),FOREIGN KEY(family_id,version) REFERENCES versions(family_id,version) ON DELETE CASCADE);
      CREATE TABLE IF NOT EXISTS reviews(id TEXT PRIMARY KEY,state TEXT NOT NULL,completed_at TEXT,archived_at TEXT,revision INTEGER NOT NULL,updated_at TEXT);
      CREATE TABLE IF NOT EXISTS publications(id TEXT PRIMARY KEY,data_json TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY,name TEXT NOT NULL,path TEXT,data_json TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS project_files(project_id TEXT NOT NULL,id TEXT NOT NULL,role TEXT NOT NULL,path TEXT NOT NULL,data_json TEXT NOT NULL,PRIMARY KEY(project_id,id,role),FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE);
      CREATE INDEX IF NOT EXISTS versions_review_idx ON versions(review_id);
      CREATE INDEX IF NOT EXISTS files_sha_idx ON files(sha256);
      PRAGMA user_version=3;
      COMMIT;`);
  }
  if(!db.prepare('PRAGMA table_info(reviews)').all().some(c=>c.name==='archive_reason'))db.exec('ALTER TABLE reviews ADD COLUMN archive_reason TEXT');
  // 作者：dsh（DeepSeek-V4.1-Flash）· 2026-10-31：node:sqlite 不一定编入 fts5；库里有别的进程留下的 family_search 虚表时，
  // CREATE ... IF NOT EXISTS 会静默通过并把 fts 置真，随后 DELETE/INSERT 报 no such module: fts5，sync 整体失败，
  // /api/review-pages 随之 500。改为实测可读写，能力不足即降级为不维护该索引。
  let fts=false;
  try{
    const existing=db.prepare("SELECT name FROM sqlite_master WHERE name='family_search'").get();
    if(!existing){db.exec('CREATE VIRTUAL TABLE family_search USING fts5(id UNINDEXED,name,aliases);');fts=true;}
    else{db.prepare('SELECT count(*) FROM family_search').get();fts=true;}
  }catch{fts=false;}
  const readMeta=(key,from=db)=>from.prepare('SELECT value FROM meta WHERE key=?').get(key)?.value;
  const sourceCache=new Map();
  function cachedSource(key,file,fallback){
    let signature;try{const s=fs.statSync(file,{bigint:true});signature=[s.size,s.mtimeNs,s.ctimeNs,s.ino].join(':');}catch(e){if(e.code!=='ENOENT')throw e;signature='missing';}
    const old=sourceCache.get(key);if(old?.signature===signature)return old.source;
    const source=readSource(file,fallback);sourceCache.set(key,{signature,source});return source;
  }
  function sync() {
    const defaults={deliveries:{families:[]},projects:{records:[]},lifecycle:{entries:{}},publications:{posts:[]},publication_policy:{},policy:{families:{}}};
    const sources=Object.fromEntries(Object.entries(sourcePaths).map(([key,file])=>[key,cachedSource(key,file,defaults[key])]));
    const old=new Map(db.prepare('SELECT name,sha256 FROM sources').all().map(s=>[s.name,s.sha256]));
    if(readMeta('importer_version')===IMPORTER_VERSION&&Object.entries(sources).every(([key,s])=>old.get(key)===s.sha256))return false;
    const deliveries=sources.deliveries.value,lifecycle=sources.lifecycle.value,policy=sources.policy.value;
    if(!Array.isArray(deliveries.families)||!lifecycle.entries||typeof lifecycle.entries!=='object'||Array.isArray(lifecycle.entries)||!policy.families||typeof policy.families!=='object')throw Error('目录源文件格式需要核对');
    const pubs=(Array.isArray(sources.publications.value.posts)?sources.publications.value.posts:[]).slice(0,10000).map(p=>cleanPublication(p,sources.publications.value,sources.publication_policy.value)).filter(Boolean);
    const families=publicationFamilies(deliveries.families,pubs,policy);
    const reviewEntries={...lifecycle.entries};
    for(const p of pubs){const reviewId=p.review_id||'review-'+hash(p.id).slice(0,16);if(p.scope==='archived'&&!reviewEntries[reviewId])reviewEntries[reviewId]={state:'archived',archived_at:p.archived_at||p.observed_at,archive_reason:p.archive_reason||'manual',revision:1};}
    const at=new Date().toISOString();
    db.exec('BEGIN IMMEDIATE');
    try {
      db.exec('DELETE FROM files; DELETE FROM versions; DELETE FROM families; DELETE FROM reviews; DELETE FROM publications; DELETE FROM project_files; DELETE FROM projects;');
      if(fts)db.exec('DELETE FROM family_search');
      const familyInsert=db.prepare('INSERT INTO families VALUES(?,?,?,?,?,?)'),versionInsert=db.prepare('INSERT INTO versions VALUES(?,?,?,?,?,?)'),fileInsert=db.prepare('INSERT INTO files VALUES(?,?,?,?,?,?,?)');
      for(const f of families.slice(0,10000)){
        if(!f||typeof f.id!=='string'||!Array.isArray(f.versions))continue;
        const clean={id:cap(f.id,128),name:cap(f.name||f.id,500),aliases:(Array.isArray(f.aliases)?f.aliases:[]).filter(x=>typeof x==='string').slice(0,100).map(x=>cap(x,500))};
        const versions=f.versions.slice(0,1000).map(cleanVersion).filter(v=>v.version);
        const latest=versions.find(v=>v.version===f.latest_version);
        // 未登记的 latest 不用旧版本补位。
        const kind=classify(f,latest||{version:'',files:[]},policy,pubs);
        familyInsert.run(clean.id,clean.name,kind,cap(f.latest_version,80)||null,cap(f.recommended_version,80)||null,JSON.stringify(clean.aliases));
        if(fts)db.prepare('INSERT INTO family_search VALUES(?,?,?)').run(clean.id,clean.name,clean.aliases.join(' '));
        for(const v of versions){const files=v.files;delete v.files;versionInsert.run(clean.id,v.version,v.parent_version||null,v.completed_at||null,v.review_id||null,JSON.stringify(v));for(const file of files)fileInsert.run(clean.id,v.version,file.id,file.path,file.sha256||null,file.bytes??null,JSON.stringify(file));}
      }
      const reviewInsert=db.prepare('INSERT INTO reviews(id,state,completed_at,archived_at,revision,updated_at,archive_reason) VALUES(?,?,?,?,?,?,?)');
      for(const [id,r] of Object.entries(reviewEntries).slice(0,20000))if(r&&typeof r==='object')reviewInsert.run(cap(id,128),['active','completed','archived'].includes(r.state)?r.state:'active',iso(r.completed_at),iso(r.archived_at),Number.isSafeInteger(r.revision)?r.revision:0,iso(r.updated_at),cap(r.archive_reason,100)||null);
      for(const p of pubs)db.prepare('INSERT OR REPLACE INTO publications VALUES(?,?)').run(p.id,JSON.stringify(p));
      const projectInsert=db.prepare('INSERT INTO projects VALUES(?,?,?,?)'),projectFileInsert=db.prepare('INSERT OR REPLACE INTO project_files VALUES(?,?,?,?,?)');
      for(const raw of (Array.isArray(sources.projects.value.records)?sources.projects.value.records:[]).slice(0,10000)){
        if(!raw||typeof raw.id!=='string')continue;
        const project=allowed(raw,['id','name','category','path','physical_path','root','aliases','canonical_project_root','source_path','result_directory','observed_files','observed_bytes','observed_depth','scan_complete','location_disks']);
        project.locations=(Array.isArray(raw.locations)?raw.locations:[]).slice(0,100).map(l=>allowed(l,['path','physical_path','category']));
        project.status=cap(raw.status||raw.organization?.status||'',200)||null;
        projectInsert.run(project.id,cap(project.name||project.id,1000),project.path||null,JSON.stringify(project));
        for(const [role,list]of [['result',raw.results],['version',raw.versions]])for(const f of (Array.isArray(list)?list:[]).slice(0,20000)){
          if(!absolute(f.path))continue;
          const file=allowed(f,['id','name','path','size','bytes','sha256','original_path','version','parent_version','step','kind','version_token','mtime']);
          file.id=cap(file.id||'file-'+hash(file.path).slice(0,16),128);projectFileInsert.run(project.id,file.id,role,file.path,JSON.stringify(file));
        }
      }
      const sourceInsert=db.prepare('INSERT OR REPLACE INTO sources VALUES(?,?,?,?,?,?)');
      for(const [key,s]of Object.entries(sources))sourceInsert.run(key,s.path,s.sha256,s.mtime_ms,s.size,at);
      const metaSet=db.prepare('INSERT OR REPLACE INTO meta VALUES(?,?)');metaSet.run('revision',String(Number(readMeta('revision')||0)+1));metaSet.run('updated_at',at);
      metaSet.run('catalog_policy',JSON.stringify({schema:1,families:Object.fromEntries(Object.entries(policy.families).filter(([,p])=>p&&Object.hasOwn(KINDS,p.kind)).map(([id,p])=>[id,{kind:p.kind}]))}));
      metaSet.run('publication_policy',JSON.stringify(allowed(sources.publication_policy.value,['schema','account_id','account_name','account_confirmed','accounts','copy_style','read_only','login_identity_required','snapshot_directory','import_command'])));
      metaSet.run('importer_version',IMPORTER_VERSION);
      db.exec('COMMIT');return true;
    } catch(e){db.exec('ROLLBACK');throw e;}
  }
  function entry(id,from=db,now=Date.now(),includeVersions=true) {
    const row=from.prepare('SELECT * FROM families WHERE id=?').get(id);if(!row)return null;
    const pubs=from.prepare('SELECT data_json FROM publications').all().map(r=>parse(r.data_json));
    const versionQuery=from.prepare('SELECT data_json FROM versions WHERE family_id=?'+(includeVersions?'':' AND version=?')+' ORDER BY completed_at DESC,version DESC');
    const versions=(includeVersions?versionQuery.all(id):versionQuery.all(id,row.latest_version||'')).map(r=>{
      const v=parse(r.data_json,{});v.files=from.prepare('SELECT data_json FROM files WHERE family_id=? AND version=? ORDER BY rowid').all(id,v.version).map(f=>parse(f.data_json));
      const review=from.prepare('SELECT * FROM reviews WHERE id=?').get(v.review_id||'');
      const publication=pubs.filter(p=>matchingPublication(p,{id},v)),creative=['published_work','draft_work'].includes(row.kind);return {...v,...reviewView(review||{},now),publication_status:creative?(publication.length?'published':'unverified'):'not_applicable',publication_evidence:publication,used_in_published_work:!creative&&publication.length>0,...publishedFiles({id},v,publication,creative)};
    });
    const latest=versions.find(v=>v.version===row.latest_version)||null;
    return {id:row.id,name:row.name,kind:row.kind,kind_label:KINDS[row.kind],aliases:parse(row.aliases_json,[]),latest_version:row.latest_version,recommended_version:row.recommended_version,scope:latest?.scope||'active',review_state:latest?.review_state||'active',review_status:latest?.review_status||'active',archive_reason:latest?.archive_reason||null,publication_status:latest?.publication_status||(['published_work','draft_work'].includes(row.kind)?'unverified':'not_applicable'),publication_evidence:latest?.publication_evidence||[],published_files:latest?.published_files||[],published_file_count:latest?.published_file_count||0,published_file_match_status:latest?.published_file_match_status||'unverified',current:latest,latest,versions,url:'/results/'+encodeURIComponent(row.id)};
  }
  function getEntry(id) {sync();return entry(String(id),db,options.now?.()??Date.now());}
  function familyRows(query=''){
    const tokens=query.toLocaleLowerCase().replace(/\\/g,'/').split(/\s+/u).filter(Boolean).slice(0,20),params=[];
    const latestText="replace(lower(f.id || ' ' || f.name || ' ' || f.aliases_json || ' ' || coalesce(json_extract(v.data_json,'$.summary'),'') || ' ' || coalesce(json_extract(v.data_json,'$.folder'),'')),char(92),'/')";
    const fileText="replace(lower(coalesce(json_extract(x.data_json,'$.title'),'') || ' ' || x.path),char(92),'/')";
    const clauses=tokens.map(token=>{params.push(token,token);return '(instr('+latestText+',?)>0 OR EXISTS (SELECT 1 FROM files x WHERE x.family_id=f.id AND x.version=f.latest_version AND instr('+fileText+',?)>0))';});
    return db.prepare('SELECT f.*,v.completed_at AS latest_completed_at,r.state AS state,r.completed_at AS review_completed_at,r.archived_at AS archived_at,r.revision AS review_revision,r.archive_reason AS review_archive_reason FROM families f LEFT JOIN versions v ON v.family_id=f.id AND v.version=f.latest_version LEFT JOIN reviews r ON r.id=v.review_id'+(clauses.length?' WHERE '+clauses.join(' AND '):'')+' ORDER BY f.rowid').all(...params);
  }
  function getProjects(input={}) {
    sync();const q=cap(input.q||'',500).toLocaleLowerCase(),limit=Math.min(500,Math.max(1,Math.trunc(Number(input.limit)||200))),offset=Math.max(0,Math.trunc(Number(input.offset)||0));
    let records=db.prepare('SELECT data_json FROM projects ORDER BY rowid').all().map(r=>parse(r.data_json));
    if(q)records=records.filter(r=>[r.id,r.name,r.path,...r.aliases||[]].some(v=>String(v||'').toLocaleLowerCase().includes(q)));
    return {total:records.length,limit,offset,records:records.slice(offset,offset+limit)};
  }
  function getCatalog(input={}) {
    sync();const scope=['active','archived','all'].includes(input.scope)?input.scope:'active';
    const kind=Object.hasOwn(KINDS,input.kind)?input.kind:'all',q=cap(input.q||input.query||'',500).trim().toLocaleLowerCase();
    const limit=Math.min(500,Math.max(1,Math.trunc(Number(input.limit)||200))),offset=Math.max(0,Math.trunc(Number(input.offset)||0));
    // 生命周期在查询时计算，24 小时到期即归档，不依赖源文件再次写入。
    const now=options.now?.()??Date.now();
    let rows=familyRows(q).map(r=>({...r,...reviewView({state:r.state,completed_at:r.review_completed_at,archived_at:r.archived_at,revision:r.review_revision,archive_reason:r.review_archive_reason},now)}));
    rows=rows.filter(f=>(scope==='all'||f.scope===scope)&&(kind==='all'||f.kind===kind));
    rows.sort((a,b)=>(Date.parse(b.latest_completed_at||'')||0)-(Date.parse(a.latest_completed_at||'')||0)||a.id.localeCompare(b.id));
    const total=rows.length;
    const families=rows.slice(offset,offset+limit).map(f=>entry(f.id,db,now,input.include_versions===true));
    return {schema:SCHEMA,revision:Number(readMeta('revision')||0),updated_at:readMeta('updated_at')||null,scope,kind,total,limit,offset,families};
  }
  function getSummary() {
    sync();const rows=familyRows(),now=options.now?.()??Date.now();
    const counts={all:rows.length,active:0,archived:0,by_kind:{}};for(const k of Object.keys(KINDS))counts.by_kind[k]={all:0,active:0,archived:0};
    for(const f of rows){const {scope}=reviewView({state:f.state,completed_at:f.review_completed_at,archived_at:f.archived_at},now);counts[scope]++;counts.by_kind[f.kind].all++;counts.by_kind[f.kind][scope]++;}
    const projects_count=Number(db.prepare('SELECT count(*) AS n FROM projects').get().n),delivery_files_count=Number(db.prepare('SELECT count(*) AS n FROM files').get().n),project_file_references_count=Number(db.prepare('SELECT count(*) AS n FROM project_files').get().n),files_count=Number(db.prepare('SELECT count(*) AS n FROM (SELECT path FROM files UNION SELECT path FROM project_files)').get().n);
    return {schema:SCHEMA,database:dbPath,revision:Number(readMeta('revision')||0),updated_at:readMeta('updated_at')||null,fts5:fts,counts,projects_count,files_count,delivery_files_count,project_file_references_count,collections:[{id:'material-library',name:'素材资产库',kind:'collection',url:'/#asset-records',record_kind:'asset_record',record_count:counts.by_kind.asset_record.all},{id:'design-research',name:'设计资产库 · 自研工作',kind:'collection',url:'/#research-records',record_kind:'research_record',record_count:counts.by_kind.research_record.all}]};
  }
  function exportSnapshot(destination) {
    sync();fs.mkdirSync(exportRoot,{recursive:true});
    const name=destination||'catalog-'+new Date().toISOString().replace(/[:.]/g,'-');
    const directory=path.resolve(path.isAbsolute(name)?name:path.join(exportRoot,name));
    const relative=path.relative(exportRoot,directory);if(!relative||relative.startsWith('..')||path.isAbsolute(relative))throw Error('导出目录必须位于 database/exports 内的新子目录');
    fs.mkdirSync(directory,{recursive:false});
    const database=path.join(directory,'catalog.sqlite3'),metadata=path.join(directory,'metadata.json'),manifest=path.join(directory,'file-references.json');
    // VACUUM INTO 创建事务一致的独立数据库；导出清单只读该快照。
    db.exec("VACUUM INTO '"+database.replace(/'/g,"''")+"'");
    const copy=new DatabaseSync(database,{readOnly:true});
    try{
      const now=Date.now(),families=copy.prepare('SELECT id FROM families ORDER BY id').all().map(r=>entry(r.id,copy,now));
      const refs=new Map();
      function add(file,role,owner,details={}){if(!absolute(file))return;const key=role+'\0'+file;if(!refs.has(key))refs.set(key,{path:file,role,owners:[],...details});const r=refs.get(key);if(!r.owners.includes(owner))r.owners.push(owner);}
      for(const f of families)for(const v of f.versions){const owner=f.id+'/'+v.version;add(v.folder,'media_directory',owner);add(v.source_manifest,'source_manifest',owner);add(v.completed_time_source,'completion_receipt',owner);for(const file of v.files){add(file.path,'media_file',owner,{id:file.id,sha256:file.sha256||null,bytes:file.bytes??null});if(file.source_path&&file.source_path!==file.path)add(file.source_path,'legacy_reference',owner);}
        try{const u=new URL(v.review_url);const decoded=decodeURIComponent(u.pathname).replace(/^\/([A-Za-z])\//,'$1:/');if(/^[A-Za-z]:\//.test(decoded)){const page=decoded.replace(/\//g,'\\');add(page,'review_page',owner,{review_id:v.review_id||null,review_url:v.review_url});add(path.win32.join(path.win32.dirname(page),'审阅存档'),'review_annotation_directory',owner,{review_id:v.review_id||null});}}catch{}
      }
      const projects=copy.prepare('SELECT data_json FROM projects ORDER BY id').all().map(r=>parse(r.data_json));
      for(const p of projects){p.results=[];p.versions=[];add(p.path,'project_directory',p.id,{id:p.id});add(p.physical_path,'physical_project_directory',p.id,{id:p.id});for(const r of copy.prepare('SELECT role,data_json FROM project_files WHERE project_id=? ORDER BY rowid').all(p.id)){const f=parse(r.data_json);p[r.role==='result'?'results':'versions'].push(f);add(f.path,'project_'+r.role,p.id,{id:f.id,sha256:f.sha256||null,bytes:f.bytes??f.size??null});if(f.original_path&&f.original_path!==f.path)add(f.original_path,'legacy_reference',p.id);}}
      for(const publication of copy.prepare('SELECT data_json FROM publications').all().map(p=>parse(p.data_json))){add(publication.record_directory,'publication_record_directory',publication.id);add(publication.text_path,'published_caption',publication.id);for(const media of publication.media||[]){add(media.path,'observed_publication_media',publication.id,{sha256:media.sha256||null});for(const file of media.matched_files||[])add(file,'selected_original_media',publication.id);}}
      for(const source of copy.prepare('SELECT name,path,sha256,size FROM sources').all())add(source.path,'catalog_source_file',source.name,{sha256:source.sha256,bytes:source.size});
      const referenceList=[...refs.values()];
      const exportedAt=new Date(now).toISOString();
      fs.writeFileSync(metadata,JSON.stringify({schema:SCHEMA,exported_at:exportedAt,revision:Number(readMeta('revision',copy)||0),updated_at:readMeta('updated_at',copy)||null,database:'catalog.sqlite3',families,projects,compatibility:{source_writers:'deliveries.json / catalog.json / review-lifecycle.json / publications.json 继续写入，读取时事务导入 SQLite',media:'媒体及人工标注保留原位；迁移时按 file-references.json 一并复制并核验哈希；清单未逐文件重新读取媒体',restore:'将 catalog.sqlite3 放入目标 database 目录，再将 compatibility-sources 内目录合并到目标 ReviewHub 根目录；媒体及人工标注按路径清单迁移；更换盘符时先统一更新源 JSON 引用再启动。不要仅复制 SQLite 后用空 JSON 重新导入。'},sources:copy.prepare('SELECT name,path,sha256,size FROM sources').all()},null,2)+'\n');
      fs.writeFileSync(manifest,JSON.stringify({schema:SCHEMA,exported_at:exportedAt,database:'catalog.sqlite3',media_embedded:false,references:referenceList},null,2)+'\n');
      const compatibility=path.join(directory,'compatibility-sources');
      const write=(file,value)=>{const target=path.join(compatibility,file);fs.mkdirSync(path.dirname(target),{recursive:true});fs.writeFileSync(target,JSON.stringify(value,null,2)+'\n');};
      const revision=Number(readMeta('revision',copy)||0),updated_at=readMeta('updated_at',copy)||null;
      const deliveries=families.map(f=>({id:f.id,name:f.name,aliases:f.aliases,kind:f.kind,latest_version:f.latest_version,recommended_version:f.recommended_version,versions:copy.prepare('SELECT data_json FROM versions WHERE family_id=? ORDER BY rowid').all(f.id).map(r=>{const v=parse(r.data_json);v.files=copy.prepare('SELECT data_json FROM files WHERE family_id=? AND version=? ORDER BY rowid').all(f.id,v.version).map(r=>parse(r.data_json));return v;})}));
      write('project-catalog/deliveries.json',{schema:1,revision,updated_at,families:deliveries});
      write('project-catalog/catalog.json',{generated_at:updated_at,records:projects});
      write('review-lifecycle.json',{version:1,entries:Object.fromEntries(copy.prepare('SELECT * FROM reviews').all().map(r=>{const {id,...record}=r;return [id,record];}))});
      write('project-catalog/publications.json',{updated_at,posts:copy.prepare('SELECT data_json FROM publications').all().map(r=>parse(r.data_json))});
      write('project-catalog/publication-policy.json',parse(readMeta('publication_policy',copy),{}));
      const exportedPolicy=parse(readMeta('catalog_policy',copy),{schema:1,families:{}});write('database/catalog-policy.json',exportedPolicy);
      fs.writeFileSync(path.join(directory,'catalog-policy.json'),JSON.stringify(exportedPolicy,null,2)+'\n');
      return {directory,database,manifest,metadata,compatibility,files_count:referenceList.filter(r=>r.role==='media_file').length,reference_count:referenceList.length};
    }finally{copy.close();}
  }
  return {getCatalog,getEntry,getProjects,getSummary,exportSnapshot,sync,close(){db.close();},database:dbPath};
}
let singleton;
const getDefault=()=>singleton||(singleton=createCatalogStore());
module.exports={createCatalogStore,KINDS,SCHEMA,getCatalog:input=>getDefault().getCatalog(input),getEntry:id=>getDefault().getEntry(id),getProjects:input=>getDefault().getProjects(input),getSummary:()=>getDefault().getSummary(),exportSnapshot:destination=>getDefault().exportSnapshot(destination)};
if(require.main===module){
  try{const command=process.argv[2]||'summary';let result;
    if(command==='export'){const basename=process.argv[3];if(basename&&!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,100}$/.test(basename))throw Error('导出名称仅使用英文、数字、点、短横线和下划线');result=module.exports.exportSnapshot(basename);}
    else if(command==='summary')result=module.exports.getSummary();else if(command==='list')result=module.exports.getCatalog({scope:process.argv[3]||'active'});else throw Error('用法：node catalog-store.cjs summary | list active/archived/all | export [名称]');
    process.stdout.write(JSON.stringify(result,null,2)+'\n');
  }catch(e){process.stderr.write(e.message+'\n');process.exitCode=1;}
  finally{singleton?.close();}
}

// 作者：Codex（GPT-6）· 2026-10-28
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');
const STATES = Object.freeze({registered:'已登记',queued:'排队中',running:'执行中',waiting_resources:'等待资源',needs_attention:'需要处理',paused:'已暂停',review:'待验收',closed:'已核销',cancelled:'已取消'});
const TERMINAL = new Set(['closed','cancelled']);
const parse = (s, fallback={}) => {try{return JSON.parse(s);}catch{return fallback;}};
const iso = v => {if(v===null||v===undefined||v==='')return null;const n=typeof v==='number'?v*1000:Date.parse(v);return Number.isFinite(n)?new Date(n).toISOString():null;};
const str = (v,max=3000) => typeof v==='string'?v.slice(0,max):'';
const hash = v => crypto.createHash('sha256').update(JSON.stringify(v)).digest('hex');
function fail(message,status=400){const e=new Error(message);e.status=status;throw e;}
function boundedJson(file){try{if(fs.statSync(file).size>512*1024)return null;return parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''),null);}catch{return null;}}
function observedState(native, detail={}) {
  if(['done','review','completed','complete','gpu_work_finished'].includes(native))return 'review';
  if(['cancelled','canceled','archived'].includes(native))return 'cancelled';
  if(['paused','stopped_preserving_checkpoint'].includes(native))return 'paused';
  if(['blocked','failed','error','needs_input'].includes(native))return 'needs_attention';
  if(['waiting_resources','waiting_gpu','waiting_memory','waiting_priority','waiting_for_resources'].includes(native)||native==='running'&&((Array.isArray(detail.wait_reasons)&&detail.wait_reasons.length)||/waiting.*(?:resource|gpu|memory|priority)/i.test(detail.phase||'')))return 'waiting_resources';
  if(native==='running')return 'running';
  if(['ready','todo','scheduled','queued','preparing'].includes(native))return 'queued';
  return 'registered';
}
function createStore(options={}) {
  const root=options.root||path.join(__dirname,'database');
  fs.mkdirSync(root,{recursive:true});
  const real=fs.realpathSync.native(root);
  if(process.platform==='win32'&&!/^[def]:[\\/]/i.test(real))fail('任务数据目录需使用 D、E 或 F 盘。');
  const db=new DatabaseSync(path.join(real,'task-board.sqlite3'));
  db.exec(`PRAGMA journal_mode=WAL; PRAGMA busy_timeout=2000;
    CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, provider TEXT NOT NULL, source_id TEXT, source_board TEXT, title TEXT NOT NULL, assignee TEXT NOT NULL, state TEXT NOT NULL, native_state TEXT, detail TEXT NOT NULL DEFAULT '', deadline_at TEXT, heartbeat_at TEXT, heartbeat_seconds INTEGER NOT NULL DEFAULT 180, progress TEXT NOT NULL DEFAULT '{}', evidence TEXT NOT NULL DEFAULT '[]', meta TEXT NOT NULL DEFAULT '{}', revision INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, progress_at TEXT NOT NULL, closed_at TEXT);
    CREATE UNIQUE INDEX IF NOT EXISTS jobs_source ON jobs(provider,source_board,source_id);
    CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT NOT NULL,kind TEXT NOT NULL,at TEXT NOT NULL,actor TEXT NOT NULL,body TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS events_job ON events(job_id,seq);
    CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,job_id TEXT NOT NULL,type TEXT NOT NULL,note TEXT NOT NULL,status TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,receipt TEXT NOT NULL DEFAULT '{}');
    CREATE INDEX IF NOT EXISTS commands_job ON commands(job_id,created_at);
    CREATE TABLE IF NOT EXISTS executions(adapter TEXT NOT NULL,external_id TEXT NOT NULL,job_id TEXT NOT NULL,sequence INTEGER NOT NULL,heartbeat_at TEXT,state TEXT NOT NULL,capabilities TEXT NOT NULL DEFAULT '[]',PRIMARY KEY(adapter,external_id));
    CREATE TABLE IF NOT EXISTS ingest_receipts(event_id TEXT PRIMARY KEY,job_id TEXT NOT NULL,received_at TEXT NOT NULL);
  `);
  const now=()=>new Date(options.now?options.now():Date.now()).toISOString();
  const job=id=>db.prepare('SELECT * FROM jobs WHERE id=?').get(id);
  const event=(id,kind,actor,body={})=>db.prepare('INSERT INTO events(job_id,kind,at,actor,body) VALUES(?,?,?,?,?)').run(id,kind,now(),actor,JSON.stringify(body));
  let transactionDepth=0;
  const transaction=fn=>{if(transactionDepth)return fn();db.exec('BEGIN IMMEDIATE');transactionDepth++;try{const value=fn();db.exec('COMMIT');return value;}catch(e){db.exec('ROLLBACK');throw e;}finally{transactionDepth--;}};
  function view(row) {
    if(!row)return null;
    const t=Date.parse(now()),active=!TERMINAL.has(row.state)&&row.state!=='review';
    const age=row.heartbeat_at?Math.max(0,Math.floor((t-Date.parse(row.heartbeat_at))/1000)):null;
    const stale=active&&['running','waiting_resources'].includes(row.state)&&(age===null||age>row.heartbeat_seconds);
    const overdue=active&&!!row.deadline_at&&t>Date.parse(row.deadline_at);
    const progressAge=Math.max(0,Math.floor((t-Date.parse(row.progress_at))/1000));
    const commands=db.prepare('SELECT * FROM commands WHERE job_id=? ORDER BY created_at DESC LIMIT 12').all(row.id).map(c=>({...c,receipt:parse(c.receipt)}));
    const executions=db.prepare('SELECT * FROM executions WHERE job_id=? ORDER BY heartbeat_at DESC').all(row.id).map(e=>({...e,capabilities:parse(e.capabilities,[])}));
    const recentEvents=db.prepare('SELECT * FROM events WHERE job_id=? ORDER BY seq DESC LIMIT 8').all(row.id).map(e=>({...e,body:parse(e.body)}));
    return {...row,active_provider:parse(row.meta).active_execution?.adapter||row.provider,active_assignee:parse(row.meta).active_execution?.agent||row.assignee,progress:parse(row.progress),evidence:parse(row.evidence,[]),meta:parse(row.meta),commands,executions,events:recentEvents,
      heartbeat_age_seconds:age,stale,overdue,progress_age_seconds:progressAge,stalled:row.state==='running'&&progressAge>900,
      alerts:[...(stale?['执行状态更新超时']:[]),...(overdue?['已过截止时间']:[]),...(row.state==='needs_attention'?['需要处理']:[]),...(row.state==='running'&&progressAge>900?['15分钟未见进展更新']:[]),...(commands.some(c=>c.status==='delivery_failed')?['指令送达失败']:[])],state_label:STATES[row.state]||row.state};
  }
  function register(input, actor='agent') {
    if(!input||typeof input!=='object')fail('请提交工单资料。');
    if(typeof input.provider!=='string'||!/^[a-z][a-z0-9_.-]{0,63}$/.test(input.provider))fail('适配器名称请使用小写英文、数字、点、横线或下划线。');
    const title=str(input.title,180).trim();if(!title)fail('请填写任务名称。');
    const source=str(input.source_id,200)||null,board=str(input.source_board,120)||'';
    const existing=source?db.prepare('SELECT * FROM jobs WHERE provider=? AND source_board=? AND source_id=?').get(input.provider,board,source):null;
    if(existing)return view(existing);
    const id='job-'+crypto.randomUUID(),time=now();
    const deadline=input.deadline_at?iso(input.deadline_at):null;if(input.deadline_at&&!deadline)fail('截止时间格式需要检查。');
    const state=Object.hasOwn(STATES,input.state)&&!TERMINAL.has(input.state)?input.state:'registered';
    const meta={author:'Codex (GPT-6)',description:str(input.description,8000),source_thread:str(input.source_thread,200),deadline_source:deadline?'user':'none'};
    return transaction(()=>{db.prepare('INSERT INTO jobs(id,provider,source_id,source_board,title,assignee,state,detail,deadline_at,heartbeat_seconds,meta,created_at,updated_at,progress_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)')
      .run(id,input.provider,source,board,title,str(input.assignee,100)||input.provider,state,str(input.detail),deadline,Math.max(30,Math.min(3600,Number(input.heartbeat_seconds)||180)),JSON.stringify(meta),time,time,time);event(id,'registered',actor,{title,state});return view(job(id));});
  }
  function report(id,input,actor='agent') {
    return transaction(()=>{
      const current=job(id);if(!current)fail('找不到这张工单。',404);
      if(TERMINAL.has(current.state))fail('这张工单已经结束，请先由用户重新打开。',409);
      const requested=input.state||current.state;
      const cancelAck=input.ack_command_id&&requested==='cancelled'&&db.prepare("SELECT id FROM commands WHERE id=? AND job_id=? AND type='cancel' AND status IN ('queued','delivered')").get(input.ack_command_id,id);
      if(!Object.hasOwn(STATES,requested)||TERMINAL.has(requested)&&!cancelAck)fail('执行者完成后请提交待验收状态。');
      const state=requested;
      const evidence=input.evidence===undefined?parse(current.evidence,[]):cleanEvidence(input.evidence);
      const progress=input.progress===undefined?parse(current.progress):cleanProgress(input.progress);
      const detail=input.detail===undefined?current.detail:str(input.detail);
      const changed=state!==current.state||JSON.stringify(progress)!==current.progress||detail!==current.detail||JSON.stringify(evidence)!==current.evidence;
      if(state==='review'&&!evidence.length)fail('提交验收时请附上实际成果或完成回执。');
      const time=now(),heartbeat=input.heartbeat_at?iso(input.heartbeat_at):time;
      if(!heartbeat||Date.parse(heartbeat)>Date.parse(time)+300000)fail('执行心跳时间需要检查。');
      db.prepare('UPDATE jobs SET state=?,detail=?,progress=?,evidence=?,heartbeat_at=?,updated_at=?,progress_at=?,revision=revision+? WHERE id=?')
        .run(state,detail,JSON.stringify(progress),JSON.stringify(evidence),heartbeat,time,changed?(iso(input.progress_at)||time):current.progress_at,changed?1:0,id);
      if(changed)event(id,'progress',actor,{state,detail,progress,evidence});
      if(input.ack_command_id){
        const c=db.prepare('SELECT * FROM commands WHERE id=? AND job_id=?').get(input.ack_command_id,id);if(!c)fail('干预指令编号不匹配。');
        if(!['queued','delivered','acknowledged'].includes(c.status))fail('这条指令已经结束。',409);
        const expected={pause:'paused',cancel:'cancelled',resume:'running'}[c.type];
        if(expected&&expected!==state&&!(c.type==='resume'&&['queued','waiting_resources'].includes(state)))fail('请在指令实际生效后确认，并填写相应状态。');
        db.prepare('UPDATE commands SET status=?,updated_at=?,receipt=? WHERE id=?').run('acknowledged',time,JSON.stringify({actor,state,detail}),c.id);event(id,'command_acknowledged',actor,{command_id:c.id,state});
        if(current.provider==='hermes'){
          const meta=parse(current.meta);if(c.type==='pause')meta.control_state='paused';if(c.type==='resume')delete meta.control_state;
          db.prepare('UPDATE jobs SET meta=? WHERE id=?').run(JSON.stringify(meta),id);
        }
      }
      return view(job(id));
    });
  }
  function cleanEvidence(value){if(!Array.isArray(value)||value.length>30)fail('成果清单最多30项。');return value.map(e=>{if(!e||typeof e!=='object'||!str(e.path||e.url||e.summary,4096))fail('每项成果请填写路径、链接或完成说明。');const out={title:str(e.title,160),path:str(e.path,4096),url:str(e.url,2000),summary:str(e.summary,3000)};if(out.url&&!/^https?:\/\//i.test(out.url))fail('成果链接请使用 http 或 https。');return out;});}
  function cleanProgress(value){if(!value||typeof value!=='object'||Array.isArray(value)||JSON.stringify(value).length>16000)fail('进度信息需要是16KB以内的对象。');return value;}
  function command(id,input,actor='user') {
    return transaction(()=>{
      const current=job(id);if(!current)fail('找不到这张工单。',404);
      if(input.revision!==current.revision)fail('工单刚有更新，请刷新后再次操作。',409);
      const type=input.type,note=str(input.note,5000).trim();
      if(!['message','pause','resume','cancel','deadline','accept','reopen'].includes(type))fail('请选择有效的操作。');
      if(!note)fail('请写明操作原因或验收结论。');
      const time=now();
      if(type==='accept'){
        if(current.state!=='review')fail('工单进入待验收后才能核销。',409);
        if(!parse(current.evidence,[]).length)fail('请先补齐完成回执或成果记录。',409);
        db.prepare('UPDATE jobs SET state=?,closed_at=?,updated_at=?,revision=revision+1 WHERE id=?').run('closed',time,time,id);event(id,'accepted',actor,{note});return {job:view(job(id))};
      }
      if(type==='reopen'){
        if(!TERMINAL.has(current.state))fail('只有已结束工单可以重新打开。',409);
        db.prepare('UPDATE jobs SET state=?,closed_at=NULL,updated_at=?,revision=revision+1 WHERE id=?').run('registered',time,id);event(id,'reopened',actor,{note});return {job:view(job(id))};
      }
      if(TERMINAL.has(current.state))fail('工单已经结束。',409);
      if(type==='deadline'){
        const deadline=input.deadline_at?iso(input.deadline_at):null;if(input.deadline_at&&!deadline)fail('截止时间格式需要检查。');
        const meta={...parse(current.meta),deadline_source:deadline?'user':'none'};
        db.prepare('UPDATE jobs SET deadline_at=?,meta=?,updated_at=?,revision=revision+1 WHERE id=?').run(deadline,JSON.stringify(meta),time,id);
      }
      const pending=db.prepare("SELECT * FROM commands WHERE job_id=? AND type=? AND status IN ('queued','delivered')").get(id,type);
      if(pending&&pending.note===note)return {job:view(job(id)),command:{...pending,receipt:parse(pending.receipt)}};
      const cid='cmd-'+crypto.randomUUID();
      db.prepare('INSERT INTO commands(id,job_id,type,note,status,created_at,updated_at) VALUES(?,?,?,?,?,?,?)').run(cid,id,type,note,'queued',time,time);
      if(type!=='deadline')db.prepare('UPDATE jobs SET updated_at=?,revision=revision+1 WHERE id=?').run(time,id);
      event(id,'command_requested',actor,{command_id:cid,type,note,deadline_at:input.deadline_at||null});return {job:view(job(id)),command:db.prepare('SELECT * FROM commands WHERE id=?').get(cid)};
    });
  }
  function markDelivery(cid,status,receipt){return transaction(()=>{const c=db.prepare('SELECT * FROM commands WHERE id=?').get(cid);if(!c||!['queued','delivery_failed'].includes(c.status))return;db.prepare('UPDATE commands SET status=?,updated_at=?,receipt=? WHERE id=?').run(status,now(),JSON.stringify(receipt),cid);event(c.job_id,'command_'+status,'bridge',{command_id:cid,...receipt});});}
  let sourceHealth={status:'starting',checked_at:null,boards:0,errors:[]};
  function syncHermes() {
    const boardsRoot=options.hermesRoot||'E:/Hermes/kanban/boards';let names=[];const errors=[];
    try{names=fs.readdirSync(boardsRoot,{withFileTypes:true}).filter(x=>x.isDirectory()).slice(0,30).map(x=>x.name);}catch{errors.push('Hermes工单目录暂时无法读取');}
    for(const board of names){let source;
      try{
        source=new DatabaseSync(path.join(boardsRoot,board,'kanban.db'),{readOnly:true});source.exec('PRAGMA busy_timeout=500');
        const rows=source.prepare("SELECT id,title,status,assignee,workspace_path,created_at,completed_at,last_heartbeat_at,block_kind,last_failure_error FROM tasks WHERE status NOT IN ('done','archived','cancelled') OR completed_at>=? ORDER BY created_at DESC LIMIT 100").all(Math.floor(Date.parse(now())/1000)-86400);
        for(const r of rows){
          const sourceId=r.id;let current=db.prepare('SELECT * FROM jobs WHERE provider=? AND source_board=? AND source_id=?').get('hermes',board,sourceId);
          if(!current)current=job(register({provider:'hermes',source_id:sourceId,source_board:board,title:r.title,assignee:r.assignee,state:observedState(r.status)},'hermes-import').id);
          if(TERMINAL.has(current.state))continue;
          const meta=parse(current.meta);let status=null,evidence=[];
          if(meta.active_execution&&meta.active_execution.adapter!=='hermes')continue;
          if(r.workspace_path&&/^[def]:[\\/]/i.test(r.workspace_path)){
            try{const workspace=fs.realpathSync.native(r.workspace_path);if(/^[def]:[\\/]/i.test(workspace)){
              status=boundedJson(path.join(workspace,'status.json'));
              for(const name of ['completion.json','GPU_release_receipt.json']){const f=path.join(workspace,name);if(boundedJson(f))evidence.push({title:name,path:f});}
            }}catch{}
          }
          const nativeState=r.status;
          // A worker-owned status file describes its sub-process. Native terminal/review wins;
          // an old queued file cannot move a live native worker back to the queue.
          let state=observedState(nativeState,status||{});
          if(meta.control_state==='paused')state='paused';
          const phase=status?.status||status?.phase;
          if(meta.control_state!=='paused'&&nativeState==='running'&&['waiting_resources','waiting_gpu','waiting_memory','waiting_priority','paused'].includes(phase))state=observedState(phase,status);
          const rawProgress={};
          for(const key of ['phase','observed_step','observed_absolute_step','actual_steps','actual_images_in_new_version','actual_admitted_data','next_step','wait_reasons'])if(status?.[key]!==undefined)rawProgress[key]=status[key];
          const detail=str(status?.reason||status?.stop_reason||(state==='needs_attention'?r.last_failure_error:null)||status?.next_step||'');
          const existingEvidence=parse(current.evidence,[]);if(!evidence.length)evidence=existingEvidence;
          if(state==='review'&&!evidence.length){const ev=source.prepare("SELECT id,kind,created_at FROM task_events WHERE task_id=? AND kind IN ('completed','review_requested') ORDER BY id DESC LIMIT 1").get(sourceId);if(ev)evidence=[{title:'Hermes完成记录',summary:'原任务 '+sourceId+'，事件 '+ev.id+'；请按工单要求验收实际成果。'}];}
          const progress=Object.keys(rawProgress).length?rawProgress:parse(current.progress);
          const changed=state!==current.state||JSON.stringify(progress)!==current.progress||detail!==current.detail||JSON.stringify(evidence)!==current.evidence;
          const time=now(),heartbeat=iso(r.last_heartbeat_at);
          db.prepare('UPDATE jobs SET state=?,native_state=?,heartbeat_at=?,detail=?,progress=?,evidence=?,meta=?,updated_at=?,progress_at=?,revision=revision+? WHERE id=?')
            .run(state,nativeState,heartbeat,detail,JSON.stringify(progress),JSON.stringify(evidence),JSON.stringify({...meta,workspace:r.workspace_path,block_kind:r.block_kind}),changed?time:current.updated_at,changed?time:current.progress_at,changed?1:0,current.id);
          if(changed)event(current.id,'source_update','hermes',{state,native_state:nativeState,detail,progress});
        }
      }catch(e){errors.push(board+': '+str(e.message,160));}finally{try{source?.close();}catch{}}
    }
    sourceHealth={status:errors.length?'degraded':'ready',checked_at:now(),boards:names.length,errors};return sourceHealth;
  }
  function snapshot(){
    const rows=db.prepare('SELECT * FROM jobs ORDER BY created_at DESC LIMIT 500').all().map(view);
    const counts=Object.fromEntries(Object.keys(STATES).map(s=>[s,rows.filter(r=>r.state===s).length]));
    return {schema:1,generated_at:now(),states:STATES,jobs:rows,counts,source_health:sourceHealth,
      attention_count:rows.filter(r=>r.alerts.length).length,transport:{poll_seconds:3,heartbeat_timeout_seconds:180},
      events:db.prepare('SELECT * FROM events ORDER BY seq DESC LIMIT 30').all().map(e=>({...e,body:parse(e.body)}))};
  }
  function detail(id){const r=view(job(id));if(!r)fail('找不到这张工单。',404);return {...r,events:db.prepare('SELECT * FROM events WHERE job_id=? ORDER BY seq DESC LIMIT 80').all(id).map(e=>({...e,body:parse(e.body)}))};}
  function configureSource(provider,board,sourceId,fields){const r=db.prepare('SELECT * FROM jobs WHERE provider=? AND source_board=? AND source_id=?').get(provider,board,sourceId);if(!r)fail('原工单尚未导入。',404);const meta={...parse(r.meta),...fields.meta};db.prepare('UPDATE jobs SET deadline_at=?,meta=?,revision=revision+1 WHERE id=?').run(fields.deadline_at===undefined?r.deadline_at:iso(fields.deadline_at),JSON.stringify(meta),r.id);return view(job(r.id));}
  // Adapter protocol v1: core accepts arbitrary adapter IDs. Session/execution IDs
  // map to a stable work order; multiple clients may attach to the same task_id.
  function ingest(input){
    return transaction(()=>{
    if(input?.schema!=='workbench.task.v1')fail('适配格式需要使用 workbench.task.v1。');
    const fields=new Set(['schema','adapter','external_id','event_id','sequence','state','task_id','title','agent','session_id','description','heartbeat_at','deadline_at','detail','progress','evidence','ack_command_id','capabilities']);
    if(Object.keys(input).some(k=>!fields.has(k)))fail('提交中有适配协议之外的字段。');
    const adapter=input.adapter,external=str(input.external_id,200),eid=str(input.event_id,200);
    if(!/^[a-z][a-z0-9_.-]{0,63}$/.test(adapter||'')||!external||!eid||!Number.isSafeInteger(input.sequence)||input.sequence<0)fail('请填写适配器、执行编号、事件编号和递增序号。');
    const previous=db.prepare('SELECT * FROM ingest_receipts WHERE event_id=?').get(eid);if(previous)return {ok:true,duplicate:true,job:detail(previous.job_id)};
    const execution=db.prepare('SELECT * FROM executions WHERE adapter=? AND external_id=?').get(adapter,external);
    if(execution&&parse(execution.capabilities,[]).includes('ack')&&Array.isArray(input.capabilities)&&input.capabilities.length===1&&input.capabilities[0]==='observe')return {ok:true,ignored:'active_reporter_preferred',job:detail(execution.job_id)};
    if(execution&&input.sequence<=execution.sequence)return {ok:true,ignored:'out_of_order',job:detail(execution.job_id)};
    if(execution&&input.task_id&&execution.job_id!==input.task_id)fail('此执行编号已绑定其他工单。',409);
    let current=input.task_id?job(input.task_id):execution?job(execution.job_id):null;
    if(input.task_id&&!current)fail('指定的统一工单不存在。',404);
    if(!current)current=job(register({provider:adapter,source_id:external,source_board:'adapter',title:input.title||adapter+'任务',assignee:input.agent||adapter,description:input.description||'',source_thread:input.session_id||external,deadline_at:input.deadline_at},adapter).id);
    if(TERMINAL.has(current.state)){
      // Completion stays closed across monitor polls. Explicit operator reopen
      // is required before a new execution changes the same work order.
      return {ok:true,ignored:'closed',job:detail(current.id)};
    }
    const metadata=parse(current.meta),owner=metadata.active_execution||(current.provider==='hermes'?{adapter:'hermes',external_id:current.source_id}:null);
    const sameOwner=owner&&owner.adapter===adapter&&owner.external_id===external;
    if(execution&&owner&&!sameOwner)return {ok:true,ignored:'execution_handed_over',job:detail(current.id)};
    if(!execution&&owner&&!sameOwner&&['running','waiting_resources'].includes(current.state))fail('原执行仍在运行，请先保存并暂停原执行，再交接这张工单。',409);
    if(!sameOwner){
      metadata.active_execution={adapter,external_id:external,agent:str(input.agent,100)||adapter};
      db.prepare('UPDATE jobs SET meta=? WHERE id=?').run(JSON.stringify(metadata),current.id);
      if(owner)event(current.id,'handed_over',adapter,{previous:owner,current:metadata.active_execution});
    }
    const passive=Array.isArray(input.capabilities)&&input.capabilities.length===1&&input.capabilities[0]==='observe';
    const result=report(current.id,{state:input.state||current.state,detail:input.detail,progress:input.progress,evidence:input.evidence,heartbeat_at:input.heartbeat_at,progress_at:passive?input.heartbeat_at:undefined,ack_command_id:input.ack_command_id},adapter);
    const capabilities=Array.isArray(input.capabilities)?input.capabilities.filter(x=>['observe','message','pause','resume','cancel','ack'].includes(x)):[];
    db.prepare('INSERT INTO executions(adapter,external_id,job_id,sequence,heartbeat_at,state,capabilities) VALUES(?,?,?,?,?,?,?) ON CONFLICT(adapter,external_id) DO UPDATE SET sequence=excluded.sequence,heartbeat_at=excluded.heartbeat_at,state=excluded.state,capabilities=excluded.capabilities')
      .run(adapter,external,current.id,input.sequence,result.heartbeat_at,result.state,JSON.stringify(capabilities));
    db.prepare('INSERT INTO ingest_receipts(event_id,job_id,received_at) VALUES(?,?,?)').run(eid,current.id,now());
    return {ok:true,job:detail(current.id),pending_commands:result.commands.filter(c=>['queued','delivered'].includes(c.status))};
    });
  }
  return {db,register,report,command,ingest,markDelivery,syncHermes,snapshot,detail,configureSource,close:()=>db.close(),hash};
}
module.exports={createStore,STATES,observedState};

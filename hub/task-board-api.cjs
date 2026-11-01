// 作者：Codex（GPT-6）· 2026-10-28
'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const {spawn}=require('node:child_process');
const {createStore}=require('./task-board-store.cjs');
const {localWrite}=require('./workbench.cjs');
let service;
const send=(res,status,value)=>{res.writeHead(status,{'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff'});res.end(JSON.stringify(value));};
async function body(req){let bytes=0,parts=[];for await(const part of req){bytes+=part.length;if(bytes>65536)throw Object.assign(Error('提交内容超过64KB。'),{status:413});parts.push(part);}const v=JSON.parse(Buffer.concat(parts).toString('utf8')||'{}');if(!v||typeof v!=='object'||Array.isArray(v))throw Error('请提交JSON对象。');return v;}
function deliver(command,job){return new Promise((resolve,reject)=>{
  const temp='E:/AgentData/Work/ReviewHubTemp';fs.mkdirSync(temp,{recursive:true});
  const real=fs.realpathSync.native(temp);if(!/^[def]:[\\/]/i.test(real))return reject(Error('临时目录路径需要检查。'));
  const child=spawn('D:/anaconda3/python.exe',['-I','-B','-Xutf8',path.join(__dirname,'task-board-hermes.py')],{windowsHide:true,shell:false,stdio:['pipe','pipe','pipe'],env:{...process.env,TEMP:real,TMP:real,TMPDIR:real}});
  let out='',done=false;const finish=(e,v)=>{if(done)return;done=true;clearTimeout(timer);e?reject(e):resolve(v);};
  const timer=setTimeout(()=>{child.kill();finish(Error('Hermes指令送达超时，保留原指令供重试。'));},10000);
  child.stdout.setEncoding('utf8');child.stdout.on('data',s=>{out+=s;if(out.length>65536){child.kill();finish(Error('Hermes返回内容过多。'));}});child.stderr.on('data',()=>{});
  child.on('error',e=>finish(e));child.on('close',code=>{try{const v=JSON.parse(out);if(code||!v.ok)throw Error(v.message||'指令送达失败。');finish(null,v);}catch(e){finish(e);}});
  child.stdin.on('error',()=>{});child.stdin.end(JSON.stringify({command,job:{source_id:job.source_id,source_board:job.source_board,id:job.id,deadline_at:job.deadline_at}}));
});}
function getService(){
  if(service)return service;
  const store=createStore({root:process.env.TASK_BOARD_DB_ROOT||undefined}),clients=new Set();let syncing=false,signature='',lastBroadcast=0,desktopAt=0,desktopHealth=[];
  async function desktops(){
    if(Date.now()-desktopAt<10000)return;desktopAt=Date.now();
    const output=await collectDesktops();desktopHealth=output.adapters||[];
    for(const e of output.events||[]){try{store.ingest(e);}catch(error){desktopHealth.push({id:e.adapter,status:'error',message:error.message});}}
  }
  function broadcast(force=false){
    const snapshot={...store.snapshot(),adapters:desktopHealth};const stable={...snapshot,generated_at:undefined,source_health:{...snapshot.source_health,checked_at:undefined},jobs:snapshot.jobs.map(j=>({...j,heartbeat_age_seconds:undefined,progress_age_seconds:undefined}))};
    const sig=crypto.createHash('sha256').update(JSON.stringify(stable)).digest('hex');
    if(!force&&sig===signature&&Date.now()-lastBroadcast<15000)return;
    signature=sig;lastBroadcast=Date.now();const payload='event: snapshot\ndata: '+JSON.stringify(snapshot)+'\n\n';
    for(const res of clients){if(res.destroyed||res.writableLength>1024*1024){res.destroy();clients.delete(res);}else res.write(payload);}
  }
  async function tick(){if(syncing)return;syncing=true;try{
    store.syncHermes();
    await desktops();
    const queued=store.db.prepare("SELECT * FROM commands WHERE status='queued' ORDER BY created_at LIMIT 3").all();
    for(const c of queued){const job=store.detail(c.job_id);if(job.provider!=='hermes')continue;
      try{store.markDelivery(c.id,'delivered',await deliver(c,job));}catch(e){store.markDelivery(c.id,'delivery_failed',{message:String(e.message).slice(0,300)});}
    }
    broadcast();
  }catch(e){for(const res of clients)res.write('event: health\ndata: '+JSON.stringify({ok:false,message:'任务源暂时无法刷新，保留上次状态。'})+'\n\n');}finally{syncing=false;}}
  const timer=setInterval(tick,3000);timer.unref();tick();
  service={store,clients,broadcast,tick,snapshot:()=>({...store.snapshot(),adapters:desktopHealth})};return service;
}
async function handle(req,res,url,port){
  if(['/task-board.js','/task-board.css','/task-board-protocol.json'].includes(url.pathname)){
    if(!['GET','HEAD'].includes(req.method)){send(res,405,{message:'请使用GET请求。'});return true;}
    const data=fs.readFileSync(path.join(__dirname,url.pathname.slice(1)));res.writeHead(200,{'content-type':url.pathname.endsWith('.js')?'text/javascript; charset=utf-8':url.pathname.endsWith('.json')?'application/json; charset=utf-8':'text/css; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff'});res.end(req.method==='HEAD'?undefined:data);return true;
  }
  if(!url.pathname.startsWith('/api/task-board'))return false;
  try{
    const svc=getService(),store=svc.store;
    if(url.pathname==='/api/task-board'&&req.method==='GET'){send(res,200,svc.snapshot());return true;}
    if(url.pathname==='/api/task-board/commands'&&req.method==='GET'){
      const execution=store.db.prepare('SELECT * FROM executions WHERE adapter=? AND external_id=?').get(url.searchParams.get('adapter')||'',url.searchParams.get('external_id')||'');
      if(!execution){send(res,404,{message:'请先登记执行编号。'});return true;}
      const owner=store.detail(execution.job_id).meta.active_execution;
      if(owner&&(owner.adapter!==execution.adapter||owner.external_id!==execution.external_id)){send(res,409,{message:'工单已交接给其他执行方。'});return true;}
      const commands=store.detail(execution.job_id).commands.filter(c=>['queued','delivered'].includes(c.status));
      for(const c of commands)if(c.status==='queued')store.markDelivery(c.id,'delivered',{adapter:execution.adapter,external_id:execution.external_id});
      send(res,200,{job_id:execution.job_id,commands:store.detail(execution.job_id).commands.filter(c=>['queued','delivered'].includes(c.status))});svc.broadcast();return true;
    }
    if(url.pathname==='/api/task-board/events'&&req.method==='GET'){
      if(svc.clients.size>=24){send(res,503,{message:'当前实时连接较多，请稍后重试。'});return true;}
      res.writeHead(200,{'content-type':'text/event-stream; charset=utf-8','cache-control':'no-cache, no-transform','connection':'keep-alive','x-accel-buffering':'no','x-content-type-options':'nosniff'});
      res.write('retry: 3000\nevent: snapshot\ndata: '+JSON.stringify(svc.snapshot())+'\n\n');svc.clients.add(res);res.on('close',()=>svc.clients.delete(res));return true;
    }
    const m=/^\/api\/task-board\/jobs\/([a-zA-Z0-9-]+)(?:\/(commands|heartbeat))?$/.exec(url.pathname);
    if(m&&req.method==='GET'&&!m[2]){send(res,200,store.detail(m[1]));return true;}
    if(req.method!=='POST'){send(res,405,{message:'请使用正确的任务接口。'});return true;}
    localWrite(req,port);const input=await body(req);
    if(url.pathname==='/api/task-board/reveal'){
      const targets={source:__dirname,protocol:'E:/Hermes/shared/policies/统一工单适配协议.md'};
      if(!Object.hasOwn(targets,input.target)){send(res,400,{message:'请选择源码或适配说明。'});return true;}
      const real=fs.realpathSync.native(targets[input.target]);if(!/^[def]:[\\/]/i.test(real))throw Error('文件位置需要检查。');
      await new Promise((resolve,reject)=>{const child=spawn('C:/Windows/explorer.exe',fs.statSync(real).isDirectory()?[real]:['/select,',real],{windowsHide:true,shell:false,stdio:'ignore'});child.once('spawn',resolve);child.once('error',reject);child.unref();});
      send(res,200,{ok:true,path:real});return true;
    }
    if(url.pathname==='/api/task-board/ingest'){send(res,200,store.ingest(input));svc.broadcast(true);return true;}
    if(url.pathname==='/api/task-board/jobs'){send(res,201,store.register(input,'user'));svc.broadcast(true);return true;}
    if(m&&m[2]==='heartbeat'){send(res,200,store.report(m[1],input,'agent'));svc.broadcast(true);return true;}
    if(m&&m[2]==='commands'){
      const result=store.command(m[1],input,'user');send(res,200,result);svc.broadcast(true);return true;
    }
    send(res,404,{message:'没有这个工单接口。'});return true;
  }catch(e){if(!res.headersSent)send(res,e.status||400,{ok:false,message:e.message||'工单操作暂时无法完成。'});return true;}
}
module.exports={handle,getService};

function collectDesktops(){return new Promise((resolve,reject)=>{
  const temp='E:/AgentData/Work/ReviewHubTemp';fs.mkdirSync(temp,{recursive:true});const real=fs.realpathSync.native(temp);
  if(!/^[def]:[\\/]/i.test(real))return reject(Error('临时目录路径需要检查。'));
  const child=spawn('D:/anaconda3/python.exe',['-I','-B','-Xutf8',path.join(__dirname,'task-board-desktops.py')],{windowsHide:true,shell:false,stdio:['ignore','pipe','pipe'],env:{...process.env,TEMP:real,TMP:real,TMPDIR:real}});
  let text='',done=false;const finish=(e,v)=>{if(done)return;done=true;clearTimeout(timer);e?reject(e):resolve(v);};
  const timer=setTimeout(()=>{child.kill();finish(Error('桌面任务读取超时。'));},8000);
  child.stdout.setEncoding('utf8');child.stdout.on('data',s=>{text+=s;if(text.length>1024*1024){child.kill();finish(Error('桌面状态超过读取上限。'));}});child.stderr.on('data',()=>{});child.on('error',finish);child.on('close',code=>{try{if(code)throw Error('桌面任务读取失败。');finish(null,JSON.parse(text));}catch(e){finish(e);}});
});}

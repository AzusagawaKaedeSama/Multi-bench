// 作者：Codex（GPT-6）· 2026-10-25
'use strict';
const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
// 作者：WorkBuddy（智能设计助手）· 2026-10-31。远程放行策略集中在 remote-access.cjs。
const {isTrustedLocal,originMatches}=require('E:/AgentData/ReviewHub/remote-access.cjs');
const ROOT=path.join(__dirname,'project-catalog');
const read=(name,fallback)=>{try{return JSON.parse(fs.readFileSync(path.join(ROOT,name),'utf8'))}catch{return fallback}};
const send=(res,status,data)=>{res.writeHead(status,{'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff'});res.end(JSON.stringify(data))};
function registeredTarget(input){
 if(!input||typeof input.id!=='string'||!/^project-[a-f0-9]{12}$/.test(input.id))throw Error('项目编号无效');
 const item=read('catalog.json',{records:[]}).records.find(x=>x.id===input.id);if(!item)throw Error('项目未登记');
 let target=item.path;
 if(input.file!==undefined){if(typeof input.file!=='string'||!/^file-[a-f0-9]{16}$/.test(input.file))throw Error('文件编号无效');target=(item.results||[]).find(f=>f.id===input.file)?.path;if(!target)throw Error('成果未登记')}
 const real=fs.realpathSync.native(target);if(!/^[CDEFI]:\\/i.test(real))throw Error('该位置不支持从此页面打开');
 return {path:real,isFile:fs.statSync(real).isFile()};
}
function queueSnapshot(){
 const q=read('training-queue.json',{jobs:[]});
 if(q.live_batch?.status_path){
  try{
   const file=fs.realpathSync.native(q.live_batch.status_path);
   if(!file.toLowerCase().startsWith('e:\\agentdata\\work\\')||path.basename(file)!=='status.json')throw Error('训练状态路径无效');
   const s=JSON.parse(fs.readFileSync(file,'utf8'));
   q.live={status:s.status,phase:s.phase||null,step:s.observed_step||0,worker:s.status==='running'?s.worker||null:null,updated_at:s.updated_unix?new Date(s.updated_unix*1000).toISOString():null,wait_reasons:s.wait_reasons||[],not_before:s.not_before_unix,training_deadline:s.training_deadline_unix,deadline:s.hard_deadline_unix,batch:q.live_batch.batch};
  }catch{q.live={status:'unavailable',batch:q.live_batch.batch}}
  return q;
 }
 try{
  const s=JSON.parse(fs.readFileSync('E:/MOSS-Training/20261022-mujica-krea2-raw-v001/v002_训练接续/status.json','utf8'));
  q.live={status:s.status,phase:s.phase,step:s.observed_absolute_step||0,worker:s.worker||null,updated_at:s.updated_at||null,wait_reasons:s.wait_reasons||[],deadline:s.deadline||null};
  for(const j of q.jobs){if(!['umiri-current','nyamu-current'].includes(j.id))continue;
   const role=j.id.split('-')[0];if(s.phase_done?.[role+':train'])j.status='completed';
   else if(s.phase?.startsWith(role+':')){j.status=s.worker?'running':s.status==='stopped_preserving_checkpoint'?'blocked':'waiting_resources';j.reason=s.stop_reason||s.last_yield_reason||'等待内存、显存和已有队列空闲'}
  }
 }catch{q.live={status:'unavailable'}}
 return q;
}
async function handle(req,res,u,port){
 if(u.pathname.startsWith('/projects/')){
  const name=u.pathname.slice(10);if(!['index.html','catalog.json','verified-lineage.json','organization-status.json'].includes(name))return false;
  if(!['GET','HEAD'].includes(req.method)){send(res,405,{message:'请使用GET'});return true}
  try{const body=fs.readFileSync(path.join(ROOT,name));res.writeHead(200,{'content-type':name.endsWith('.html')?'text/html; charset=utf-8':'application/json; charset=utf-8','cache-control':'no-store'});res.end(req.method==='HEAD'?undefined:body)}catch{send(res,404,{message:'文件未登记'})}return true;
 }
 if(u.pathname==='/api/training-queue'){if(req.method!=='GET'){send(res,405,{message:'请使用GET'});return true}send(res,200,queueSnapshot());return true}
 if(u.pathname==='/api/publications'){send(res,200,read('publications.json',{status:'waiting_account_verification',posts:[]}));return true}
 if(u.pathname!=='/api/projects/reveal')return false;
 try{
  if(req.method!=='POST'){send(res,405,{message:'请使用POST'});return true}
  const host=req.headers.host;if(!originMatches(req)||!isTrustedLocal(req)||!/^application\/json(?:;|$)/i.test(req.headers['content-type']||'')){send(res,403,{message:'请从本机工作台点击打开'});return true}
  let size=0,parts=[];for await(const b of req){size+=b.length;if(size>2048){send(res,413,{message:'请求过大'});return true}parts.push(b)}
  const input=JSON.parse(Buffer.concat(parts).toString('utf8'));if(Object.keys(input).some(k=>!['id','file'].includes(k)))throw Error('请求字段无效');
  const target=registeredTarget(input);
  // 仅用户点击时显示资源管理器；参数来自登记清单，不接收命令和任意路径。
  await new Promise((resolve,reject)=>{const c=spawn('C:\\Windows\\explorer.exe',target.isFile?['/select,',target.path]:[target.path],{shell:false,windowsHide:true,detached:true,stdio:'ignore'});c.once('error',reject);c.once('spawn',()=>{c.unref();resolve()})});
  send(res,200,{ok:true});
 }catch(e){send(res,400,{ok:false,message:e.message==='项目未登记'||e.message==='成果未登记'?e.message:'打开失败，请复制路径后查看'})}return true;
}
module.exports={handle,registeredTarget,queueSnapshot};

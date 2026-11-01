// 作者：Codex（GPT-6）· 2026-10-27
'use strict';
const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
const {getReviewPages}=require('E:/AgentData/ReviewHub/review-pages.cjs');
// 作者：WorkBuddy（智能设计助手）· 2026-10-31。远程放行策略集中在 remote-access.cjs。
const {isTrustedLocal,originMatches}=require('E:/AgentData/ReviewHub/remote-access.cjs');
const STORE=()=>require('E:/AgentData/ReviewHub/catalog-store.cjs');
const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const LABELS={published_work:'已发布作品',draft_work:'作品稿',asset_record:'素材记录',research_record:'自研记录',operations_record:'任务记录'};
const MIME={'.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.webp':'image/webp','.gif':'image/gif','.mp4':'video/mp4','.webm':'video/webm','.mov':'video/quicktime','.mp3':'audio/mpeg','.wav':'audio/wav','.pdf':'application/pdf','.md':'text/plain; charset=utf-8','.txt':'text/plain; charset=utf-8'};
function selected(family,version){const f=STORE().getEntry(family);if(!f)return null;const v=f.versions.find(v=>v.version===(version||f.latest_version));return v?{family:f,version:v}:null}
function snapshot(query='',options={}){return STORE().getCatalog({q:query,scope:options.scope||'active',kind:options.kind||'published_work',limit:500}).families.map(f=>({...f,latest:f.versions.find(v=>v.version===f.latest_version),url:'/results/'+f.id}))}
function json(res,status,value){res.writeHead(status,{'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff'});res.end(JSON.stringify(value))}
function physical(target){const p=fs.realpathSync.native(target);if(!/^[DEFI]:\\/i.test(p))throw Error('文件位置需要核对');const s=fs.statSync(p);return{path:p,isFile:s.isFile()}}
async function registeredTarget(input){
 if(!input||typeof input!=='object'||Array.isArray(input))throw Error('请求格式需要检查');
 if(input.project!==undefined){if(Object.keys(input).length!==1||!/^project-[a-f0-9]{12}$/.test(input.project))throw Error('项目编号需要检查');return require('E:/AgentData/ReviewHub/project-api.cjs').registeredTarget({id:input.project})}
 if(input.review!==undefined){if(Object.keys(input).length!==1||!/^review-[a-f0-9]{16}$/.test(input.review))throw Error('审阅编号需要检查');const p=(await getReviewPages({includeArchived:true})).find(p=>p.id===input.review&&p.available);if(!p?.page_path)throw Error('审阅项目未登记');return physical(path.win32.dirname(p.page_path))}
 if(Object.keys(input).some(k=>!['family','version','file'].includes(k))||! /^[a-z0-9][a-z0-9-]{0,63}$/.test(input.family||''))throw Error('记录编号需要检查');
 const v=selected(input.family,input.version)?.version;if(!v)throw Error('记录版本未登记');
 let p=v.folder;if(input.file!==undefined){p=(v.files||[]).find(f=>f.id===input.file)?.path;if(!p)throw Error('文件未登记')}
 return physical(p);
}
const css=':root{color-scheme:dark}*{box-sizing:border-box}body{font:16px/1.65 system-ui,"Microsoft YaHei",sans-serif;background:#151419;color:#eee;margin:0}main{max-width:1150px;margin:auto;padding:30px 24px}h1{font-size:27px;margin:12px 0}a{color:#b8ceff}button,.button{font:inherit;background:#302d39;color:#fff;border:1px solid #55505f;border-radius:8px;padding:8px 13px;cursor:pointer;display:inline-block;text-decoration:none}button:hover,.button:hover{background:#444052}.actions{display:flex;flex-wrap:wrap;gap:10px;margin:14px 0}.path{display:block;white-space:pre-wrap;overflow-wrap:anywhere;background:#222127;padding:12px;border-radius:8px;font:14px Consolas,monospace}.muted{color:#aaa5b4;font-size:14px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:22px}.card{background:#211f27;border:1px solid #39353f;border-radius:12px;padding:16px;min-width:0}img,video{display:block;width:100%;height:auto;border-radius:7px}h2{margin-top:30px}h3{font-size:17px}.badge{background:#363046;padding:4px 10px;border-radius:20px}summary{cursor:pointer;margin:18px 0}#notice{position:sticky;bottom:12px;background:#363046;border-radius:8px;padding:9px;display:none}';
const button=(label,input)=>'<button data-reveal="'+esc(JSON.stringify(input))+'">'+label+'</button>';
const copy=(label,text)=>'<button data-copy="'+esc(text)+'">'+label+'</button>';
const script=`document.addEventListener('click',async e=>{const b=e.target.closest('[data-copy],[data-reveal]');if(!b)return;const n=document.getElementById('notice');try{if(b.dataset.copy!==undefined){await navigator.clipboard.writeText(b.dataset.copy);n.textContent='已复制完整路径'}else{b.disabled=true;const r=await fetch('/api/results/reveal',{method:'POST',headers:{'Content-Type':'application/json'},body:b.dataset.reveal});const d=await r.json();if(!r.ok)throw Error(d.message);n.textContent='已交给资源管理器打开'}}catch(err){n.textContent=err.message||'请复制完整路径后打开'}finally{b.disabled=false;n.style.display='block'}});`;
function documentPage(title,body){return'<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>'+esc(title)+'</title>'+require('E:/AgentData/ReviewHub/branding.cjs').iconLinks+'<style>'+css+'</style><main><a href="/#creative">创作与审阅</a> · <a href="/#results">已发布作品</a> · <a href="/#results-archive">成果归档</a> · <a href="/#asset-records">素材资产库</a> · <a href="/#research-records">自研工作</a>'+body+'<div id="notice" role="status"></div></main><script>'+script+'</script></html>'}
function mediaUrl(family,version,file){return'/media/'+encodeURIComponent(family)+'/'+encodeURIComponent(version)+'/'+encodeURIComponent(file.id)}
function deliveryPage(f,v){
 const base={family:f.id,version:v.version},kind=f.kind||'operations_record',label=LABELS[kind],archived=(v.scope||f.scope)==='archived';
 const card=file=>{const ext=path.extname(file.path).toLowerCase(),url=mediaUrl(f.id,v.version,file),image=/^image\//.test(MIME[ext]||''),video=/^video\//.test(MIME[ext]||'');return'<article class="card">'+(image?'<a target="_blank" rel="noopener" href="'+esc(url)+'"><img loading="lazy" src="'+esc(url)+'" alt="'+esc(file.title||path.basename(file.path))+'"></a>':video?'<video controls preload="metadata" src="'+esc(url)+'"></video>':'')+'<h3>'+esc(file.title||path.basename(file.path))+'</h3><code class="path">'+esc(file.path)+'</code><div class="actions">'+button('在文件夹中定位',{...base,file:file.id})+copy('复制文件路径',file.path)+(MIME[ext]?'<a class="button" target="_blank" rel="noopener" href="'+esc(url)+'">'+(image?'查看原图':'打开文件')+'</a>':'')+'</div></article>'};
 const publicationBody=require('./publication-view.cjs').render(f,v);
 const files=kind==='published_work'?(v.published_files||[]):(v.files||[]),images=files.filter(x=>x.kind==='image'||/^\.(png|jpe?g|webp|gif)$/i.test(path.extname(x.path||''))),five=images.filter(x=>x.people===5),others=images.filter(x=>x.people!==5),docs=files.filter(x=>!images.includes(x));
 const typeLink=kind==='published_work'?'results':kind==='draft_work'?'draft-records':kind==='asset_record'?'asset-records':kind==='research_record'?'research-records':'task-records';
 return documentPage(f.name,'<h1>'+esc(f.name)+'</h1><p><span class="badge">'+esc(label)+'</span> <span class="badge">'+esc(v.version)+'</span> '+(archived?'已归档':v.version===f.latest_version?'当前记录':'历史版本')+' · '+esc(String(v.completed_at||'').replace('T',' '))+'</p><p>'+esc(v.summary||'')+'</p><div class="actions"><a class="button" target="_blank" rel="noopener" href="'+esc(v.review_url)+'">打开审阅台</a>'+button('打开文件夹',base)+copy('复制文件夹路径',v.folder)+(archived?'<a class="button" href="/#'+typeLink+(kind==='draft_work'?'':'-archive')+'">查看归档记录</a>':'')+'</div><code class="path">'+esc(v.folder)+'</code><p class="muted">'+(f.recommended_version?'当前推荐版本：'+esc(f.recommended_version):'')+'</p>'+publicationBody+(five.length?'<h2>五人同框 · '+five.length+' 张</h2><div class="grid">'+five.map(card).join('')+'</div>':'')+(others.length?'<details'+(!five.length?' open':'')+'><summary>图片 · '+others.length+' 张</summary><div class="grid">'+others.map(card).join('')+'</div></details>':'')+(docs.length?'<h2>文件与说明</h2><div class="grid">'+docs.map(card).join('')+'</div>':'')+'<details><summary>版本与来源</summary><p>父版本：'+esc(v.parent_version||'初始记录')+'</p><code class="path">'+esc(v.source_manifest)+'</code></details>');
}
async function openPage(id){
 if(/^project-[a-f0-9]{12}$/.test(id)){const p=JSON.parse(fs.readFileSync('E:/AgentData/ReviewHub/project-catalog/catalog.json','utf8')).records.find(p=>p.id===id);if(!p)return null;const target=await registeredTarget({project:id});return documentPage(p.name,'<h1>'+esc(p.name)+'</h1><div class="actions">'+button('打开资源管理器',{project:id})+copy('复制绝对路径',target.path)+'</div><code class="path">'+esc(target.path)+'</code>')}
 const p=(await getReviewPages({includeArchived:true})).find(p=>p.id===id&&p.available);if(!p)return null;
 const f=STORE().getCatalog({scope:'all',kind:'all',limit:500,include_versions:true}).families.find(f=>f.versions.some(v=>v.review_id===id));
 return documentPage(p.name,'<h1>'+esc(p.name)+'</h1><div class="actions"><a class="button" target="_blank" rel="noopener" href="'+esc(p.url)+'">打开审阅台</a>'+button('打开资源管理器',{review:id})+copy('复制所在文件夹',p.folder_path)+(f?'<a class="button" href="/results/'+f.id+'">查看记录详情</a>':'')+'</div><h2>审阅页所在文件夹</h2><code class="path">'+esc(p.folder_path)+'</code><h2>审阅页面</h2><code class="path">'+esc(p.page_path)+'</code>');
}
async function streamMedia(req,res,match){
 if(!['GET','HEAD'].includes(req.method)){json(res,405,{message:'请使用GET或HEAD'});return}
 const [family,version,fileId]=match.slice(1);let target,v;
 try{v=selected(family,version)?.version;const file=v?.files.find(f=>f.id===fileId);if(!file)throw Error();target=physical(file.path);if(!target.isFile||!MIME[path.extname(target.path).toLowerCase()])throw Error()}catch{json(res,404,{message:'文件未登记或已离线'});return}
 const size=fs.statSync(target.path).size;let start=0,end=size-1,status=200;
 if(req.headers.range){const r=/^bytes=(\d*)-(\d*)$/.exec(req.headers.range);if(!r||!r[1]&&!r[2]){res.writeHead(416,{'content-range':'bytes */'+size});res.end();return}if(!r[1]){start=Math.max(0,size-Number(r[2]))}else{start=Number(r[1]);if(r[2])end=Math.min(Number(r[2]),size-1)}if(!Number.isSafeInteger(start)||!Number.isSafeInteger(end)||start<0||start>end||start>=size){res.writeHead(416,{'content-range':'bytes */'+size});res.end();return}status=206}
 const headers={'content-type':MIME[path.extname(target.path).toLowerCase()],'content-length':size?end-start+1:0,'accept-ranges':'bytes','cache-control':'no-store','x-content-type-options':'nosniff'};
 if(status===206)headers['content-range']=`bytes ${start}-${end}/${size}`;res.writeHead(status,headers);
 if(req.method==='HEAD'||!size){res.end();return}
 const stream=fs.createReadStream(target.path,{start,end});stream.on('error',()=>res.destroy());res.on('close',()=>stream.destroy());stream.pipe(res);
}
let launching=false,lastOpened=0;
async function handle(req,res,u,port){
 if(['/api/catalog','/api/catalog-summary','/api/deliveries','/api/catalog-export'].includes(u.pathname)){
  try{
   if(u.pathname==='/api/catalog-export'){
    const host=req.headers.host;if(req.method!=='POST'){json(res,405,{message:'请从工作台导出按钮创建快照'});return true}
    if(!originMatches(req)||!isTrustedLocal(req)||!/^application\/json(?:;|$)/i.test(req.headers['content-type']||'')){json(res,403,{message:'请从本机工作台导出'});return true}
    let n=0;for await(const b of req){n+=b.length;if(n>2048){json(res,413,{message:'请求过大'});return true}}
    json(res,200,{ok:true,...STORE().exportSnapshot()});return true;
   }
   if(req.method!=='GET'){json(res,405,{message:'请使用GET'});return true}
   if(u.pathname==='/api/catalog-summary'){json(res,200,STORE().getSummary());return true}
   const scope=u.searchParams.get('scope')||'active',kind=u.searchParams.get('kind')||(u.pathname==='/api/deliveries'?'published_work':'all');
   if(!['active','archived','all'].includes(scope)||!['all',...Object.keys(LABELS)].includes(kind)){json(res,400,{message:'请选择已登记的分类和归档范围'});return true}
   const q=(u.searchParams.get('q')||u.searchParams.get('query')||'').slice(0,200);
   if(u.pathname==='/api/deliveries'){json(res,200,{families:snapshot(q,{scope,kind})});return true}
   const limit=Math.min(500,Math.max(1,Number(u.searchParams.get('limit')||200))),offset=Math.max(0,Number(u.searchParams.get('offset')||0));
   if(!Number.isInteger(limit)||!Number.isInteger(offset)){json(res,400,{message:'分页参数需要整数'});return true}
   json(res,200,STORE().getCatalog({scope,kind,q,limit,offset}));return true;
  }catch(error){json(res,503,{message:'目录暂时无法读取，请稍后刷新'});return true}
 }
 const media=/^\/media\/([a-z0-9][a-z0-9-]{0,63})\/(v\d{4,})\/(file-[a-f0-9]{16})$/.exec(u.pathname);
 if(media){await streamMedia(req,res,media);return true}
 const route=/^\/(results\/([a-z0-9][a-z0-9-]{0,63})|open\/((?:review-[a-f0-9]{16}|project-[a-f0-9]{12})))$/.exec(u.pathname);
 if(route){if(!['GET','HEAD'].includes(req.method)){json(res,405,{message:'请使用GET'});return true}let output;if(route[2]){const s=selected(route[2],u.searchParams.get('version'));if(s)output=deliveryPage(s.family,s.version)}else output=await openPage(route[3]);if(!output){json(res,404,{message:'记录入口未登记'});return true}res.writeHead(200,{'content-type':'text/html; charset=utf-8','cache-control':'no-store','x-frame-options':'DENY','x-content-type-options':'nosniff','referrer-policy':'same-origin'});res.end(req.method==='HEAD'?undefined:output);return true}
 if(u.pathname!=='/api/results/reveal')return false;
 if(req.method!=='POST'){json(res,405,{message:'请从页面按钮打开'});return true}
 const host=req.headers.host;if(!isTrustedLocal(req)||!originMatches(req)||!/^application\/json(?:;|$)/i.test(req.headers['content-type']||'')){json(res,403,{message:'请从本机工作台点击打开'});return true}
 if(launching||Date.now()-lastOpened<1000){json(res,429,{message:'资源管理器正在打开，请稍候'});return true}
 try{let size=0,parts=[];for await(const b of req){size+=b.length;if(size>2048){json(res,413,{message:'请求过大'});return true}parts.push(b)}const target=await registeredTarget(JSON.parse(Buffer.concat(parts).toString('utf8')));launching=true;try{await new Promise((resolve,reject)=>{const c=spawn('C:\\Windows\\explorer.exe',target.isFile?['/select,',target.path]:[target.path],{shell:false,windowsHide:true,detached:true,stdio:'ignore'});c.once('error',reject);c.once('spawn',()=>{c.unref();resolve()})});lastOpened=Date.now();json(res,200,{ok:true})}finally{launching=false}}
 catch(e){json(res,400,{message:'打开失败，请复制绝对路径后查看'})}return true;
}
module.exports={handle,snapshot,selected,registeredTarget,deliveryPage,openPage};

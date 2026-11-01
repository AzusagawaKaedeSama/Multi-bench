// 作者：Codex（GPT-6）· 2026-10-28
'use strict';
const fs=require('node:fs');
const {createStore}=require('./task-board-store.cjs');
const store=createStore();
try{
  const op=process.argv[2]||'snapshot';let result;
  if(op==='snapshot')result=store.snapshot();
  else if(op==='detail'||op==='pending'){result=store.detail(process.argv[3]);if(op==='pending')result=result.commands.filter(c=>['queued','delivered','delivery_failed'].includes(c.status));}
  else if(['register','report','ingest'].includes(op)){
    const raw=fs.readFileSync(0,'utf8');if(Buffer.byteLength(raw)>65536)throw Error('提交内容超过64KB。');const v=JSON.parse(raw);
    result=op==='ingest'?store.ingest(v):op==='register'?store.register(v,'agent'):store.report(v.id,v,'agent');
  }else throw Error('可用操作：register、report、snapshot、detail、pending。');
  console.log(JSON.stringify(result));
}catch(e){console.error(JSON.stringify({ok:false,message:e.message}));process.exitCode=1;}finally{store.close();}

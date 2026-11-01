// 作者：Codex（GPT-6）· 2026-10-27
'use strict';
const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function publicUrl(value){try{const u=new URL(value);return ['https:','http:'].includes(u.protocol)?u.href:''}catch{return''}}
function render(family,version){
 const pubs=version.publication_evidence||family.publication_evidence||[];
 if(!pubs.length)return'';
 const parts=pubs.map(p=>{
  const link=publicUrl(p.url),platform=p.platform==='bilibili'?'B站':p.platform==='xiaohongshu'?'小红书':p.platform||'';
  const media=Array.isArray(p.media)?p.media:[];
  const visuals=media.map((m,i)=>{const url=publicUrl(p.platform==='bilibili'&&m.type==='video'&&/^BV[A-Za-z0-9]{10}$/.test(m.bvid||'')?'https://www.bilibili.com/video/'+m.bvid+'/?p='+Math.max(1,Number(m.page)||1):m.url);if(!url)return'';const selected=Array.from(new Set(m.matched_files||[]));return'<article class="card">'+(m.type==='image'&&!m.path&&!selected.length?'<a href="'+esc(url)+'" target="_blank" rel="noopener"><img loading="lazy" src="'+esc(url)+'" alt="发布图片 '+(i+1)+'"></a>':'<a class="button" href="'+esc(url)+'" target="_blank" rel="noopener">'+(m.type==='image'?'查看平台图片':'查看发布视频')+(media.length>1?' · 第'+(i+1)+'项':'')+'</a>')+(m.bvid?'<p class="muted">'+esc(m.bvid)+(m.page?' · 分P '+esc(m.page):'')+(m.cid?' · CID '+esc(m.cid):'')+'</p>':'')+(selected.length?'<p class="muted">本机选用文件'+(m.associations?.some(a=>a.version)?' · '+m.associations.map(a=>esc(a.version||'')).filter(Boolean).join('、'):'')+'</p>'+selected.map(path=>'<code class="path">'+esc(path)+'</code>').join(''):m.path?'<p class="muted">平台发布副本已归档，可在下方预览和定位。</p>':'<p class="muted">本机文件关联待核对</p>')+'</article>'}).join('');
  return'<section><h2>'+esc(platform+' · '+(p.account_name||p.account_id||''))+'</h2><p class="muted">'+esc(p.published_at||'')+'</p>'+(p.publication_role==='collaborator'?'<p>合作投稿 · 本人负责'+esc(p.collaboration?.account_role||'参与制作')+' · 主作者：'+esc(p.primary_author?.name||p.primary_author?.id||'')+'</p>':'')+'<div class="actions"><a class="button" href="'+esc(link)+'" target="_blank" rel="noopener">查看原帖</a></div><details open><summary>发布文案</summary><div style="white-space:pre-wrap;overflow-wrap:anywhere">'+esc(p.body_observed?p.body||'':'文案待补齐')+'</div></details>'+(p.text_path?'<p>文案文件</p><code class="path">'+esc(p.text_path)+'</code>':'')+(visuals?'<h3>实际发布媒体 · '+media.length+' 项</h3><div class="grid">'+visuals+'</div>':'')+'</section>';
 });
 return parts.join('');
}
module.exports={render};

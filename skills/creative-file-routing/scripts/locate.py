"""Author: Codex GPT-6, 2026-10-27.

Read-only local artifact lookup. Search registered indexes, not directory trees.
Keywords are ANDed per artifact together with its own family aliases/metadata.
File extension is authoritative for known media types. No mtime-based ordering.
"""
from __future__ import annotations
import argparse, hashlib, json, os, re, sys, time, unicodedata
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

DELIVERIES=Path('E:/AgentData/ReviewHub/project-catalog/deliveries.json')
CATALOG=Path('E:/AgentData/ReviewHub/project-catalog/catalog.json')
IMAGE={'.png','.jpg','.jpeg','.webp','.gif','.bmp','.tif','.tiff','.avif','.svg','.heic'}
VIDEO={'.mp4','.mov','.mkv','.webm','.avi','.m4v','.mpeg','.mpg','.wmv'}
DOCUMENT={'.md','.txt','.html','.htm','.pdf','.docx','.doc','.json','.csv','.tsv','.xlsx','.pptx','.yaml','.yml'}
PROJECT={'.aep','.aepx','.prproj','.blend','.psd','.ai','.indd','.c4d','.drp','.fcpbundle'}
MODEL={'.safetensors','.ckpt','.pt','.pth','.onnx','.gguf'}
TEXT_KEYS=('id','name','title','description','summary','aliases','keywords','tags','character','characters','subject','event','game','variant','display_kind','source_type','artwork_kind','category','material_role','role','roles','kind','path','source_path','original_path','filename','people','people_count')
INCOMPLETE={'pending','queued','running','preparing','failed','cancelled','canceled','in_progress'}
SENSITIVE_PARTS={'credentials','secrets','.ssh','browser-profiles','cookies','.sandbox-secrets'}
SENSITIVE_FILE=re.compile(r'^(?:auth|credentials?|cookies?|tokens?|passwords?|secrets?)(?:[._-]|$)|^\.env(?:[._-]|$)',re.I)

def normalize(value):
    return unicodedata.normalize('NFKC',str(value)).casefold()
def terms(query):
    return [normalize(x).strip('"\'') for x in re.split(r'[\s,，;；]+',query.strip()) if x.strip('"\'')]
def text(value):
    if isinstance(value,dict):return ' '.join(text(v) for k,v in value.items() if k in TEXT_KEYS)
    if isinstance(value,(list,tuple,set)):return ' '.join(text(v) for v in value)
    return '' if value is None else str(value)
def searchable(value):return normalize(text(value))
def version_number(value):
    if isinstance(value,int):return value
    m=re.search(r'(?:^|[_\s-])v?(\d+)(?:$|[_\s.-])',str(value or '').strip('_'),re.I)
    return int(m.group(1)) if m else -1
def normalized_version(value):
    n=version_number(value)
    return f'v{n:04d}' if n>=0 else (str(value) if value not in (None,'') else None)
def iso_time(value):
    if not isinstance(value,str):return None
    try:
        d=datetime.fromisoformat(value.replace('Z','+00:00'))
        return d.replace(tzinfo=timezone.utc) if d.tzinfo is None else d
    except ValueError:return None
def timestamp(value):
    d=iso_time(value);return d.timestamp() if d else -1
def public_manifest(path):
    p=Path(path)
    return p.suffix.casefold()=='.json' and not SENSITIVE_FILE.search(p.name) and not any(normalize(x) in SENSITIVE_PARTS for x in p.parts)
def local_path(value,base=None):
    if not isinstance(value,str) or not value or re.match(r'^[a-z]+://',value,re.I):return None
    p=Path(value)
    if not p.is_absolute():
        if base is None:return None
        p=Path(base)/p
    # UNC locations are excluded so a lookup cannot start a network traversal.
    if str(p).startswith(('\\\\','//')):return None
    return p.absolute()
def actual_kind(path,declared=None):
    suffix=Path(path).suffix.casefold()
    if suffix in IMAGE:return 'image'
    if suffix in VIDEO:return 'video'
    if suffix in PROJECT:return 'project'
    if suffix in MODEL:return 'model'
    if suffix in DOCUMENT:return 'document'
    return {'picture':'image','photo':'image','clip':'video','text':'document'}.get(str(declared),declared or 'file')
def is_material(context):
    s=searchable(context)
    return any(t in s for t in ('素材','参考','material','reference','card_art','official_game_art','official_website','official_database'))
def kind_matches(requested,media,context):
    if requested in ('any','all'):return True
    if requested in ('material','source'):return media in ('image','video','document','material','project') and is_material(context)
    if requested=='reference':return media in ('image','video','document','material','project') and any(t in searchable(context) for t in ('参考','精选','reference'))
    if requested=='workflow':return 'workflow' in searchable(context) or '工作流' in searchable(context)
    return requested==media
def requested_people(words):
    cn={'一':1,'二':2,'两':2,'三':3,'四':4,'五':5,'六':6,'七':7,'八':8,'九':9,'十':10}
    for word in words:
        match=re.fullmatch(r'(\d+|[一二两三四五六七八九十])个?人(?:同框|组图)?',word)
        if match:return int(match.group(1)) if match.group(1).isdigit() else cn[match.group(1)]
    return None
def people_count(file,metadata):
    value=file.get('people',file.get('people_count',metadata.get('people',metadata.get('people_count'))))
    if isinstance(value,int) and value>0:return value
    for k in ('character','characters','roles'):
        value=metadata.get(k,file.get(k))
        if isinstance(value,list) and value:return len(value)
    return None
def completed_at(value):
    if value.get('status') in INCOMPLETE or value.get('completed') is False:return None
    for key in ('completed_at','finished_at','completion_time'):
        if iso_time(value.get(key)):return value[key]
    return None
def manifest_rows(data):
    """Only declared artifact arrays; do not recurse through arbitrary configuration."""
    if isinstance(data,list):return [x for x in data if isinstance(x,dict)]
    if not isinstance(data,dict):return []
    result=[]
    for key in ('items','files','artifacts','assets','results','outputs','jobs'):
        value=data.get(key)
        if isinstance(value,list):result.extend(x for x in value if isinstance(x,dict))
        elif isinstance(value,dict):result.extend(x for x in value.values() if isinstance(x,dict))
    return result[:20000]
def row_path(row,base):
    for key in ('path','file_path','absolute_path','output_path','file'):
        value=row.get(key)
        if isinstance(value,dict):value=value.get('path')
        p=local_path(value,base)
        if p:return p
    return None

class Finder:
    def __init__(self,query,kind,limit,deliveries,catalog,budget_seconds,people=None):
        self.query=query;all_words=terms(query);self.people=people if people is not None else requested_people(all_words)
        self.words=[w for w in all_words if requested_people([w]) is None];self.kind=kind;self.limit=max(1,min(100,int(limit)));self.deadline=time.monotonic()+budget_seconds
        self.deliveries_path=Path(deliveries);self.catalog_path=Path(catalog);self.manifest_cache={};self.manifest_bytes=0;self.warnings=[];self.missing=0;self.checked=0;self.index_rows=0
        self.unknown_people=0;self.canonical_receipts={}
    def check(self):
        if time.monotonic()>self.deadline:raise TimeoutError('Lookup time budget exceeded')
    def read(self,path,cap=64*1024*1024):
        self.check();p=Path(path)
        if not p.is_file():return None
        if p.stat().st_size>cap:self.warnings.append({'path':str(p),'reason':'JSON exceeds lookup size limit'});return None
        try:return json.loads(p.read_text(encoding='utf-8-sig'))
        except (OSError,ValueError) as e:self.warnings.append({'path':str(p),'reason':type(e).__name__});return None
    def manifest(self,path,base):
        p=local_path(path,base)
        if p is None or not public_manifest(p):return None,None
        key=os.path.normcase(str(p))
        if key in self.manifest_cache:return p,self.manifest_cache[key]
        if len(self.manifest_cache)>=12:return p,None
        self.check()
        if not p.is_file():self.manifest_cache[key]=None;return p,None
        size=p.stat().st_size
        if size>16*1024*1024 or self.manifest_bytes+size>64*1024*1024:return p,None
        self.manifest_bytes+=size;value=self.read(p,16*1024*1024);self.manifest_cache[key]=value;return p,value
    def candidate(self,context,files):
        if not self.words:return True
        family_text=searchable(context)
        if any(w in family_text for w in self.words):return True
        return any(any(w in searchable(row) for w in self.words) for row in files)
    def enrich(self,files,manifest,base):
        rows=manifest_rows(manifest);by_sha={};by_path={}
        for row in rows:
            self.check();p=row_path(row,base)
            if p:by_path.setdefault(os.path.normcase(str(p.resolve())),[]).append(row)
            if row.get('sha256'):by_sha.setdefault(row['sha256'],[]).append(row)
        enriched=[];seen=set();seen_sha=set()
        for file in files:
            entry=dict(file);p=row_path(entry,base);matches=[]
            if p:matches=by_path.get(os.path.normcase(str(p.resolve())),[])
            if not matches and entry.get('sha256'):matches=by_sha.get(entry['sha256'],[])
            if matches:
                metadata=matches[0];entry['_manifest_metadata']={k:v for k,v in metadata.items() if k in TEXT_KEYS}
                if p:seen.add(os.path.normcase(str(p.resolve())))
                if metadata.get('sha256'):seen_sha.add(metadata['sha256'])
            enriched.append(entry)
        # Explicit files omitted by a registration can be found in that matched
        # family's source manifest. Do not enumerate its directory.
        for row in rows:
            p=row_path(row,base)
            if p and os.path.normcase(str(p.resolve())) not in seen and row.get('sha256') not in seen_sha:
                enriched.append({**row,'path':str(p),'_manifest_only':True});seen.add(os.path.normcase(str(p.resolve())))
        return enriched
    def archive_original(self,version):
        """An explicit archive receipt may identify the original among collections.

        Only that small, explicitly named media file is hashed; no directory scan
        or blanket hashing is performed. Preserve its original completion time.
        """
        path=version.get('archive_receipt')
        if not path:return None
        key=str(path)
        if key in self.canonical_receipts:return self.canonical_receipts[key]
        self.canonical_receipts[key]=None
        rp,receipt=self.manifest(path,version.get('folder'))
        if not isinstance(receipt,dict):return None
        p=local_path(receipt.get('video') or receipt.get('image') or receipt.get('file'),rp.parent)
        sha=receipt.get('sha256')
        if not p or not isinstance(sha,str) or not re.fullmatch('[a-fA-F0-9]{64}',sha):return None
        try:
            st=p.stat()
            if not p.is_file() or st.st_size>64*1024*1024:return None
            if isinstance(receipt.get('bytes'),int) and receipt['bytes']!=st.st_size:return None
            digest=hashlib.sha256()
            with p.open('rb') as source:
                while True:
                    self.check();chunk=source.read(1024*1024)
                    if not chunk:break
                    digest.update(chunk)
            if digest.hexdigest()!=sha.lower():return None
        except OSError:return None
        result={'path':str(p),'realpath':str(p.resolve()),'sha256':sha.lower(),'receipt':str(rp)}
        self.canonical_receipts[key]=result;return result
    def file_result(self,file,family,version,index_source,manifest_path=None,manifest_data=None):
        self.check();family_context={k:v for k,v in family.items() if k in TEXT_KEYS};version_context={k:v for k,v in version.items() if k in TEXT_KEYS and k not in ('path','files')}
        local_context={k:v for k,v in file.items() if k in TEXT_KEYS}
        metadata=file.get('_manifest_metadata',{})
        joined=' '.join((searchable(family_context),searchable(version_context),searchable(local_context),searchable(metadata)))
        if not all(w in joined for w in self.words):return None
        base=Path(manifest_path).parent if manifest_path else Path(version.get('folder') or family.get('path') or index_source.parent)
        registered=row_path(file,base)
        if registered is None:return None
        media=actual_kind(registered,file.get('kind'))
        context={**family_context,**version_context,**local_context,'tags':[family_context,version_context,local_context,metadata]}
        if not kind_matches(self.kind,media,context):return None
        people=people_count(file,metadata);expected_people=self.people
        if expected_people and media=='image':
            if people is None:self.unknown_people+=1;return None
            if people!=expected_people:return None
        choices=[registered]
        for key in ('source_path','original_path','file_path'):
            p=local_path(file.get(key),base)
            if p and p not in choices:choices.append(p)
        actual=None
        for p in choices:
            self.check()
            try:
                if p.is_file():actual=p;break
            except OSError:continue
        self.checked+=1
        if actual is None:self.missing+=1;return None
        try:st=actual.stat();real=actual.resolve(strict=True)
        except OSError:self.missing+=1;return None
        declared=file.get('bytes',file.get('size'))
        if isinstance(declared,int) and declared!=st.st_size:
            self.warnings.append({'path':str(actual),'reason':'Registered size differs from actual file'});return None
        original=actual
        for key in ('original_path','source_path','file_path'):
            p=local_path(file.get(key),base)
            try:
                if p and p.is_file() and os.path.samefile(p,actual):original=p;break
            except OSError:continue
        ver=normalized_version(file.get('version',version.get('version')))
        if ver is None:ver=normalized_version(file.get('version_token'))
        completion=None if version.get('status') in INCOMPLETE or version.get('completed') is False else (completed_at(version) or completed_at(manifest_data or {}) or completed_at(file))
        completion_source=version.get('completed_time_source') or version.get('completion_receipt')
        if not completion_source and completion:completion_source=str(manifest_path or index_source)
        family_id=str(family.get('id') or family.get('name') or '')
        descriptive={k:v for k,v in local_context.items() if k not in ('path','source_path','original_path')}
        local_text=' '.join((searchable(descriptive),searchable(metadata)))
        direct_hits=sum(w in local_text for w in self.words)
        standalone=1 if media=='image' and any(x in local_text for x in ('独立卡图','standalone','独立原图','原始图片')) else 0
        canonical=version.get('_archive_original')
        canonical_match=bool(canonical and file.get('sha256')==canonical['sha256'] and os.path.normcase(str(real))==os.path.normcase(canonical['realpath']))
        character_pair=1 if re.search(r'[＆&×]|与',str(file.get('title') or metadata.get('title') or '')) else 0
        value={'path':str(actual.absolute()),'absolute_path':str(actual.absolute()),'original_path':str(original.absolute()),'realpath':str(real),'registered_path':str(registered),'title':file.get('title') or metadata.get('title') or file.get('name') or actual.name,'kind':media,'people':people,'material':is_material(context) or canonical_match,'family_id':family_id,'family':family.get('name') or family_id,'family_aliases':family.get('aliases',[]),'version':ver,'completed_at':completion,'completed_time_source':completion_source,'completion_verified_in_index':bool(completion),'completion_state':'completed' if completion else 'not_confirmed','source_manifest':str(manifest_path) if manifest_path else version.get('source_manifest') or family.get('source_manifest'),'source_thread':file.get('source_thread') or version.get('source_thread') or family.get('source_thread'),'review_url':file.get('review_url') or version.get('review_url') or family.get('review_url'),'review_id':version.get('review_id') or family.get('review_id'),'workbench_url':'http://127.0.0.1:3900/#creative','results_url':version.get('results_url') or family.get('results_url') or ('http://127.0.0.1:3900/results/'+quote(family_id,safe='') if index_source==self.deliveries_path else None),'latest_registered_version':normalized_version(family.get('latest_version')),'recommended_version':normalized_version(family.get('recommended_version')),'latest':False,'latest_matching_available':False,'recommended':bool(ver and ver==normalized_version(family.get('recommended_version'))),'source_index':str(index_source),'manifest_metadata':metadata,'sha256_registered':file.get('sha256'),'verification':{'exists':True,'bytes':st.st_size,'file_id':st.st_ino,'device':st.st_dev},'canonical_candidate':canonical_match,'archive_receipt':canonical['receipt'] if canonical_match else None,'_rank':(direct_hits,standalone,-character_pair,2 if index_source==self.deliveries_path else 1),'_identity':(st.st_dev,st.st_ino),'_file_id':file.get('id')}
        if canonical_match:value['verification']['sha256_verified_against_archive_receipt']=True
        elif media=='image' and standalone and any(x in searchable(metadata) for x in ('official_website','official_database','official_game_art')):
            value['canonical_candidate']=True
            value['canonical_reason']='Registered official standalone artwork with source-manifest provenance'
        return value
    def delivery_results(self,document):
        result=[]
        for family in document.get('families',[]):
            self.check()
            for version in family.get('versions',[]):
                files=[x for x in version.get('files',[]) if isinstance(x,dict)];self.index_rows+=len(files)
                if not files or not self.candidate(family,files):continue
                # Exclude a known video-only family before touching its manifest
                # when the caller explicitly requested still images.
                if self.kind in ('image','video','document','model') and not any(actual_kind(x.get('path',''),x.get('kind'))==self.kind for x in files):continue
                mp,md=self.manifest(version.get('source_manifest') or version.get('completion_receipt'),version.get('folder'))
                if md is not None:files=self.enrich(files,md,mp.parent)
                version={**version,'_archive_original':self.archive_original(version)}
                for file in files:
                    value=self.file_result(file,family,version,self.deliveries_path,mp,md)
                    if value:result.append(value)
        return result
    def catalog_results(self,document):
        result=[]
        for record in document.get('records',[]):
            self.check()
            if self.kind=='project':
                context=searchable(record)
                if not all(w in context for w in self.words):continue
                p=local_path(record.get('canonical_project_root') or record.get('path'))
                if p and p.is_dir():
                    st=p.stat();result.append({'path':str(p),'absolute_path':str(p),'original_path':str(p),'realpath':str(p.resolve()),'title':record.get('name'),'kind':'project','family_id':record.get('id'),'family':record.get('name'),'version':None,'completed_at':completed_at(record),'source_manifest':record.get('source_manifest'),'source_thread':record.get('source_thread'),'review_url':record.get('review_url'),'latest':False,'latest_matching_available':False,'recommended':False,'latest_registered_version':record.get('latest_version'),'recommended_version':record.get('recommended_version'),'source_index':str(self.catalog_path),'explorer_url':'http://127.0.0.1:3900/open/'+quote(str(record.get('id')),safe=''),'verification':{'exists':True,'file_id':st.st_ino,'device':st.st_dev},'_rank':(0,0,0,1),'_identity':(st.st_dev,st.st_ino)})
            files=[x for x in record.get('results',[])+record.get('versions',[]) if isinstance(x,dict) and x.get('path')]
            self.index_rows+=len(files)
            if not files or not self.candidate(record,files):continue
            version={'folder':record.get('path'),'source_manifest':record.get('source_manifest'),'source_thread':record.get('source_thread'),'review_url':record.get('review_url'),'completed_at':record.get('completed_at')}
            mp,md=self.manifest(record.get('source_manifest'),record.get('path'))
            if md is not None:files=self.enrich(files,md,mp.parent)
            for file in files:
                value=self.file_result(file,record,version,self.catalog_path,mp,md)
                if value:result.append(value)
        return result
    def finish(self,rows):
        # A later collection registration is a reference to an archived original,
        # not a new creative version of that original. Only explicit, hash-checked
        # archive receipts authorize collapsing cross-family registrations.
        groups={}
        for row in rows:
            if row.get('sha256_registered'):groups.setdefault((row['kind'],row['sha256_registered']),[]).append(row)
        removed=set()
        for group in groups.values():
            paths=sorted({r['path'] for r in group})
            for row in group:
                if len(paths)>1:row['same_sha256_paths']=paths
            originals=[r for r in group if r.get('verification',{}).get('sha256_verified_against_archive_receipt')]
            if not originals:continue
            original=min(originals,key=lambda r:timestamp(r.get('completed_at')) if r.get('completed_at') else float('inf'))
            related=[]
            for row in group:
                if row is original:continue
                if row['family_id']==original['family_id'] and row.get('version')!=original.get('version'):continue
                related.append({k:row.get(k) for k in ('path','family_id','family','version','completed_at','source_index','source_manifest','source_thread','review_url')})
                removed.add(id(row))
            original['related_index_refs']=related
        rows=[r for r in rows if id(r) not in removed]
        # Same registered file may appear in results and versions, or through
        # Junction/hard-link aliases. Keep version boundaries and prefer the
        # richer delivery receipt over an unversioned catalog duplicate.
        known={r['_identity'] for r in rows if r.get('version') and r['source_index']==str(self.deliveries_path)}
        unique={}
        for row in rows:
            if row['_identity'] in known and not row.get('version') and row['source_index']!=str(self.deliveries_path):continue
            key=(row['_identity'],row.get('version'))
            if key not in unique or row['_rank']>unique[key]['_rank']:unique[key]=row
        rows=list(unique.values());latest={}
        for row in rows:
            if not row.get('completed_at') or version_number(row.get('version'))<0:continue
            key=(row['family_id'],row['kind']);v=(version_number(row['version']),timestamp(row['completed_at']))
            if key not in latest or v>latest[key]:latest[key]=v
        for row in rows:
            current=(version_number(row.get('version')),timestamp(row.get('completed_at')))
            row['latest_matching_available']=bool(row.get('completed_at') and latest.get((row['family_id'],row['kind']))==current)
            row['latest']=row['latest_matching_available']
            row['latest_registered']=bool(row.get('version') and row.get('latest_registered_version') and row['version']==row['latest_registered_version'])
        rows.sort(key=lambda r:(bool(r.get('completed_at')),r['latest_matching_available'],r['_rank'][1],r['_rank'][0],r['_rank'][2],timestamp(r.get('completed_at')),version_number(r.get('version')),r['_rank'][3]),reverse=True)
        for row in rows:
            row['file_id']=row.pop('_file_id',None)
            for k in ('_rank','_identity'):row.pop(k,None)
        return rows
    def run(self):
        if not self.words and not self.people:raise ValueError('Provide at least one keyword')
        delivery=self.read(self.deliveries_path) or {};catalog=self.read(self.catalog_path) or {}
        rows=self.finish(self.delivery_results(delivery)+self.catalog_results(catalog))
        if self.unknown_people:self.warnings.append({'reason':'Images without registered or manifest character counts excluded from exact people filter','count':self.unknown_people})
        return {'schema_version':1,'author':'Codex GPT-6','status':'ok' if rows else 'no_matches','query':self.query,'keywords_and':self.words,'kind':self.kind,'people_filter':self.people,'count':min(len(rows),self.limit),'total_matches':len(rows),'matches':rows[:self.limit],'statistics':{'indexed_file_rows_considered':self.index_rows,'matched_file_paths_checked':self.checked,'missing_files_filtered':self.missing,'unknown_people_filtered':self.unknown_people,'source_manifests_read':len(self.manifest_cache),'directory_scans':0},'ordering':'Completed versions first; latest matching available version within each family and media kind; direct file keyword matches; declared standalone image role; completion receipt time. File mtime is never used. Explicit verified archive originals retain their original completion time; collections appear as related references.','latest_scope':'latest/latest_matching_available refers to currently present query-matching artifacts of the requested media; latest_registered separately compares the family registered pointer.','warnings':self.warnings}

def locate(query,kind='any',limit=10,deliveries_path=DELIVERIES,catalog_path=CATALOG,budget_seconds=20,people=None):
    if people is not None and (not isinstance(people,int) or not 1<=people<=100):raise ValueError('people must be an integer from 1 to 100')
    return Finder(query,kind,limit,deliveries_path,catalog_path,budget_seconds,people).run()
def main():
    for stream in (sys.stdout,sys.stderr):
        if hasattr(stream,'reconfigure'):stream.reconfigure(encoding='utf-8')
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--query',required=True,help='Space-separated AND keywords; 五人/5人 is an exact registered character-count filter');p.add_argument('--kind',choices=['any','image','video','document','project','material','source','reference','workflow','model'],default='any');p.add_argument('--limit',type=int,default=10);p.add_argument('--people',type=int,help='Exact character count; images without a verified metadata count are excluded');a=p.parse_args()
    try:print(json.dumps(locate(a.query,a.kind,a.limit,people=a.people),ensure_ascii=False,indent=2));return 0
    except Exception as e:print(json.dumps({'status':'error','error':str(e)},ensure_ascii=False));return 2
if __name__=='__main__':sys.exit(main())

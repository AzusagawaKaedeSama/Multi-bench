# 作者：Codex（GPT-6）· 2026-10-22
from pathlib import Path
from review_branding import ensure_icon
import os,sys,json,urllib.request,urllib.parse,datetime,shutil,time,uuid
os.environ.update(TEMP=r'E:\AgentData\Work',TMP=r'E:\AgentData\Work',TMPDIR=r'E:\AgentData\Work')
REG=Path(r'D:\2-视频制作\0-工作流\审阅服务\审阅页清单.txt')
def get(url):
 with urllib.request.urlopen(urllib.parse.quote(url,safe=':/?=&'),timeout=10)as r:return r.status,r.read()
def canonical(line):
 try:
  s=urllib.parse.unquote(line.strip()).replace('\\','/');s=s[1:]if s.startswith('/')else s
  if len(s)>2 and s[1]==':':s=s[0]+s[2:]
  if s[1:2]!='/'or s[:1].upper()not in'DEF':return None
  return str(Path(s[0]+':/'+s[2:]).resolve()).lower()
 except:return None
def main():
 if len(sys.argv)!=2:raise ValueError('用法：register_review.py HTML绝对路径')
 file=Path(sys.argv[1]).resolve(strict=True)
 if file.drive.upper()not in('D:','E:','F:')or not file.is_file()or file.suffix.lower()not in('.html','.htm'):raise ValueError('审阅页必须是D/E/F真实目录中的HTML文件')
 ensure_icon(file)
 line=file.drive[0]+'/'+file.as_posix()[3:];url='http://127.0.0.1:3955/'+line;status,body=get(url)
 if status!=200 or b'window.__'not in body:raise ValueError('3955页面或后台自动保存脚本需要检查')
 lock=Path(r'E:\AgentData\ReviewHub\review-registry.lock');lock.parent.mkdir(parents=True,exist_ok=True);deadline=time.monotonic()+5;fd=None
 while fd is None:
  try:fd=os.open(lock,os.O_WRONLY|os.O_CREAT|os.O_EXCL)
  except FileExistsError:
   if time.monotonic()>=deadline:raise ValueError('其它Agent正在登记审阅页，稍后读回入口再操作')
   time.sleep(.1)
 try:
  os.write(fd,str(os.getpid()).encode());before=REG.read_text('utf-8');changed=False
  if str(file).lower()not in {canonical(x)for x in before.splitlines()}:
   backup=Path(r'E:\AgentData\ReviewHub\registry-history');backup.mkdir(parents=True,exist_ok=True);shutil.copy2(REG,backup/(datetime.datetime.now().strftime('%Y%m%dT%H%M%S%f')+'.txt'))
   temp=REG.with_name(REG.name+'.'+uuid.uuid4().hex+'.pending');temp.write_text(before.rstrip()+'\n'+line+'\n',encoding='utf-8');temp.replace(REG);changed=True
 finally:
  os.close(fd);lock.unlink()
 _,data=get('http://127.0.0.1:3900/api/review-pages?scope=all');pages=json.loads(data);match=[p for p in pages if canonical(urllib.parse.urlsplit(p['url']).path)==str(file).lower()]
 if len(match)!=1 or not match[0]['available']:raise ValueError('登记后3900卡片未正确出现，请检查登记读取与实际页面')
 print(json.dumps({'author':'Codex (GPT-6)','registered':True,'changed':changed,'entry':'http://127.0.0.1:3900/#creative','card':match[0]},ensure_ascii=False))
if __name__=='__main__':
 try:main()
 except Exception as e:print(json.dumps({'author':'Codex (GPT-6)','registered':False,'error':str(e)},ensure_ascii=False));sys.exit(1)

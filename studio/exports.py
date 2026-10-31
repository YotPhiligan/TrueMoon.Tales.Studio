"""Durable, bounded CPU exports; independent from generation jobs and project revision."""
import copy,json,re,subprocess,sys,threading,time
from pathlib import Path
from .model import StudioError,atomic,read,now,uid,digest
from .export_plan import plan,TIMEOUT

ACTIVE={'queued','running','stopping'}


def terminate_owned(row,folder):
    import psutil
    try:
        process=psutil.Process(row['pid']);command=process.cmdline()
        if abs(process.create_time()-row['process_created'])>.02 or not command or Path(command[-1]).resolve()!=folder.resolve():return False
        if not any(Path(c).resolve()==Path(__file__).with_name('export_worker.py').resolve() for c in command if 'export_worker.py' in c):return False
        children=process.children(recursive=True);process.kill()
        for child in children:
            try:child.kill()
            except psutil.NoSuchProcess:pass
        psutil.wait_procs([process,*children],timeout=2);return True
    except (psutil.NoSuchProcess,psutil.AccessDenied,KeyError,ValueError,OSError):return False


class Exports:
    def __init__(self,store):
        self.store=store;self.lock=threading.RLock();self.active=None;self.thread=None;self.stop_event=threading.Event()
    def folder(self,pid,eid,create=False):
        if not isinstance(eid,str) or not re.fullmatch(r'export-[0-9a-f]{16}',eid):raise StudioError('Некорректный ID экспорта.',422)
        legacy=self.store.safe(self.store.location(pid).parent/'Exports'/eid,False)
        root=self.store.paths.exports_root
        configured=self.store.safe(root/pid/eid,False) if root else legacy
        if not create and not configured.exists() and legacy.exists():return legacy
        return configured
    def get(self,pid,eid):
        folder=self.folder(pid,eid);path=folder/'Status.json'
        if not path.is_file():raise StudioError('Экспорт не найден.',404)
        with self.lock:
            row=read(path)
            if row.get('project_id')!=pid or row.get('id')!=eid:raise StudioError('Экспорт относится к другому проекту.',422)
            result=copy.deepcopy(row)
            progress=folder/'Progress.json'
            if progress.is_file():result['progress']=read(progress)
            if row['state']=='completed':result['video_url']=f'/api/projects/{pid}/exports/{eid}/video'
            return result
    def all_rows(self,pid):
        folders=[self.store.location(pid).parent/'Exports']
        if self.store.paths.exports_root:folders.insert(0,self.store.paths.exports_root/pid)
        ids=dict.fromkeys(p.name for folder in folders for p in folder.glob('export-*') if (p/'Status.json').is_file())
        rows=[self.get(pid,eid) for eid in ids]
        return sorted(rows,key=lambda r:r['created_at'],reverse=True)
    def list(self,pid):return self.all_rows(pid)[:50]
    def save(self,folder,row,**patch):
        row={**row,**patch,'updated_at':now(),'revision':row['revision']+1};atomic(self.store.writable(folder/'Status.json'),row);return row
    def create(self,pid,revision,profile='draft',actor='author',client_key=None):
        if not isinstance(actor,str) or not actor.strip() or len(actor)>80:raise StudioError('Укажите исполнителя.',422)
        if client_key is not None and (not isinstance(client_key,str) or not re.fullmatch(r'[A-Za-z0-9_-]{16,80}',client_key)):raise StudioError('Некорректный ключ отправки.',422)
        with self.lock:
            submission={'project_revision':revision,'profile':profile,'actor':actor}
            if client_key:
                for row in self.all_rows(pid):
                    if row.get('client_key')==client_key:
                        if row.get('submission')!=submission:raise StudioError('Ключ уже использован для другого экспорта.',409)
                        return row
            if self.active:raise StudioError('Дождитесь завершения или остановите текущий экспорт.',409)
            request=plan(self.store,pid,revision,profile);eid=uid('export-');folder=self.store.writable(self.folder(pid,eid,create=True));folder.mkdir(parents=True,exist_ok=False)
            atomic(folder/'Request.json',request)
            row={'id':eid,'project_id':pid,'project_revision':revision,'profile':profile,'size':request['size'],'fps':request['fps'],
                 'frames':request['frames'],'seconds':request['seconds'],'state':'queued','revision':1,'created_at':now(),'updated_at':now(),
                 'actor':actor,'client_key':client_key,'submission':submission,'error':''}
            atomic(folder/'Status.json',row);self.stop_event.clear();self.active=(pid,eid)
            try:
                import psutil
                with (folder/'Worker.log').open('wb') as log:
                    process=subprocess.Popen([sys.executable,'-s','-B',str(Path(__file__).with_name('export_worker.py').resolve()),str(folder)],
                        stdout=log,stderr=log,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
                row=self.save(folder,row,state='running',pid=process.pid,process_created=psutil.Process(process.pid).create_time())
                self.thread=threading.Thread(target=self.monitor,args=(folder,row,process),daemon=True);self.thread.start()
            except Exception as error:
                row=self.save(folder,row,state='failed',error=str(error));self.active=None
            return self.get(pid,eid)
    def monitor(self,folder,row,process):
        started=time.monotonic();reason=None
        try:
            while process.poll() is None:
                if self.stop_event.is_set():reason='cancelled';terminate_owned(row,folder);break
                if time.monotonic()-started>TIMEOUT+5:reason='failed';terminate_owned(row,folder);break
                self.stop_event.wait(.2)
            process.wait(timeout=5)
            result=read(folder/'Result.json') if (folder/'Result.json').is_file() else {}
            with self.lock:
                current=read(folder/'Status.json')
                if result.get('state')=='completed' and (folder/'Draft.mp4').is_file():
                    self.save(folder,current,state='completed',result=result,error='')
                else:self.save(folder,current,state=reason or 'failed',error='Экспорт остановлен.' if reason=='cancelled' else result.get('error','Рабочий процесс остановлен или превысил лимит времени.'))
        except Exception as error:
            terminate_owned(row,folder)
            with self.lock:self.save(folder,read(folder/'Status.json'),state='failed',error=str(error))
        finally:
            with self.lock:self.active=None
    def stop(self,pid,eid,revision):
        with self.lock:
            row=self.get(pid,eid)
            if type(revision) is not int or row['revision']!=revision:raise StudioError('Статус экспорта изменился. Перечитайте его.',409)
            if self.active!=(pid,eid) or row['state'] not in ACTIVE:raise StudioError('Экспорт уже завершён.',409)
            self.save(self.folder(pid,eid),read(self.folder(pid,eid)/'Status.json'),state='stopping');self.stop_event.set()
            return self.get(pid,eid)
    def video(self,pid,eid):
        row=self.get(pid,eid)
        if row['state']!='completed':raise StudioError('Экспорт ещё не готов.',409)
        path=self.folder(pid,eid)/'Draft.mp4'
        if not path.is_file():raise StudioError('Файл экспорта недоступен.',404)
        return path
    def recover(self):
        for p in self.store.list_projects():
            for row in self.all_rows(p['id']):
                if row['state'] not in ACTIVE:continue
                folder=self.folder(p['id'],row['id']);terminate_owned(row,folder)
                result=read(folder/'Result.json') if (folder/'Result.json').is_file() else {}
                if result.get('state')=='completed' and (folder/'Draft.mp4').is_file() and digest(folder/'Draft.mp4')==result.get('sha256'):
                    self.save(folder,row,state='completed',result=result,error='')
                else:self.save(folder,row,state='interrupted',error='Studio был перезапущен. Экспорт автоматически не повторяется.')
    def close(self):
        self.stop_event.set()
        if self.thread:self.thread.join(timeout=4)

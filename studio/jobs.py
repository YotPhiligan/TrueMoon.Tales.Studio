"""Explicit prepare -> one bounded launch -> collect, reusing the native AV core."""
from __future__ import annotations
import copy,json,os,re,shutil,subprocess,sys,threading,time
from pathlib import Path
import psutil
from .model import ACTIVE,StudioError,atomic,block_hash,digest,now,read,uid

class Jobs:
    def __init__(self,store,python=None,core=None):
        self.store=store;self.python=Path(python or store.paths.python_executable)
        self.core=Path(core or store.paths.profiles_root);self.lock=threading.RLock();self.threads={}

    def directory(self,pid,jid):
        if not re.fullmatch(r'job-[a-f0-9]{16}',jid):raise StudioError('Некорректный ID задания.')
        return self.store.safe(self.store.location(pid).parent/'Jobs'/jid,False)

    def get(self,pid,jid):
        path=self.directory(pid,jid)/'Studio.job.json'
        if not path.is_file():raise StudioError('Задание не найдено.',404)
        return read(path)

    def write(self,job):
        with self.lock:
            job['updated_at']=now();atomic(self.store.writable(self.directory(job['project_id'],job['id'])/'Studio.job.json'),job)

    def all(self,pid=None):
        projects=[{'id':pid}] if pid else self.store.list_projects();rows=[]
        for p in projects:
            folder=self.store.location(p['id']).parent/'Jobs'
            for f in folder.glob('*/Studio.job.json'):
                try:rows.append(read(f))
                except (OSError,ValueError):continue
        return sorted(rows,key=lambda r:r['created_at'],reverse=True)

    def command(self,args,directory,log,timeout=90):
        self.store.writable(directory,False).mkdir(parents=True,exist_ok=True)
        flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
        env=os.environ.copy();env.update(TMT_COMFY_ROOT=str(self.store.paths.comfy_root),TMT_STUDIO_PYTHON=str(self.python))
        with (directory/(log+'.stdout.log')).open('wb') as out,(directory/(log+'.stderr.log')).open('wb') as err:
            try:result=subprocess.run([str(self.python),'-s','-B',*map(str,args)],stdout=out,stderr=err,timeout=timeout,creationflags=flags,env=env)
            except subprocess.TimeoutExpired:raise StudioError('Подготовка превысила лимит времени; GPU не запускался.',422)
        if result.returncode:
            detail=(directory/(log+'.stderr.log')).read_text(encoding='utf-8',errors='replace')[-5000:]
            raise StudioError('Не удалось подготовить задание.',422,detail)

    def prepare(self,pid,bid,revision,actor='author'):
        with self.store.lock:
            p=self.store.get(pid)
            if p['revision']!=revision:raise StudioError('Проект уже изменился; обновите версию перед подготовкой.',409)
            b=copy.deepcopy(self.store.find_block(p,bid));validation=self.store.validate(p,b)
            if validation['errors']:raise StudioError('Материалы блока требуют исправления.',422,validation)
        if not self.python.is_file() or not (self.core/'prepare.py').is_file():raise StudioError('Не найден portable Python или профиль Omni.v1.1.',422)
        self.powershell()
        jid=uid('job-');folder=self.store.writable(self.directory(pid,jid));source=folder/'Input';source.mkdir(parents=True)
        name='STUDIO.'+re.sub(r'[^A-Za-z0-9._-]','-',b['label'])[:32]+'.'+jid[4:]
        cfg={'version':1,'id':name,'profile':'omni-context' if b['settings']['mode']=='context' else 'omni-guided',
             'size':b['settings']['size'],'seconds':float(b['settings']['seconds']),'seed':int(b['settings']['seed']),
             'prompt':'Prompt.txt','references':[{'role':r['role'],'path':str(self.store.resolve_asset(p,r['asset_id']))} for r in b['references']],
             'guides':[{'picture':next(i+1 for i,r in enumerate(b['references']) if r['id']==g['reference_id']),'frame':g['frame']} for g in b['guides']],
             'clip_index':1}
        if not self.store.paths.legacy_mode:cfg['runtime']={'python_executable':str(self.python),'comfy_root':str(self.store.paths.comfy_root)}
        if cfg['profile']=='omni-context':
            take=self.store.find_take(p,b['context_take_id']);cfg['previous_latent']=str(self.store.resolve_asset(p,take['native_av_asset_id']));cfg['clip_index']=2
        (source/'Prompt.txt').write_text(b['prompt'],encoding='utf-8');atomic(source/'Config.json',cfg)
        atomic(source/'Block.snapshot.json',{'project_revision':revision,'block':b,'actor':actor,'created_at':now()})
        job={'id':jid,'project_id':pid,'block_id':bid,'name':name,'block_label':b['label'],'state':'preparing','phase':'Подготовка графа',
             'created_at':now(),'updated_at':now(),'actor':actor,'source_revision':revision,'block_hash':block_hash(b),
             'timing':validation['timing'],'seed':b['settings']['seed'],'size':b['settings']['size'],'run_dir':str(folder/'Run'),
             'config':str(source/'Config.json'),'source_files':[],'launch_requested':False,'cancel_requested':False,'error':None}
        self.write(job)
        try:
            self.command([self.core/'prepare.py',source/'Config.json',folder/'Preview'],folder,'Prepare')
            build=read(folder/'Preview/Build.json');job['source_files']=[{'path':r['Source'],'sha256':r['SHA256']} for r in build['source_input_hashes']]
            job['input_files']={str(f):digest(f) for f in [source/'Config.json',source/'Prompt.txt',source/'Block.snapshot.json']}
            job['implementation_files']={str(f):digest(f) for f in self.core.iterdir() if f.suffix in ['.py','.ps1']}
            job['implementation_files'].update(build.get('template_sha256',{}))
            job['preview_api_sha256']=build.get('api_sha256')
            job['state']='prepared';job['phase']='Граф подготовлен; генерация не запускалась';self.write(job)
        except StudioError as error:
            job.update(state='failed',phase='Ошибка подготовки',error=str(error),error_detail=error.details);self.write(job);raise
        return job

    @staticmethod
    def powershell():
        # Windows PowerShell 5.1 can return ExitCode=None after a redirected
        # Start-Process timed wait. The native runner requires PowerShell 7.
        candidate=shutil.which('pwsh') or str(Path(os.environ.get('ProgramFiles','C:/Program Files'))/'PowerShell/7/pwsh.exe')
        if not Path(candidate).is_file():raise StudioError('Для ограниченного запуска требуется установленный PowerShell 7 (pwsh.exe).',422)
        return str(Path(candidate).resolve())

    @staticmethod
    def process_identity(process):
        return {'pid':process.pid,'created':process.create_time(),'exe':process.exe(),'cmdline':process.cmdline()}

    @staticmethod
    def matching(identity):
        if not identity:return None
        try:
            p=psutil.Process(identity['pid'])
            if abs(p.create_time()-identity['created'])>.02 or p.exe().casefold()!=identity['exe'].casefold() or p.cmdline()!=identity['cmdline']:return None
            return p
        except (psutil.NoSuchProcess,psutil.AccessDenied):return None

    def launch(self,pid,jid,actor='author'):
        with self.lock:
            job=self.get(pid,jid);folder=self.store.writable(self.directory(pid,jid))
            if job['state']!='prepared' or job['launch_requested'] or (folder/'Launch.intent').exists():raise StudioError('Это задание уже запускалось. Подготовьте новый дубль.',409)
            if not self.store.paths.legacy_read_enabled and self.store.paths.is_legacy_path(job['config']):raise StudioError('Задание подготовлено до переноса. Подготовьте новый дубль; исторический снимок сохранён.',409)
            if any(j['state'] in ACTIVE and j.get('launch_requested') for j in self.all()):raise StudioError('Уже выполняется одно задание Studio. Дождитесь завершения или остановите его.',409)
            p=self.store.get(pid);block=self.store.find_block(p,job['block_id'])
            if block_hash(block)!=job['block_hash']:raise StudioError('Настройки блока изменились после подготовки. Подготовьте новый снимок.',409)
            if not job.get('input_files'):raise StudioError('У этого задания нет проверяемого снимка входов. Подготовьте его заново.',409)
            for path,expected in {**job['input_files'],**job.get('implementation_files',{})}.items():
                try:
                    source=Path(path) if self.store.paths.legacy_mode else self.store.safe(path)
                    valid=source.is_file() and digest(source)==expected
                except (OSError,StudioError):valid=False
                if not valid:raise StudioError('Снимок промпта/настроек или профиль изменился после подготовки. Подготовьте новый дубль.',409)
            for row in job['source_files']:
                if digest(self.store.safe(row['path']))!=row['sha256']:raise StudioError('Исходный файл изменился после подготовки; подготовьте задание заново.',409)
            with (folder/'Launch.intent').open('x',encoding='utf-8') as f:f.write(json.dumps({'at':now(),'actor':actor}))
            job.update(state='queued',phase='Ожидание запуска',launch_requested=True,launched_at=now());self.write(job)
            thread=threading.Thread(target=self.worker,args=(pid,jid),daemon=True);self.threads[jid]=thread;thread.start();return job

    def worker(self,pid,jid):
        folder=self.directory(pid,jid)
        try:
            with self.lock:
                job=self.get(pid,jid)
                if job['cancel_requested']:raise InterruptedError('Остановлено до запуска')
                job.update(state='preparing',phase='Проверка и копирование входов');self.write(job)
            self.command([self.core/'prepare.py',job['config'],folder/'Run','--stage'],folder,'Stage')
            if job.get('preview_api_sha256') and digest(folder/'Run/Prepared.api.json')!=job['preview_api_sha256']:
                raise StudioError('Граф изменился между подготовкой и staging. Генерация не запущена.',409)
            with self.lock:
                current=self.get(pid,jid)
                if current['cancel_requested']:raise InterruptedError('Остановлено до генерации')
                # shell=False, literal arguments; the runner owns all Comfy processes and deadlines.
                shell=self.powershell()
                args=[shell,'-NoProfile','-ExecutionPolicy','Bypass','-File',str(self.core/'run.ps1'),'-JobDir',str(folder/'Run'),'-Mode','api']
                with (folder/'Runner.stdout.log').open('wb') as out,(folder/'Runner.stderr.log').open('wb') as err:
                    proc=subprocess.Popen(args,stdout=out,stderr=err,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
                current.update(state='running',phase='Проверка профиля / генерация',runner=self.process_identity(psutil.Process(proc.pid)))
                self.write(current)
            # The runner and its independent watchdog are bounded. Preserve their own cleanup.
            code=proc.wait(timeout=900)
            current=self.get(pid,jid)
            if current['cancel_requested']:raise InterruptedError('Задание остановлено')
            if code:raise StudioError('Генерация завершилась с ошибкой. Дубль автоматически не повторялся.',422,self.log_tail(folder))
            self.finish(pid,jid)
        except InterruptedError as error:
            job=self.get(pid,jid);job.update(state='cancelled',phase='Остановлено',error=str(error));self.write(job)
        except Exception as error:
            job=self.get(pid,jid)
            if isinstance(error,subprocess.TimeoutExpired):
                try:self.stop(pid,jid)
                except StudioError:pass
            cancelled=job.get('cancel_requested',False)
            job.update(state='cancelled' if cancelled else 'failed',phase='Остановлено' if cancelled else 'Ошибка',error=str(error),error_detail=getattr(error,'details',None) or self.log_tail(folder));self.write(job)

    def owned_server(self,job):
        watchdog=self.directory(job['project_id'],job['id'])/'Run/Watchdog.json'
        if not watchdog.exists():return None
        try:
            data=read(watchdog);process=psutil.Process(data['server_pid'])
            if abs(process.create_time()-data['server_created_epoch'])>.02:return None
            if Path(process.exe()).resolve()!=self.python.resolve():return None
            expected=(self.core/'server.py').resolve()
            if not any(Path(s).resolve()==expected for s in process.cmdline()[1:] if not s.startswith('-')):return None
            return process
        except (OSError,ValueError,KeyError,psutil.NoSuchProcess,psutil.AccessDenied):return None

    def log_tail(self,folder):
        lines=[]
        for name in ['Runner.stderr.log','Run/Server.stderr.log','Run/Driver.stderr.log','Stage.stderr.log']:
            f=folder/name
            if f.exists():
                with f.open('rb') as stream:stream.seek(max(0,f.stat().st_size-6000));text=stream.read().decode('utf-8',errors='replace')
                lines.append(name+'\n'+text[-4000:])
        return '\n'.join(lines)[-10000:]

    def finish(self,pid,jid):
        job=self.get(pid,jid);folder=self.store.writable(self.directory(pid,jid));run=folder/'Run'
        if not (run/'Cleanup.json').exists() or not read(run/'Cleanup.json')['stopped']:raise StudioError('Сервер ещё не подтвердил завершение; результат пока не собран.',409)
        job.update(state='collecting',phase='Проверка результата');self.write(job)
        self.command([self.core/'collect.py',run],folder,'Collect',90)
        result=read(run/'Result.json')
        with self.store.lock:
            p=self.store.get(pid);b=self.store.find_block(p,job['block_id'])
            existing=next((t for t in b['takes'] if t.get('job_id')==jid),None)
            if not existing:
                outputs=result['outputs'];key='207' if '207.0' in outputs else '92'
                take={'id':uid('take-'),'name':job['name'],'job_id':jid,'technical_status':'success','review_state':'unreviewed',
                      'video_asset_id':self.store.asset(p,outputs[key+'.0']['path']),
                      'raw_asset_id':self.store.asset(p,outputs['92.0']['path']),
                      'last_frame_asset_id':self.store.asset(p,outputs['202.0']['path']),
                      'native_av_asset_id':self.store.asset(p,outputs['200.0']['path']),
                      'video_info':result['videos'][key],'notes':'Технически выполнено. Требует просмотра и отдельного отбора.',
                      'settings_hash':job['block_hash'],'created_at':now(),'imported':False,
                      'metrics':{k:result[k] for k in ['server_wall_seconds','device_gpu_peak_GiB','rss_peak_GiB']}}
                b['takes'].append(take);self.store.commit(p,p['revision'],'runner','Добавлен дубль '+job['name'])
                existing=take
        job.update(state='completed',phase='Готово · требуется просмотр',take_id=existing['id'],finished_at=now());self.write(job)

    def stop(self,pid,jid):
        with self.lock:
            job=self.get(pid,jid)
            if job['state'] not in ACTIVE:raise StudioError('Задание уже не выполняется.',409)
            job.update(cancel_requested=True,state='stopping',phase='Остановка своего задания');self.write(job)
            root=self.matching(job.get('runner'))
            if root:
                identities=[]
                for proc in root.children(recursive=True):
                    try:identities.append(self.process_identity(proc))
                    except psutil.NoSuchProcess:pass
                # Children were obtained from the verified owned runner, never from a port lookup.
                for identity in reversed(identities):
                    child=self.matching(identity)
                    if child:
                        try:child.kill()
                        except psutil.NoSuchProcess:pass
                if self.matching(job['runner']):root.kill()
            else:
                server=self.owned_server(job)
                if server:
                    try:server.kill()
                    except psutil.NoSuchProcess:pass
            return self.get(pid,jid)

    def recover(self):
        for job in self.all():
            if job['state'] not in ACTIVE:continue
            if self.matching(job.get('runner')) or self.owned_server(job):
                threading.Thread(target=self.observe,args=(job['project_id'],job['id']),daemon=True).start();continue
            run=self.directory(job['project_id'],job['id'])/'Run'
            if (run/'History.json').exists() and read(run/'History.json')['status']['status_str']=='success' and (run/'Cleanup.json').exists():
                try:self.finish(job['project_id'],job['id'])
                except StudioError as e:job.update(state='failed',phase='Ошибка восстановления',error=str(e));self.write(job)
            else:
                # Never resubmit: incomplete state needs explicit new preparation.
                job.update(state='interrupted',phase='Приложение перезапущено · дубль не повторён',error='Результат не подтверждён. Проверьте сохранённые логи.');self.write(job)

    def observe(self,pid,jid):
        while True:
            current=self.get(pid,jid)
            if not self.matching(current.get('runner')) and not self.owned_server(current):break
            time.sleep(2)
        job=self.get(pid,jid)
        try:
            if job.get('cancel_requested'):job.update(state='cancelled',phase='Остановлено');self.write(job)
            else:self.finish(pid,jid)
        except StudioError as error:job.update(state='failed',phase='Ошибка',error=str(error));self.write(job)

    def progress(self,pid):
        rows=self.all(pid)
        for job in rows:
            if job['state'] not in ACTIVE:continue
            run=self.directory(pid,job['id'])/'Run';runtime_path=run/'Runtime.json'
            try:
                if runtime_path.exists():
                    r=read(runtime_path);count=len(r.get('h3_forwards',[]));job['progress']={'forwards':min(count,8),'total':8,'sage':r.get('sage_success',0)}
                    active=[n for n in r.get('nodes',[]) if 'seconds' not in n]
                    if active:job['active_node']=active[-1]['class']
            except (OSError,ValueError):pass
        return rows

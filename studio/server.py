"""Loopback-only HTTP/API and seekable media serving for True Moon Studio."""
from __future__ import annotations
import json,mimetypes,re,secrets,socket,urllib.parse
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from pathlib import Path
from .model import AUDIO,IMAGES,MEDIA,VIDEOS,StudioError,atomic,now,read,uid
from . import agent
from . import scenario

def byte_range(header,size):
    if not header:return 0,size-1,False
    match=re.fullmatch(r'bytes=(\d*)-(\d*)',header.strip())
    if not match or not size or not any(match.groups()):raise StudioError('Некорректный диапазон.',416)
    first,last=match.groups()
    if not first:
        count=int(last)
        if count<=0:raise StudioError('Пустой диапазон.',416)
        start=max(0,size-count);end=size-1
    else:
        start=int(first);end=min(size-1,int(last)) if last else size-1
    if start>=size or start>end:raise StudioError('Диапазон вне файла.',416)
    return start,end,True

class LocalServer(ThreadingHTTPServer):
    daemon_threads=True
    allow_reuse_address=False
    def server_bind(self):
        if hasattr(socket,'SO_EXCLUSIVEADDRUSE'):self.socket.setsockopt(socket.SOL_SOCKET,socket.SO_EXCLUSIVEADDRUSE,1)
        super().server_bind()

def handler_for(store,jobs,static,exports=None):
    if exports is None:
        from .exports import Exports
        exports=Exports(store)
    token=secrets.token_urlsafe(32);static=Path(static).resolve()
    class Handler(BaseHTTPRequestHandler):
        protocol_version='HTTP/1.1'
        server_version='TrueMoonStudio/0.15'
        def setup(self):super().setup();self.connection.settimeout(30)

        def guard(self,write=False):
            port=self.server.server_address[1];allowed={f'127.0.0.1:{port}',f'localhost:{port}'}
            host=self.headers.get('Host','')
            if host not in allowed:raise StudioError('Недопустимый Host.',403)
            origin=self.headers.get('Origin')
            if origin and origin not in {f'http://{h}' for h in allowed}:raise StudioError('Недопустимый Origin.',403)
            if self.headers.get('Sec-Fetch-Site')=='cross-site':raise StudioError('Межсайтовый запрос отклонён.',403)
            if write and not secrets.compare_digest(self.headers.get('X-Studio-Token',''),token):raise StudioError('Сессия изменилась. Обновите страницу.',403)

        def send_headers(self,status,length,ctype='application/json; charset=utf-8',extra=None):
            self.send_response(status);self.send_header('Content-Type',ctype);self.send_header('Content-Length',str(length))
            self.send_header('X-Content-Type-Options','nosniff');self.send_header('Referrer-Policy','same-origin')
            self.send_header('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'")
            self.send_header('Cache-Control','no-store')
            for k,v in (extra or {}).items():self.send_header(k,str(v))
            self.end_headers()

        def respond(self,value,status=200):
            data=json.dumps(value,ensure_ascii=False).encode('utf-8');self.send_headers(status,len(data))
            if self.command!='HEAD':self.wfile.write(data)

        def body(self):
            if self.headers.get_content_type()!='application/json':raise StudioError('Ожидается JSON.',415)
            try:count=int(self.headers.get('Content-Length','0'))
            except ValueError:raise StudioError('Некорректная длина запроса.')
            if not 0<count<=2*1024*1024:raise StudioError('Размер JSON-запроса вне допустимого диапазона.',413)
            try:data=json.loads(self.rfile.read(count))
            except (ValueError,UnicodeDecodeError):raise StudioError('Некорректный JSON.')
            if not isinstance(data,dict):raise StudioError('Ожидается JSON-объект.')
            return data

        def file(self,path,media=False):
            path=Path(path);size=path.stat().st_size
            try:start,end,partial=byte_range(self.headers.get('Range'),size) if media else (0,size-1,False)
            except StudioError as e:
                self.send_headers(e.status,0,extra={'Content-Range':f'bytes */{size}','Accept-Ranges':'bytes'});return
            extra={'Accept-Ranges':'bytes'} if media else {}
            if partial:extra['Content-Range']=f'bytes {start}-{end}/{size}'
            length=max(0,end-start+1);ctype=mimetypes.guess_type(path.name)[0] or 'application/octet-stream'
            self.send_headers(206 if partial else 200,length,ctype,extra)
            if self.command=='HEAD':return
            with path.open('rb') as f:
                f.seek(start);remaining=length
                while remaining:
                    chunk=f.read(min(256*1024,remaining))
                    if not chunk:break
                    self.wfile.write(chunk);remaining-=len(chunk)

        def route(self):
            write=self.command in ['POST','PATCH','PUT','DELETE'];self.guard(write)
            url=urllib.parse.urlsplit(self.path);path=url.path.rstrip('/') or '/';q=urllib.parse.parse_qs(url.query)
            arg=lambda name,default=None:q.get(name,[default])[0]
            if self.command in ['GET','HEAD']:
                if path=='/api/bootstrap':
                    return self.respond({'version':'0.15.0','token':token,'projects':store.list_projects(),
                        'profile':{'name':'Omni NVFP4 QAD','core':'Omni.v1.1','sizes':[[896,512],[1280,736]],'fps':24,'frame_grid':17,'frame_offset':5,'context_frames':22,'audio_context_frames':18},
                        'generation_available':jobs.python.is_file() and (jobs.core/'run.ps1').is_file(),
                        'agent':{'context_version':2,'brief':True,'requests':True,'automatic_dispatch':False}})
                if path=='/api/projects':return self.respond(store.list_projects())
                if path=='/api/title-fonts':
                    from .titles import catalog
                    return self.respond(catalog())
                if path=='/api/workspace':return self.respond({'configured':not store.paths.legacy_mode,'legacy_read_enabled':store.paths.legacy_read_enabled,'projects_root':str(store.paths.projects_root),'state_root':str(store.state),'mounts':{k:str(v) for k,v in store.paths.mounts.items()},'policy':store.paths.export_policy()})
                if path=='/api/health':return self.respond({'ok':True,'app':'true-moon-studio','version':'0.15.0','gpu_jobs_started_by_health':0})
                if path=='/api/media':
                    p=store.get(arg('project'));f=store.resolve_asset(p,arg('asset'))
                    if f.suffix.lower() not in MEDIA:raise StudioError('Материал недоступен как медиа.',403)
                    return self.file(f,True)
                match=re.fullmatch(r'/api/projects/([^/]+)(?:/(.*))?',path)
                if match:
                    pid,tail=match.groups()
                    if not tail:return self.respond(store.get(pid))
                    if tail in ('asset-library','asset-library/preview'):
                        from .library import catalog, preview_path
                        if tail=='asset-library':return self.respond(catalog(store,pid))
                        return self.file(preview_path(store,pid,arg('file')),True)
                    take_comparison=re.fullmatch(r'takes/([^/]+)/comparison',tail)
                    if take_comparison:
                        from .take_comparison import describe
                        return self.respond(describe(store,jobs,pid,urllib.parse.unquote(take_comparison.group(1))))
                    if tail=='jobs':return self.respond(jobs.progress(pid))
                    if tail=='exports/readiness':
                        from .export_plan import readiness
                        return self.respond(readiness(store,pid))
                    if tail=='exports':return self.respond(exports.list(pid))
                    export_match=re.fullmatch(r'exports/(export-[0-9a-f]{16})(/video)?',tail or '')
                    if export_match:
                        if export_match.group(2):return self.file(exports.video(pid,export_match.group(1)),True)
                        return self.respond(exports.get(pid,export_match.group(1)))
                    if tail=='history':return self.respond(store.history(pid))
                    if tail=='title-layers':return self.respond(store.title_layers(pid))
                    title_image=re.fullmatch(r'title-images/([^/]+)',tail or '')
                    if title_image:return self.file(store.title_image(pid,title_image.group(1)),True)
                    if tail=='timeline':
                        from .timeline import get
                        return self.respond(get(store.get(pid)))
                    if tail=='scenario':return self.respond(scenario.get(store.get(pid)))
                    if tail in ('media-info','waveform','audio-streams') and arg('file') is not None:
                        from .library import KINDS, preview_path
                        file=preview_path(store,pid,arg('file'));kind=KINDS[file.suffix.lower()]
                        if tail=='media-info' and kind=='video':return self.respond(store.probe_video(file))
                        if kind not in ('audio','video') or tail=='media-info':
                            raise StudioError('Этот тип материала здесь не поддерживается.',422)
                        if tail=='audio-streams':return self.respond(store.sound_streams_path(file))
                        stream=arg('stream') or '0'
                        if not re.fullmatch(r'\d{1,2}',stream):raise StudioError('Некорректный звуковой поток.',422)
                        return self.respond(store.waveform_path(pid,file,int(stream)))
                    if tail=='media-info':return self.respond(store.probe_video(store.resolve_asset(store.get(pid),arg('asset'))))
                    if tail=='waveform':
                        stream=arg('stream') or '0'
                        if not re.fullmatch(r'\d{1,2}',stream):raise StudioError('Некорректный звуковой поток.',422)
                        return self.respond(store.waveform(pid,arg('asset'),int(stream)))
                    if tail=='audio-streams':return self.respond(store.sound_streams(pid,arg('asset')))
                    if tail=='sound-preview':return self.file(store.sound_preview(pid,arg('event')),True)
                    if tail=='context':return self.respond(store.context(pid,arg('block')))
                    if tail=='brief':return self.respond(agent.get_brief(store.get(pid)))
                    if tail=='agent/context':return self.respond(agent.context(store,pid,arg('block'),arg('segment'),arg('item')))
                    if tail=='agent/requests':return self.respond(agent.list_requests(store,pid))
                    breakdown_match=re.fullmatch(r'agent/requests/([^/]+)/breakdown',tail)
                    if breakdown_match:return self.respond(agent.preview_breakdown(store,pid,breakdown_match.group(1)))
                    request_match=re.fullmatch(r'agent/requests/([^/]+)',tail)
                    if request_match:return self.respond(agent.get_request(store,pid,request_match.group(1)))
                    if tail=='source':
                        p=store.get(pid);b=store.find_block(p,arg('block'))
                        if not b.get('card_path'):raise StudioError('У блока нет исходной карточки.',404)
                        return self.respond({'path':b['card_path'],'text':store.safe(b['card_path']).read_text(encoding='utf-8-sig')})
                    m=re.fullmatch(r'jobs/([^/]+)',tail)
                    if m:return self.respond(jobs.get(pid,m.group(1)))
                if path.startswith('/api/'):raise StudioError('Маршрут не найден.',404)
                f=(static/('index.html' if path=='/' else urllib.parse.unquote(path).lstrip('/'))).resolve()
                if not f.is_relative_to(static) or not f.is_file() or f.suffix not in ['.html','.js','.css','.svg','.png','.ico']:raise StudioError('Файл не найден.',404)
                return self.file(f)
            if self.command=='POST' and path=='/api/projects':
                d=self.body();return self.respond(store.create(d.get('name','')),201)
            m=re.fullmatch(r'/api/projects/([^/]+)/(.*)',path)
            if not m:raise StudioError('Маршрут не найден.',404)
            pid,tail=m.groups()
            if tail=='upload' and self.command=='POST':return self.upload(pid,q)
            d=self.body();actor=d.get('actor','author');revision=d.get('expected_revision')
            if tail=='title-preview' and self.command=='POST':return self.respond(store.title_preview(pid,revision,d.get('title')))
            if tail=='exports' and self.command=='POST':return self.respond(exports.create(pid,revision,d.get('profile','draft'),actor,d.get('client_key')),202)
            export_match=re.fullmatch(r'exports/(export-[0-9a-f]{16})/stop',tail)
            if export_match and self.command=='POST':return self.respond(exports.stop(pid,export_match.group(1),revision),202)
            if tail=='brief' and self.command=='PATCH':return self.respond(agent.save_brief(store,pid,d.get('brief'),revision,actor))
            if tail=='agent/requests' and self.command=='POST':
                return self.respond(agent.create_request(store,pid,revision,d.get('goal'),d.get('mode','propose'),d.get('block_id'),actor,d.get('scope'),d.get('client_key')),201)
            breakdown_match=re.fullmatch(r'agent/requests/([^/]+)/breakdown',tail)
            if breakdown_match and self.command=='POST':
                return self.respond(agent.apply_breakdown(store,pid,breakdown_match.group(1),revision,actor))
            request_match=re.fullmatch(r'agent/requests/([^/]+)',tail)
            if request_match and self.command=='POST':
                return self.respond(agent.update_request(store,pid,request_match.group(1),revision,d.get('action'),actor,d.get('result',''),d.get('result_target'),d.get('proposal')))
            match=re.fullmatch(r'blocks/([^/]+)',tail)
            if match and self.command=='PATCH':return self.respond(store.save_block(pid,match.group(1),d.get('block',{}),revision,actor))
            if tail=='operations' and self.command=='POST':return self.respond(store.operation(pid,revision,d.get('operation'),d.get('payload',{}),actor))
            if tail=='timeline' and self.command=='POST':return self.respond(store.timeline_operation(pid,revision,d.get('action'),d.get('payload',{}),actor))
            if tail=='scenario' and self.command=='POST':
                with store.lock:
                    project=store.get(pid);agent.check_project_revision(project,revision)
                    if d.get('action')=='add_catalog_video':
                        from .library import add_video
                        updated=add_video(store,project,d.get('payload',{}))
                    else:updated=scenario.apply(store,project,d.get('action'),d.get('payload',{}))
                    return self.respond(store.commit(updated,revision,actor,'Сценарий: '+str(d.get('action'))))
            match=re.fullmatch(r'blocks/([^/]+)/validate',tail)
            if match and self.command=='POST':
                p=store.get(pid);b=store.find_block(p,match.group(1));return self.respond(store.validate(p,b))
            match=re.fullmatch(r'blocks/([^/]+)/prepare',tail)
            if match and self.command=='POST':return self.respond(jobs.prepare(pid,match.group(1),revision,actor),201)
            match=re.fullmatch(r'jobs/([^/]+)/(launch|stop)',tail)
            if match and self.command=='POST':
                jid,action=match.groups();return self.respond(jobs.launch(pid,jid,actor) if action=='launch' else jobs.stop(pid,jid),202)
            raise StudioError('Маршрут не найден.',404)

        def upload(self,pid,q):
            try:count=int(self.headers.get('Content-Length','0'))
            except ValueError:raise StudioError('Некорректная длина загрузки.')
            if not 0<count<=128*1024*1024:raise StudioError('Допустим файл размером до 128 МБ.',413)
            name=Path(q.get('name',['file'])[0]).name;suffix=Path(name).suffix.lower()
            if suffix not in MEDIA:raise StudioError('Поддерживаются изображения, видео и звук.',415)
            expected=int(q.get('revision',['0'])[0]);p=store.get(pid)
            if p['revision']!=expected:raise StudioError('Проект изменился; повторите добавление файла после обновления.',409)
            library=q.get('mode')==['library-video']
            block_id=q.get('block',[None])[0]
            if library and suffix not in VIDEOS:raise StudioError('Выберите MP4, WebM или MOV.',415)
            if library and block_id:raise StudioError('Импорт видео создаёт отдельный источник.',422)
            root=store.project_dir(pid)
            role=q.get('role',['Референс'])[0]
            project_media=q.get('mode')==['project-media']
            if library or (project_media and suffix in VIDEOS):dest='video'
            elif project_media and suffix in AUDIO:dest='Audio'
            elif project_media and suffix in IMAGES:dest='Frames' if role=='Опорный кадр' else 'References'
            else:dest=None
            folder=store.writable(root/dest if dest else store.production_dir(pid)/'Imports')
            if dest and folder.parent!=root:raise StudioError('Каталог материалов находится вне проекта.',403)
            folder.mkdir(parents=True,exist_ok=True)
            basename=re.sub(r'[^\w. -]','_',name,flags=re.UNICODE)[-100:]
            target=store.writable(folder/(uid('asset-')+'-'+basename),False)
            created=False
            try:
                with target.open('xb') as f:
                    created=True;remaining=count
                    while remaining:
                        chunk=self.rfile.read(min(1024*1024,remaining))
                        if not chunk:raise StudioError('Загрузка прервалась.')
                        f.write(chunk);remaining-=len(chunk)
                if library:
                    result=store.import_video(pid,expected,target,name)
                else:
                    result=store.operation(pid,expected,'add_asset',
                        {'path':str(target),'block_id':block_id,'role':role})
            except Exception:
                if created:target.unlink(missing_ok=True)
                raise
            return self.respond(result,201)

        def handle_route(self):
            try:self.route()
            except StudioError as error:
                self.close_connection=True;self.respond({'error':str(error),'details':error.details},error.status)
            except (ConnectionError,TimeoutError):self.close_connection=True
            except Exception as error:
                self.close_connection=True
                import traceback;traceback.print_exc()
                self.respond({'error':'Внутренняя ошибка. Сохранённые исходники не перезаписаны.','details':type(error).__name__},500)

        do_GET=handle_route;do_HEAD=handle_route;do_POST=handle_route;do_PATCH=handle_route
        def log_message(self,format,*args):
            if self.command not in ['GET','HEAD'] or (len(args)>1 and str(args[1]).startswith(('4','5'))):super().log_message(format,*args)
    return Handler

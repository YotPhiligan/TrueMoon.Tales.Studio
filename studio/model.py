"""Project persistence, import, validation and agent context. No GPU side effects."""
from __future__ import annotations
import copy,hashlib,json,math,mimetypes,os,re,struct,threading,time,uuid
from pathlib import Path
from datetime import datetime,timezone
from .paths import WorkspacePaths,PathPolicyError

FIELDS=['subject_definitions','summary','retention_analysis','detailed_description','overall_soundscape','non_diegetic_music']
IMAGES={'.png','.jpg','.jpeg','.webp','.avif'}
VIDEOS={'.mp4','.webm','.mov'}
AUDIO={'.mp3','.wav','.ogg','.flac','.m4a','.aac','.opus'}
MEDIA=IMAGES|VIDEOS|AUDIO
ACTIVE={'queued','preparing','running','collecting','stopping'}

class StudioError(Exception):
    def __init__(self,message,status=400,details=None):super().__init__(message);self.status=status;self.details=details

def now():return datetime.now(timezone.utc).isoformat()
def uid(prefix=''):return prefix+uuid.uuid4().hex[:16]
def digest(path):
    h=hashlib.sha256()
    with Path(path).open('rb') as f:
        for b in iter(lambda:f.read(1024*1024),b''):h.update(b)
    return h.hexdigest()
def read(path):return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def atomic(path,value):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    temp=path.with_name(path.name+'.'+uuid.uuid4().hex+'.pending')
    with temp.open('x',encoding='utf-8',newline='\n') as f:
        json.dump(value,f,ensure_ascii=False,indent=2);f.flush();os.fsync(f.fileno())
    start=time.monotonic()
    while True:
        try:temp.replace(path);break
        except PermissionError:
            if time.monotonic()-start>2:raise
            time.sleep(.02)
def split_prompt(text):
    result={name:'' for name in FIELDS};matches=list(re.finditer(r'^('+ '|'.join(FIELDS)+r'):\s*',text,re.M))
    for i,m in enumerate(matches):result[m.group(1)]=text[m.end():matches[i+1].start() if i+1<len(matches) else len(text)].strip()
    return result
def join_prompt(sections):return '\n\n'.join(f'{k}:\n{sections.get(k,"").strip()}' for k in FIELDS)
def timing(seconds,mode='guided'):
    try:value=float(seconds)
    except (TypeError,ValueError):raise StudioError('Длительность должна быть числом.')
    if not math.isfinite(value) or not 2<=value<=8:raise StudioError('Для текущего профиля допустим источник от 2 до 8 секунд.')
    n=round(value*24);n+=(5-n%17)%17;trim=22 if mode=='context' else 0
    return {'requested_seconds':value,'frames':n,'fps':24,'source_seconds':n/24,'trim_frames':trim,'new_frames':n-trim,'usable_seconds':(n-trim)/24}
def block_hash(block):
    data={k:block.get(k) for k in ['prompt','references','guides','settings','context_take_id']}
    return hashlib.sha256(json.dumps(data,sort_keys=True,ensure_ascii=False).encode()).hexdigest()

class Store:
    def __init__(self,book:Path,amv:Path,state_dir:Path,*,paths=None):
        self.paths=paths or WorkspacePaths.legacy(book,amv,state_dir)
        self.book=self.paths.legacy_root;self.amv=self.paths.amv_root;self.state=self.paths.state_root;self.lock=threading.RLock()
        self.audio_lock=threading.RLock()
        self.title_lock=threading.RLock()
        self.writable(self.state,False).mkdir(parents=True,exist_ok=True);self.registry_path=self.writable(self.state/'Registry.json',False)
        self.registry=read(self.registry_path) if self.registry_path.exists() else {'projects':[]}

    def safe(self,path,exists=True):
        try:return self.paths.resolve(path,exists)
        except PathPolicyError as error:raise StudioError(str(error),error.status) from error

    def writable(self,path,exists=False):
        try:return self.paths.resolve(path,exists,write=True)
        except PathPolicyError as error:raise StudioError(str(error),error.status) from error

    def relative(self,path):
        try:return self.paths.reference(path)
        except PathPolicyError as error:raise StudioError(str(error),error.status) from error

    def cache_dir(self,pid,kind):
        if kind not in ['Audio','Sound','Titles']:raise StudioError('Некорректный вид кэша.',422)
        base=self.paths.cache_root/pid if self.paths.cache_root else self.production_dir(pid)/'Preview'
        return self.writable(base/kind,False)

    def adopt_registry(self,filename):
        """Validate first, then copy registry only. Project documents remain byte-identical."""
        with self.lock:
            value=read(self.safe(filename))
            if not isinstance(value,dict) or not isinstance(value.get('projects'),list):raise StudioError('Некорректный реестр.',422)
            ids=set();roots=set()
            for row in value['projects']:
                if not isinstance(row,dict) or not isinstance(row.get('id'),str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',row['id']):raise StudioError('Некорректный ID проекта в реестре.',422)
                root=self.safe(row.get('root'),False);project=read(self.project_file(root))
                if row['id'] in ids or root in roots or project.get('id')!=row['id'] or self.safe(project.get('root'),False)!=root:raise StudioError('Конфликт ID или путей проектов в реестре.',409)
                ids.add(row['id']);roots.add(root)
            if self.registry.get('projects') and self.registry!=value:raise StudioError('Новый реестр уже содержит другие проекты; он не перезаписан.',409)
            if not self.registry_path.exists() or self.registry!=value:atomic(self.registry_path,value)
            self.registry=value
            return self.list_projects()

    def project_dir(self,project_id):
        row=next((r for r in self.registry['projects'] if r['id']==project_id),None)
        if not row:raise StudioError('Проект не найден.',404)
        return self.safe(row['root'],False)

    def production_dir(self,project_id):
        return self.safe(self.project_dir(project_id)/'Production',False)

    def project_file(self,root):
        current=self.safe(root/'Studio.project.json',False)
        legacy=self.safe(root/'Production/Studio.project.json',False)
        if current.exists() and legacy.exists():
            raise StudioError('В проекте два файла Studio.project.json. Устраните конфликт расположений.',409)
        return legacy if legacy.exists() or (self.paths.legacy_mode and not current.exists()) else current

    def location(self,project_id):
        return self.project_file(self.project_dir(project_id))

    def list_projects(self):
        result=[]
        with self.lock:
            for row in self.registry['projects']:
                p=self.get(row['id'])
                result.append({k:p[k] for k in ['id','name','root','revision','updated_at']}|{'blocks':len(p['blocks']),'segments':len(p['segments'])})
        return result

    def get(self,project_id):
        with self.lock:return read(self.location(project_id))

    def timeline_operation(self,pid,revision,action,payload,actor='author'):
        from .timeline import apply,capacity,TimelineError,get as get_timeline
        with self.lock:
            p=self.get(pid)
            if type(revision) is not int or p['revision']!=revision:raise StudioError('Таймлайн изменился в другой сессии. Обновите проект перед переносом.',409)
            if action=='set_transition':
                from .scenario import get as scenario_view
                if scenario_view(p)['issues']:raise StudioError('Исправьте время и конфликты сценария перед настройкой перехода.',422)
            from .scenario import translate
            translated=translate(self,p,action,payload)
            if translated is not None:return self.commit(translated,revision,actor,'Сценарный таймлайн: '+str(action))
            if action in ['add_sound','update_sound'] and isinstance(payload,dict):
                from . import sound
                e=next((e for e in sound.events(get_timeline(p)) if e['id']==payload.get('id')), {}) if action=='update_sound' else {}
                aid=payload.get('asset_id',e.get('asset_id'));stream=payload.get('stream',e.get('stream',0))
                if type(stream) is not int or not 0<=stream<64:raise StudioError('Некорректный звуковой поток.',422)
                if 'asset_selection' in payload:
                    from .library import select
                    aid,path=select(self,p,payload['asset_selection'],('audio','video'))
                    wave=self.waveform_path(pid,path,stream)
                else:wave=self.waveform(pid,aid,stream)
                payload={**payload,'asset_id':aid,'source_seconds':wave['seconds']}
            if action=='set_music' and isinstance(payload,dict):
                aid=payload.get('asset_id')
                if 'asset_selection' in payload:
                    from .library import select
                    aid,path=select(self,p,payload['asset_selection'],('audio',))
                    wave=self.waveform_path(pid,path)
                    payload={**payload,'asset_id':aid}
                else:
                    if not isinstance(aid,str) or p['assets'].get(aid,{}).get('kind')!='audio':
                        raise StudioError('Выберите звуковой файл из библиотеки проекта.',422)
                    wave=self.waveform(pid,aid)
                p['assets'][aid]['audio_info']={k:wave[k] for k in ['seconds','sample_rate','channels','source_sha256']}
            if action in ['add_clip','update_clip'] and isinstance(payload,dict) and payload.get('take_id'):
                clip=next((c for c in get_timeline(p)['clips'] if c['id']==payload.get('id')),None)
                bid=payload.get('block_id') if action=='add_clip' else (clip or {}).get('block_id')
                b=next((b for b in p['blocks'] if b['id']==bid),None)
                take=next((t for t in (b or {}).get('takes',[]) if t['id']==payload['take_id']),None)
                if take:
                    try:capacity(take,p.get('fps',24))
                    except TimelineError:take['video_info']=self.probe_video(self.resolve_asset(p,take['video_asset_id']))
            try:p['timeline']=apply(p,action,payload)
            except TimelineError as error:raise StudioError(str(error),422)
            if action in ['add_title','update_title']:
                self.render_title_layers(p)
            return self.commit(p,revision,actor,'Таймлайн: '+str(action))

    def render_title(self,project,row):
        from .titles import render_layer
        try:return render_layer(self,project,row)
        except (ValueError,OSError) as error:raise StudioError(str(error),422) from error

    def title_layers(self,pid):
        p=self.get(pid)
        return {'project_revision':p['revision'],'canvas_size':p['output_size'],'layers':[{k:v for k,v in layer.items() if k!='path'} for layer in self.render_title_layers(p)]}

    def render_title_layers(self,project,rows=None):
        layers=[self.render_title(project,row) for row in (project.get('timeline',{}).get('titles',[]) if rows is None else rows)]
        if sum(r['width']*r['height'] for r in layers)>16000000:raise StudioError('Суммарный размер титров слишком велик. Уменьшите шрифты или число титров.',422)
        return layers

    def title_preview(self,pid,revision,payload):
        from .titles import new_row
        p=self.get(pid)
        if type(revision) is not int or revision!=p['revision']:raise StudioError('Проект изменился. Откройте редактор титра заново.',409)
        if not isinstance(payload,dict):raise StudioError('Некорректные параметры титра.',422)
        layer=self.render_title(p,new_row(payload,p['fps'],identifier='title-preview'))
        return {'project_revision':p['revision'],'layer':{k:v for k,v in layer.items() if k!='path'}}

    def title_image(self,pid,key):
        if not isinstance(key,str) or not re.fullmatch(r'[0-9a-f]{64}',key):raise StudioError('Некорректный слой титра.',422)
        return self.safe(self.cache_dir(pid,'Titles')/(key+'.png'))

    def waveform(self,pid,aid,stream=0):
        if not isinstance(aid,str):raise StudioError('Некорректный звуковой материал.',422)
        p=self.get(pid);asset=p['assets'].get(aid)
        if not asset:raise StudioError('Материал не найден.',404)
        if asset['kind'] not in ['audio','video']:raise StudioError('Для звуковой волны нужен аудио или видео файл.',422)
        return self.waveform_path(pid,self.resolve_asset(p,aid),stream)

    def waveform_path(self,pid,source,stream=0):
        from . import audio
        if type(stream) is not int or not 0<=stream<64:raise StudioError('Некорректный звуковой поток.',422)
        fingerprint=digest(source)
        cache=self.writable(self.cache_dir(pid,'Audio')/f'{fingerprint}{".stream"+str(stream) if stream else ""}.v{audio.VERSION}.json',False)
        with self.audio_lock:
            if cache.is_file():
                try:
                    data=read(cache)
                    if audio.valid(data) and data.get('source_sha256')==fingerprint:return data
                except (OSError,ValueError):pass
            try:data=audio.analyse_bounded(source,stream) if stream else audio.analyse_bounded(source)
            except audio.AudioError as error:raise StudioError(str(error),422)
            if digest(source)!=fingerprint:raise StudioError('Звуковой файл изменился во время анализа. Повторите выбор.',409)
            data['source_sha256']=fingerprint;atomic(cache,data);return data

    def sound_streams(self,pid,aid):
        if not isinstance(aid,str):raise StudioError('Некорректный звуковой материал.',422)
        p=self.get(pid);asset=p['assets'].get(aid)
        if not asset or asset['kind'] not in ['audio','video']:raise StudioError('Выберите аудио или видео.',422)
        return self.sound_streams_path(self.resolve_asset(p,aid))

    @staticmethod
    def sound_streams_path(path):
        from . import sound
        try:return sound.streams(path)
        except ValueError as error:raise StudioError(str(error),422) from error

    def sound_preview(self,pid,eid):
        from . import sound,timeline
        p=self.get(pid);e=next((e for e in sound.events(timeline.get(p)) if e['id']==eid),None)
        if not e:raise StudioError('Звуковое событие не найдено.',404)
        source=self.resolve_asset(p,e['asset_id']);fingerprint=digest(source)
        key=hashlib.sha256(json.dumps([fingerprint,e['stream'],e['source_in_frame'],e['duration_frames'],p['fps']]).encode()).hexdigest()
        target=self.writable(self.cache_dir(pid,'Sound')/f'{key}.wav',False)
        with self.audio_lock:
            if target.is_file():return target
            target.parent.mkdir(parents=True,exist_ok=True);temp=target.with_name(uid('sound-')+'.wav')
            try:sound.preview(source,temp,e['stream'],e['source_in_frame']/p['fps'],e['duration_frames']/p['fps'])
            except ValueError as error:raise StudioError(str(error),422) from error
            if digest(source)!=fingerprint:raise StudioError('Источник изменился во время подготовки звука.',409)
            temp.replace(target);return target

    @staticmethod
    def probe_video(path):
        """Read legacy MP4 metadata on explicit montage binding, without GPU."""
        try:
            import av
            with av.open(str(path)) as container:
                stream=container.streams.video[0];fps=float(stream.average_rate or stream.base_rate or 0)
                frames=stream.frames or None
                seconds=frames/fps if frames and fps else float(stream.duration*stream.time_base) if stream.duration is not None else (container.duration or 0)/av.time_base
                if not math.isfinite(seconds) or seconds<=0:raise ValueError('missing video duration')
                return {'frames':frames,'fps':fps,'seconds':seconds,'width':stream.width,'height':stream.height,'silent':not len(container.streams.audio),'metadata_source':'media_header'}
        except Exception as error:raise StudioError('Не удалось прочитать метаданные видео: '+type(error).__name__+'.',422)

    def commit(self,project,expected,actor,action):
        with self.lock:
            path=self.writable(self.location(project['id']));current=read(path)
            if type(expected) is not int or current['revision']!=expected:
                raise StudioError('Проект изменился в другой сессии. Сохранённые правки не перезаписаны.',409,{'current_revision':current['revision']})
            value=copy.deepcopy(project);value['revision']=expected+1;value['updated_at']=now()
            value['activity']=(value.get('activity',[])+[{'at':now(),'actor':str(actor)[:80],'action':action,'revision':value['revision']}])[-100:]
            history=self.writable(self.production_dir(project['id'])/'History'/f'r{current["revision"]:06d}-{uid()}.json',False)
            atomic(history,current);atomic(path,value);return value

    def asset(self,project,path):
        p=self.safe(path);matches=[]
        for key,value in project['assets'].items():
            try:equal=self.safe(value['path'],False)==p
            except StudioError:equal=False
            if equal:matches.append((key,value))
        if len(matches)>1:raise StudioError('Несколько ID материалов указывают на один файл; требуется явное разрешение конфликта.',409)
        rel=matches[0][1]['path'] if matches else self.relative(p)
        aid=matches[0][0] if matches else uuid.uuid5(uuid.NAMESPACE_URL,rel.casefold()).hex[:20]
        if p.suffix.lower() in IMAGES:kind='image'
        elif p.suffix.lower() in VIDEOS:kind='video'
        elif p.suffix.lower()=='.safetensors':kind='latent'
        elif p.suffix.lower() in MEDIA:kind='audio'
        else:raise StudioError('Этот тип файла не поддерживается.')
        stat=p.stat();existing=project['assets'].get(aid)
        project['assets'][aid]={**(existing or {}),'id':aid,'path':rel,'name':p.name,'kind':kind,'bytes':stat.st_size,'modified_ns':stat.st_mtime_ns,
                               'sha256':existing.get('sha256') if existing and existing['modified_ns']==stat.st_mtime_ns else None}
        return aid

    def import_video(self,pid,revision,path,name):
        with self.lock:
            project=self.get(pid)
            if project['revision']!=revision:raise StudioError('Проект изменился; повторите импорт после обновления.',409)
            info=self.probe_video(path)
            asset_id=self.asset(project,path)
            block,_=self.video_source(project,asset_id,name,info)
            return self.commit(project,revision,'author','Импортировано видео '+block['title'])

    def video_source(self,project,asset_id,name,info):
        group=next((row for row in project['segments'] if row.get('imported_videos')),None)
        if group is None:
            group={'id':uid('seg-'),'name':'Импортированное видео','notes':'','block_ids':[],'imported_videos':True}
            project['segments'].append(group)
        title=Path(name).stem[:160].strip() or 'Импортированное видео'
        block=self.new_block(group['id'],title)
        block['title']=title
        take={'id':uid('take-'),'name':name,'video_asset_id':asset_id,
              'video_info':info,'native_av_asset_id':None,'last_frame_asset_id':None,
              'technical_status':'imported','review_state':'unreviewed','notes':'',
              'created_at':now(),'imported':True}
        block['takes'].append(take)
        group['block_ids'].append(block['id'])
        project['blocks'].append(block)
        return block,take

    def resolve_asset(self,project,asset_id):
        asset=project['assets'].get(asset_id)
        if not asset:raise StudioError('Материал не найден.',404)
        return self.safe(asset['path'])

    def create(self,name,root=None,project_id=None,import_existing=False):
        with self.lock:
            if not str(name).strip():raise StudioError('Укажите название проекта.')
            project_id=project_id or uid('project-')
            if not isinstance(project_id,str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',project_id):raise StudioError('Некорректный ID проекта.',422)
            if any(r['id']==project_id for r in self.registry['projects']):raise StudioError('Этот ID проекта уже используется.',409)
            folder=self.writable(root,False) if root else self.writable(self.paths.projects_root/project_id,False)
            if self.paths.legacy_mode and not folder.is_relative_to(self.amv):raise StudioError('Каталог проекта должен находиться внутри AMV.',403)
            if any(self.safe(r['root'],False)==folder for r in self.registry['projects']):raise StudioError('Этот каталог уже открыт как проект.',409)
            path=self.writable(self.project_file(folder),False)
            if path.exists():raise StudioError('В каталоге уже есть состояние Studio; оно не перезаписано.',409)
            p={'schema_version':1,'id':project_id,'name':str(name).strip()[:160],'root':self.relative(folder),
               'revision':1,'created_at':now(),'updated_at':now(),'fps':24,'output_size':[1280,736],'episodes':[],'scenes':[],
               'assets':{},'segments':[],'blocks':[],'activity':[],'import_notes':[]}
            if import_existing:self.import_materials(p,folder)
            if not p['segments']:p['segments']=[{'id':uid('seg-'),'name':'Первый сегмент','notes':'','block_ids':[]}]
            atomic(path,p)
            self.registry['projects'].append({'id':project_id,'root':p['root']});atomic(self.registry_path,self.registry)
            return p

    def seed(self):
        if self.registry['projects']:return
        for name,folder,pid in [('Клэр · Satisfy','Clair','claire'),('Сказки истинной луны · Опенинг','Opening','opening')]:
            root=self.amv/folder;state=self.project_file(root)
            if state.exists():
                existing=read(state)
                if existing.get('id')!=pid:raise StudioError('Найден проект с другим ID; автоматический импорт остановлен.',409)
                self.registry['projects'].append({'id':pid,'root':existing['root']});atomic(self.registry_path,self.registry)
            elif root.exists():self.create(name,root,pid,True)

    def result_index(self):
        index={};configs={}
        results=self.amv/'Research/Results'
        if not results.exists():return index,configs
        for f in results.rglob('Result.json'):
            try:
                r=read(f)
                if isinstance(r,dict) and isinstance(r.get('id'),str) and isinstance(r.get('outputs'),dict):
                    index[r['id']]=(f.parent,r)
                    if (f.parent/'Job.json').exists():configs[r['id']]=(f.parent,read(f.parent/'Job.json'))
            except (OSError,ValueError,KeyError):continue
        return index,configs

    def reconcile_imports(self):
        """Correct early import-only comparison records; preserve review choices and all files."""
        for row in self.registry['projects']:
            with self.lock:
                p=self.get(row['id']);changed=False
                for b in p['blocks']:
                    kept=[]
                    for t in b['takes']:
                        comparison=bool(re.search(r'(?:^|[._-])(?:compare|comparison|SameTimeline|vs)(?:[._-]|$)',t['name'],re.I))
                        if comparison and t.get('imported') and t.get('review_state')=='unreviewed' and b.get('selected_take_id')!=t['id']:changed=True
                        else:kept.append(t)
                    b['takes']=kept
                if changed:self.commit(p,p['revision'],'importer','Сравнительные сборки сохранены как материалы, не как генерационные дубли')

    def new_block(self,segment_id,label='Новый блок'):
        return {'id':uid('block-'),'label':label,'title':'Новый блок','segment_id':segment_id,'notes':'','card_path':None,
                'references':[],'guides':[],'prompt':join_prompt({**{f:'' for f in FIELDS},'overall_soundscape':'N/A','non_diegetic_music':'N/A'}),
                'settings':{'mode':'guided','seconds':2.0,'size':[896,512],'seed':'148096032077132'},
                'montage_seconds':None,'context_take_id':None,'takes':[],'selected_take_id':None,'status':'draft'}

    def import_materials(self,p,folder):
        prefix='CL' if folder.name=='Clair' else 'OP';cards={}
        for f in sorted((folder/'Shots').glob('*.md')) if (folder/'Shots').exists() else []:
            match=re.match(r'('+prefix+r'-[A-Za-z0-9]+)',f.stem)
            if not match:continue
            label=match.group(1)
            if label not in cards or len(f.stem)<len(cards[label].stem):cards[label]=f
        groups={}
        for label,card in cards.items():
            group=('Исследования' if re.match(r'CL-(?:MC|PT|SD|SC|AC|RP|MR|PV|ST|TE|SA)',label) else
                   'Воспоминания' if label.startswith('CL-F') else 'Операция' if prefix=='CL' else 'Опенинг')
            if group not in groups:
                segment={'id':uid('seg-'),'name':group,'notes':'Импорт существующих карточек. Отбор и монтажные окна проверяются отдельно.','block_ids':[]}
                groups[group]=segment;p['segments'].append(segment)
            seg=groups[group];b=self.new_block(seg['id'],label);text=card.read_text(encoding='utf-8-sig')
            title=text.splitlines()[0].lstrip('# ').strip() if text else label
            b.update(title=re.sub(r'^'+re.escape(label)+r'\s*[—–-]?\s*','',title),card_path=self.relative(card))
            prompt_file=next(iter(sorted((folder/'Shots').glob(label+'*.Prompt.txt'))),None)
            if prompt_file:b['prompt']=prompt_file.read_text(encoding='utf-8-sig').strip()
            b['notes']='Импортировано из '+card.name+'. Версии материалов и монтажное окно требуют проверки.'
            p['blocks'].append(b);seg['block_ids'].append(b['id'])
        index,configs=self.result_index()
        # Prefer available reproducible settings, explicitly as an editable candidate, never as approval.
        for b in p['blocks']:
            matching=[(ident,row) for ident,row in configs.items() if ident.startswith(b['label']+'.') or ident==b['label']]
            matching.sort(key=lambda x:x[1][0].stat().st_mtime,reverse=True)
            if matching:
                _,(job,cfg)=matching[0]
                try:
                    refs=[]
                    for r in cfg['references']:
                        path=(job/r['path']).resolve() if not Path(r['path']).is_absolute() else Path(r['path'])
                        refs.append({'id':uid('ref-'),'asset_id':self.asset(p,path),'role':r.get('role','Референс')})
                    b['references']=refs
                    b['guides']=[{'reference_id':refs[g['picture']-1]['id'],'frame':g['frame']} for g in cfg.get('guides',[])]
                    if (job/'Prompt.txt').exists():b['prompt']=(job/'Prompt.txt').read_text(encoding='utf-8-sig')
                    b['settings']={'mode':'context' if cfg.get('profile')=='omni-context' else 'guided','seconds':cfg.get('seconds',2),
                                   'size':cfg.get('size',[896,512]),'seed':str(cfg.get('seed',148096032077132))}
                    b['import_config']=self.relative(job/'Job.json')
                    b['import_previous_latent']=cfg.get('previous_latent')
                except (StudioError,KeyError,IndexError,OSError):pass
            if not b['references']:
                candidates=sorted((folder/'Frames').glob(b['label']+'.FirstFrame.H3.*.png')) if (folder/'Frames').exists() else []
                if candidates:
                    aid=self.asset(p,candidates[-1]);rid=uid('ref-');b['references']=[{'id':rid,'asset_id':aid,'role':'Начальная композиция — требует проверки'}];b['guides']=[{'reference_id':rid,'frame':0}]
        # Keep New over Raw when they are two deliveries of the same take.
        video_files=sorted((folder/'video').glob('*.mp4')) if (folder/'video').exists() else []
        choices={}
        for f in video_files:
            stem=re.sub(r'\.(Raw|New)$','',f.stem)
            if re.search(r'(?:^|[._-])(?:compare|comparison|SameTimeline|vs)(?:[._-]|$)',stem,re.I):
                self.asset(p,f);continue  # review assemblies remain assets, not generation takes
            if stem not in choices or f.stem.endswith('.New'):choices[stem]=f
        labels=sorted((b['label'] for b in p['blocks']),key=len,reverse=True)
        for stem,f in choices.items():
            label=next((x for x in labels if stem==x or stem.startswith(x+'.')),None)
            if not label:continue
            b=next(b for b in p['blocks'] if b['label']==label)
            try:
                take={'id':uid('take-'),'name':stem,'video_asset_id':self.asset(p,f),'native_av_asset_id':None,'last_frame_asset_id':None,
                      'review_state':'unreviewed','notes':'Архивный дубль; импорт не означает приёмку.','technical_status':'imported','created_at':now(),'imported':True}
                hit=index.get(stem)
                if hit:
                    job,result=hit;outs=result['outputs'];take['technical_status']='success';take['job_path']=self.relative(job)
                    for node,key in [('200.0','native_av_asset_id'),('202.0','last_frame_asset_id')]:
                        if node in outs:take[key]=self.asset(p,outs[node]['path'])
                    take['metrics']={k:result.get(k) for k in ['server_wall_seconds','device_gpu_peak_GiB','rss_peak_GiB']}
                    kind='207' if f.stem.endswith('.New') else '92';take['video_info']=result.get('videos',{}).get(kind)
                b['takes'].append(take)
            except (StudioError,OSError):continue
        # Link imported context settings to their actual source take if available.
        for b in p['blocks']:
            previous=b.pop('import_previous_latent',None)
            if previous:
                for owner in p['blocks']:
                    for t in owner['takes']:
                        aid=t.get('native_av_asset_id')
                        if aid and self.resolve_asset(p,aid)==Path(previous).resolve():b['context_take_id']=t['id']
        p['import_notes']=['Карточки и исходники только прочитаны. Новейшие настройки импортированы как кандидаты, не утверждённые версии.',
                           'Монтажные окна не вычислялись из номеров/длин файлов. Текст плана и музыкальная сетка сохранены.']

    def find_block(self,p,block_id):
        block=next((b for b in p['blocks'] if b['id']==block_id),None)
        if not block:raise StudioError('Блок не найден.',404)
        return block

    def save_block(self,pid,bid,data,revision,actor='author'):
        with self.lock:
            p=self.get(pid);b=self.find_block(p,bid)
            for key in ['title','notes','prompt','references','guides','settings','montage_seconds','context_take_id']:
                if key in data:b[key]=copy.deepcopy(data[key])
            self.validate_shape(p,b)
            b['status']='ready' if not self.validate(p,b)['errors'] else 'draft'
            return self.commit(p,revision,actor,'Изменён блок '+b['label'])

    def validate_shape(self,p,b):
        if not isinstance(b.get('prompt'),str) or len(b['prompt'])>100000:raise StudioError('Некорректный текст промпта.')
        if not isinstance(b.get('references'),list) or len(b['references'])>8:raise StudioError('Допустимо до восьми референсов.')
        ids=[]
        for ref in b['references']:
            if not isinstance(ref,dict) or not isinstance(ref.get('id'),str) or ref.get('asset_id') not in p['assets']:raise StudioError('Некорректная ссылка на материал.')
            if p['assets'][ref['asset_id']]['kind']!='image':raise StudioError('Референс должен быть изображением.')
            if not isinstance(ref.get('role'),str) or len(ref['role'])>1000:raise StudioError('Некорректная роль референса.')
            ids.append(ref['id'])
        if len(ids)!=len(set(ids)):raise StudioError('ID референсов должны различаться.')
        if not isinstance(b.get('guides'),list) or len(b['guides'])>4:raise StudioError('Допустимо до четырёх опор.')
        for g in b['guides']:
            if g.get('reference_id') not in ids or type(g.get('frame')) is not int:raise StudioError('Опора должна ссылаться на референс и целый индекс кадра.')
        settings=b.get('settings',{})
        if settings.get('mode') not in ['guided','context']:raise StudioError('Неизвестный режим генерации.')
        timing(settings.get('seconds'),settings['mode'])
        if settings.get('size') not in [[896,512],[1280,736]]:raise StudioError('Выберите размер текущего профиля.')
        try:seed=int(str(settings.get('seed','')))
        except ValueError:raise StudioError('Seed должен быть целым числом.')
        if not 0<=seed<2**64:raise StudioError('Seed вне диапазона uint64.')
        settings['seed']=str(seed)
        if b.get('montage_seconds') is not None and (type(b['montage_seconds']) not in [int,float] or not math.isfinite(b['montage_seconds']) or b['montage_seconds']<=0):raise StudioError('Монтажная длительность должна быть положительной.')

    def validate(self,p,b):
        errors=[];warnings=[]
        try:self.validate_shape(p,b)
        except StudioError as e:return {'errors':[str(e)],'warnings':[],'timing':None}
        t=timing(b['settings']['seconds'],b['settings']['mode']);refs=b['references'];text=b['prompt']
        fields=re.findall(r'^([a-z_]+):',text,re.M)
        if fields!=FIELDS:errors.append('Промпт должен содержать ровно шесть разделов Ref2VA в установленном порядке.')
        if {int(n) for n in re.findall(r'<Picture (\d+)>',text)}!=set(range(1,len(refs)+1)):errors.append('Метки Picture должны совпадать с номерами всех подключённых референсов.')
        if re.search(r'<(?:Video|Audio) \d+>',text):errors.append('В первой версии этот профиль принимает картинки и native AV; Video/Audio reference не подключён.')
        if '[удалённый референс]' in text:errors.append('Исправьте места промпта, относящиеся к удалённому референсу.')
        sections=split_prompt(text)
        for key in FIELDS[:-2]:
            if not sections[key]:errors.append('Не заполнен раздел '+key+'.')
        if any(sections[k]!='N/A' for k in FIELDS[-2:]):errors.append('Текущий профиль без звука: оба звуковых раздела должны быть N/A.')
        if not refs:errors.append('Добавьте хотя бы один референс.')
        for i,r in enumerate(refs,1):
            if not r['role'].strip():errors.append(f'Укажите роль Picture {i}.')
            try:self.resolve_asset(p,r['asset_id'])
            except StudioError as e:errors.append(str(e))
        times=[]
        if not b['guides']:errors.append('Назначьте хотя бы одну временную опору.')
        for g in b['guides']:
            frame=t['frames']-1 if g['frame']==-1 else g['frame'];times.append(frame)
            if not 0<=frame<t['frames']:errors.append('Индекс опоры выходит за длину источника.')
        if len(times)!=len(set(times)):errors.append('Две опоры назначены на один кадр.')
        if b['settings']['mode']=='context':
            take=self.find_take(p,b.get('context_take_id'))
            if not take or not take.get('native_av_asset_id'):errors.append('Для продолжения выберите дубль с сохранённым native AV-latent.')
            else:
                try:
                    path=self.resolve_asset(p,take['native_av_asset_id'])
                    with path.open('rb') as f:
                        prefix=f.read(8)
                        if len(prefix)!=8:raise ValueError('truncated latent prefix')
                        count=struct.unpack('<Q',prefix)[0]
                        if not 0<count<=1024*1024:raise ValueError('invalid latent header')
                        raw=f.read(count)
                        if len(raw)!=count:raise ValueError('truncated latent header')
                        header=json.loads(raw)
                    if not isinstance(header,dict):raise ValueError('invalid latent object')
                    if not {'video','audio'}<=header.keys():errors.append('Нужен парный native AV-latent.')
                    elif not isinstance(header['video'],dict) or not isinstance(header['video'].get('shape'),list) or len(header['video']['shape'])<2:raise ValueError('invalid video shape')
                    elif header['video']['shape'][-2:]!=[b['settings']['size'][1]//16,b['settings']['size'][0]//16]:errors.append('Размер продолжения должен совпадать с исходным AV-latent.')
                except (StudioError,OSError,ValueError,KeyError,TypeError,struct.error):errors.append('Не удалось проверить native AV-latent.')
            if 0 in times:warnings.append('Motion Context заменяет стартовый Guide внутри контекстного начала.')
        if t['usable_seconds']<float(b.get('montage_seconds') or 0):warnings.append('Полезный выход короче монтажного окна; автоматического ускорения или заполнения нет.')
        warnings.append('Готовый промпт не гарантирует траекторию и тайминг: результат требует просмотра.')
        return {'errors':list(dict.fromkeys(errors)),'warnings':warnings,'timing':t}

    def find_take(self,p,tid):return next((t for b in p['blocks'] for t in b['takes'] if t['id']==tid),None)

    def operation(self,pid,revision,operation,payload,actor='author'):
        with self.lock:
            p=self.get(pid)
            if operation=='add_segment':p['segments'].append({'id':uid('seg-'),'name':str(payload.get('name','Новый сегмент'))[:160],'notes':'','block_ids':[]})
            elif operation=='add_block':
                s=next((s for s in p['segments'] if s['id']==payload.get('segment_id')),None)
                if not s:raise StudioError('Сегмент не найден.',404)
                b=self.new_block(s['id'],str(payload.get('label') or 'Блок '+str(len(p['blocks'])+1)));p['blocks'].append(b);s['block_ids'].append(b['id'])
            elif operation=='reorder_references':
                b=self.find_block(p,payload['block_id']);order=payload['order'];old=b['references']
                if len(order)!=len(old) or set(order)!={r['id'] for r in old}:raise StudioError('Перестановка должна содержать все ID ровно один раз.')
                mapping={i+1:order.index(r['id'])+1 for i,r in enumerate(old)}
                b['prompt']=re.sub(r'<Picture (\d+)>',lambda m:f'<Picture {mapping.get(int(m.group(1)),int(m.group(1)))}>',b['prompt'])
                b['references']=[next(r for r in old if r['id']==rid) for rid in order]
            elif operation=='review_take':
                b=self.find_block(p,payload['block_id']);t=next((t for t in b['takes'] if t['id']==payload['take_id']),None)
                if not t:raise StudioError('Дубль не найден.',404)
                state=payload.get('state','unreviewed')
                if state not in ['unreviewed','selected','accepted','rejected']:raise StudioError('Неизвестный статус отбора.')
                t['review_state']=state;t['notes']=str(payload.get('notes',t.get('notes','')))[:10000]
                if state in ['selected','accepted']:b['selected_take_id']=t['id']
                elif b['selected_take_id']==t['id']:b['selected_take_id']=None
            elif operation=='create_continuation':
                source=self.find_block(p,payload['block_id']);take=next((t for t in source['takes'] if t['id']==payload['take_id']),None)
                if not take or not take.get('native_av_asset_id') or not take.get('last_frame_asset_id'):raise StudioError('У дубля должны быть native AV и lossless последний кадр.')
                b=copy.deepcopy(source);b.update(id=uid('block-'),label=source['label']+'-C'+str(len(p['blocks'])+1),title='Продолжение · '+source['title'],takes=[],selected_take_id=None,status='draft',context_take_id=take['id'])
                b['settings']['mode']='context';b['settings']['seed']=str((int(b['settings']['seed'])+1)%(2**64));b['montage_seconds']=None
                first=next((g['reference_id'] for g in b['guides'] if g['frame']==0),None)
                ref=next((r for r in b['references'] if r['id']==first),b['references'][-1] if b['references'] else None)
                if ref is None:raise StudioError('Исходный блок не содержит референсов.')
                ref['asset_id']=take['last_frame_asset_id'];ref['role']='Фактический финал предыдущего дубля / начало продолжения'
                b['guides']=[{'reference_id':ref['id'],'frame':0}]
                b['notes']='Продолжение '+take['name']+'. Промпт перенесён как черновик: задайте следующее действие и проверьте роли остальных картинок.'
                p['blocks'].append(b);next(s for s in p['segments'] if s['id']==b['segment_id'])['block_ids'].append(b['id'])
            elif operation=='assign_catalog_image':
                from .library import assign_image
                if type(revision) is not int or p['revision']!=revision:
                    raise StudioError('Проект изменился. Откройте выбор изображения заново.',409)
                before=copy.deepcopy(p)
                assign_image(self,p,payload)
                if p==before:return p
            elif operation=='add_asset':
                aid=self.asset(p,payload['path'])
                if payload.get('block_id'):
                    b=self.find_block(p,payload['block_id'])
                    if p['assets'][aid]['kind']!='image':raise StudioError('В референсы можно добавить изображение.')
                    if len(b['references'])>=8:raise StudioError('Допустимо до восьми референсов.')
                    b['references'].append({'id':uid('ref-'),'asset_id':aid,'role':str(payload.get('role','Референс'))})
            elif operation=='restore_block':
                bid=payload['block_id'];history_path=self.production_dir(pid)/'History'/Path(payload['history_file']).name
                if not history_path.is_file():raise StudioError('Версия не найдена.',404)
                old=self.find_block(read(history_path),bid);b=self.find_block(p,bid)
                for key in ['title','notes','prompt','references','guides','settings','montage_seconds','context_take_id']:b[key]=copy.deepcopy(old[key])
            else:raise StudioError('Неизвестная операция.')
            return self.commit(p,revision,actor,operation)

    def context(self,pid,bid):
        p=self.get(pid);b=self.find_block(p,bid);s=next(s for s in p['segments'] if s['id']==b['segment_id'])
        return {'project':{k:p[k] for k in ['id','name','root','revision']},'segment':s,'block':b,'validation':self.validate(p,b),
                'assets':[{**p['assets'][r['asset_id']],'picture':i+1,'role':r['role'],'absolute_path':str(self.resolve_asset(p,r['asset_id']))} for i,r in enumerate(b['references'])],
                'neighbor_blocks':[x for x in p['blocks'] if x['id'] in s['block_ids'] and x['id']!=bid],
                'instructions':[str(self.paths.code_root/'AGENTS.md'),str(self.safe(self.book/'AGENTS.md',False)),str(self.amv/'AGENTS.md'),str(self.amv/'Plan.md'),str(self.safe(p['root'],False)/'Plan.md')],
                'editing_contract':'PATCH needs expected_revision; propose variants without overwriting, explicit prepare and explicit launch are separate actions.'}

    def history(self,pid):
        files=sorted((self.production_dir(pid)/'History').glob('*.json'),reverse=True)
        return [{'file':f.name,'revision':read(f)['revision'],'updated_at':read(f)['updated_at']} for f in files[:60]]

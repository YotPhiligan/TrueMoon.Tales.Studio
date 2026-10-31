"""Frame-based montage placements. No edits to generation inputs or media files."""
from __future__ import annotations
import copy,math,re,uuid
from . import clip_edit as ce

EDIT_FIELDS = ('speed', 'source_out_frame', 'anchor_frame', 'gap_protected')

def edit_fields(item): return {key: copy.deepcopy(item[key]) for key in EDIT_FIELDS if key in item}

def validate_edit_fields(item):
    if 'anchor_frame' in item and item['anchor_frame'] is not None: integer(item['anchor_frame'], 'Закреплённое начало')
    if 'gap_protected' in item and type(item['gap_protected']) is not bool: raise TimelineError('Защита паузы задаётся переключателем.')
    try: ce.speed(item)
    except ce.ClipEditError as error: raise TimelineError(str(error)) from error

MAX_FRAME=24*60*60*6
COLORS=['mint','blue','violet','amber','rose','teal']
HISTORY_LIMIT=30

class TimelineError(ValueError):pass

def identifier(prefix):return prefix+uuid.uuid4().hex[:16]

def empty(fps=24):
    return {'version':1,'fps':fps,'segments':[],'clips':[],'markers':[],'music':None,'clip_audio':False,'undo':[],'redo':[]}

def get(project):
    t=copy.deepcopy(project.get('timeline') or empty(project.get('fps',24)));t.setdefault('music',None);t.setdefault('clip_audio',False);return t

def integer(value,label,minimum=0):
    if type(value) is not int or not minimum<=value<=MAX_FRAME:raise TimelineError(label+' вне допустимого диапазона кадров.')
    return value

def text(value,label):
    if not isinstance(value,str) or not value.strip() or len(value)>160:raise TimelineError('Укажите '+label+' (до 160 символов).')
    return value.strip()

def find(rows,key,label):
    item=next((x for x in rows if x['id']==key),None)
    if not item:raise TimelineError(label+' не найден.')
    return item

def end(item):return item['start_frame']+item['duration_frames']

def capacity(take,fps):
    info=take.get('video_info') or {}
    if not isinstance(info,dict):raise TimelineError('Некорректные метаданные дубля.')
    seconds=info.get('seconds')
    if seconds is None:
        frames=info.get('frames');source_fps=info.get('fps')
        if type(frames) not in [int,float] or not math.isfinite(frames) or frames<=0 or type(source_fps) not in [int,float] or not math.isfinite(source_fps) or source_fps<=0:raise TimelineError('Неизвестна длина дубля. Проверьте метаданные видео.')
        seconds=frames/source_fps
    if type(seconds) not in [int,float] or not math.isfinite(seconds) or seconds<=0:raise TimelineError('Неизвестна длина дубля. Проверьте метаданные видео.')
    return math.floor(seconds*fps+1e-6)

def content(timeline):
    keys=['segments','clips','markers','music','clip_audio']+[key for key in ['scenario','sound_events','transitions','titles'] if key in timeline]
    return {k:copy.deepcopy(timeline.get(k,False if k=='clip_audio' else None)) for k in keys}

def validate(project,timeline):
    from .sound import validate as validate_sound
    from .transitions import validate as validate_transitions
    from .titles import validate as validate_titles
    fps=timeline.get('fps')
    if type(fps) is not int or not 1<=fps<=120 or fps!=project.get('fps',24):raise TimelineError('FPS монтажа должен совпадать с проектом.')
    for kind,limit in [('segments',200),('clips',2000),('markers',1000)]:
        rows=timeline.get(kind)
        if not isinstance(rows,list) or len(rows)>limit:raise TimelineError('Превышен предел элементов таймлайна.')
        ids=[]
        for item in rows:
            if not isinstance(item,dict) or not isinstance(item.get('id'),str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',item['id']):raise TimelineError('Некорректный ID элемента.')
            ids.append(item['id'])
        if len(set(ids))!=len(ids):raise TimelineError('ID элементов должны различаться.')
    for s in timeline['segments']:
        text(s.get('name'),'название сегмента');integer(s.get('start_frame'),'Начало сегмента');integer(s.get('duration_frames'),'Длина сегмента',1)
        if end(s)>MAX_FRAME:raise TimelineError('Сегмент выходит за пределы таймлайна.')
        if s.get('color') not in COLORS:raise TimelineError('Неизвестный цвет сегмента.')
    ordered=sorted(timeline['segments'],key=lambda x:x['start_frame'])
    for a,b in zip(ordered,ordered[1:]):
        if end(a)>b['start_frame']:raise TimelineError('Сегменты пересекаются. Выберите свободный интервал.')
    for c in timeline['clips']:
        integer(c.get('start_frame'),'Начало клипа');integer(c.get('duration_frames'),'Длина клипа',1);validate_edit_fields(c)
        if not any(k in c for k in ['speed','source_out_frame']):integer(c.get('source_in_frame'),'Вход в дубль')
        if c.get('anchor_frame') is not None and c['anchor_frame'] != c['start_frame']:raise TimelineError('Начало блока закреплено. Снимите закрепление перед переносом.')
        s=find(timeline['segments'],c.get('segment_id'),'Монтажный сегмент');b=find(project['blocks'],c.get('block_id'),'Исходный блок')
        if not s['start_frame']<=c['start_frame'] or end(c)>end(s):raise TimelineError('Клип должен помещаться в свой сегмент.')
        if c.get('take_id') is None:
            if c['source_in_frame']!=0 or c.get('speed',1)!=1 or 'source_out_frame' in c:raise TimelineError('У заглушки нет участка и скорости видео.')
        else:
            take=find(b['takes'],c['take_id'],'Дубль этого блока')
            try:ce.validate(c,capacity(take,fps))
            except ce.ClipEditError as error:raise TimelineError(str(error)) from error
    ordered=sorted(timeline['clips'],key=lambda x:x['start_frame'])
    for a,b in zip(ordered,ordered[1:]):
        if end(a)>b['start_frame']:raise TimelineError('Клипы пересекаются. Выберите свободное место.')
    for m in timeline['markers']:text(m.get('name'),'название маркера');integer(m.get('frame'),'Позиция маркера')
    validate_sound(project,timeline)
    validate_transitions(timeline)
    validate_titles(project,timeline)
    if type(timeline.get('clip_audio',False)) is not bool:raise TimelineError('Звук клипов задаётся переключателем.')
    music=timeline.get('music')
    if music is not None:
        if not isinstance(music,dict):raise TimelineError('Некорректная музыкальная дорожка.')
        aid=music.get('asset_id');asset=project['assets'].get(aid) if isinstance(aid,str) else None
        if not asset or asset['kind']!='audio':raise TimelineError('Музыка должна ссылаться на звуковой материал проекта.')
        for key in ['start_frame','source_in_frame']:integer(music.get(key),'Позиция музыки')
        integer(music.get('duration_frames'),'Длина музыки',1)
        if end(music)>MAX_FRAME:raise TimelineError('Музыка выходит за пределы таймлайна.')
        if music['source_in_frame']+music['duration_frames']>capacity({'video_info':asset.get('audio_info')},fps):raise TimelineError('Участок музыки выходит за длину звукового файла.')
        gain=music.get('gain')
        if type(gain) not in [int,float] or not math.isfinite(gain) or not 0<=gain<=1:raise TimelineError('Громкость музыки должна быть от 0 до 100%.')
        if type(music.get('muted')) is not bool:raise TimelineError('Выключение музыки задаётся переключателем.')

def apply(project,action,payload):
    t=get(project)
    if not isinstance(payload,dict):raise TimelineError('Ожидаются параметры операции таймлайна.')
    before=content(t)
    if action in ['undo','redo']:
        source=action;dest='redo' if action=='undo' else 'undo'
        if not t[source]:raise TimelineError('Нет действий для '+('отмены.' if action=='undo' else 'повтора.'))
        entry=t[source].pop();t[dest].append({'state':before,'action':entry['action']});t[dest]=t[dest][-HISTORY_LIMIT:]
        state=copy.deepcopy(entry['state']);state.setdefault('music',None);state.setdefault('clip_audio',False)
        if 'scenario' in t:state.setdefault('scenario',None)
        if 'sound_events' in t:state.setdefault('sound_events',[])
        if 'transitions' in t:state.setdefault('transitions',[])
        if 'titles' in t:state.setdefault('titles',[])
        t.update(state);validate(project,t);return t
    if action in ['add_title','update_title','remove_title']:
        from .titles import apply as apply_title
        apply_title(t,action,payload)
    elif action in ['set_transition','remove_transition']:
        from . import transitions
        getattr(transitions,action)(t,payload)
    elif action in ['add_sound','update_sound','remove_sound']:
        from .sound import apply as apply_sound
        apply_sound(t,action,payload)
    elif action=='set_music':
        t['music']={'id':identifier('music-'),'asset_id':payload.get('asset_id'),'start_frame':payload.get('start_frame'),
            'source_in_frame':payload.get('source_in_frame',0),'duration_frames':payload.get('duration_frames'),'gain':payload.get('gain',.8),'muted':payload.get('muted',False)}
    elif action=='update_music':
        if not t['music']:raise TimelineError('Музыка не выбрана.')
        for key in ['start_frame','source_in_frame','duration_frames','gain','muted']:
            if key in payload:t['music'][key]=payload[key]
    elif action=='remove_music':t['music']=None
    elif action=='set_clip_audio':t['clip_audio']=payload.get('enabled')
    elif action=='add_segment':
        t['segments'].append({'id':identifier('tseg-'),'name':text(payload.get('name'),'название сегмента'),
            'start_frame':integer(payload.get('start_frame'),'Начало сегмента'),'duration_frames':integer(payload.get('duration_frames'),'Длина сегмента',1),
            'color':payload.get('color',COLORS[len(t['segments'])%len(COLORS)])})
    elif action in ['update_segment','move_segment','resize_segment','edit_segment','remove_segment']:
        s=find(t['segments'],payload.get('id'),'Монтажный сегмент')
        if action=='remove_segment':
            t['segments'].remove(s);t['clips']=[c for c in t['clips'] if c['segment_id']!=s['id']]
            if 'sound_events' in t:t['sound_events']=[e for e in t['sound_events'] if e['segment_id']!=s['id']]
        elif action in ['move_segment','edit_segment']:
            target=integer(payload.get('start_frame'),'Начало сегмента');delta=target-s['start_frame'];s['start_frame']=target
            for c in t['clips']:
                if c['segment_id']==s['id']:
                    c['start_frame']+=delta
                    if c.get('anchor_frame') is not None:c['anchor_frame']+=delta
            if action=='edit_segment':
                for key in ['name','color','duration_frames']:
                    if key in payload:s[key]=payload[key]
        elif action=='resize_segment':
            for key in ['start_frame','duration_frames']:
                if key in payload:s[key]=payload[key]
        else:
            for key in ['name','color']:
                if key in payload:s[key]=payload[key]
    elif action=='add_clip':
        b=find(project['blocks'],payload.get('block_id'),'Исходный блок')
        t['clips'].append({'id':identifier('clip-'),'segment_id':payload.get('segment_id'),'block_id':b['id'],
            'take_id':payload.get('take_id'),'start_frame':payload.get('start_frame'),'duration_frames':payload.get('duration_frames'),'source_in_frame':payload.get('source_in_frame',0),**edit_fields(payload)})
    elif action in ['update_clip','remove_clip']:
        c=find(t['clips'],payload.get('id'),'Клип')
        if action=='remove_clip':t['clips'].remove(c)
        else:
            previous=copy.deepcopy(c)
            for key in ['segment_id','take_id','start_frame','duration_frames','source_in_frame',*EDIT_FIELDS]:
                if key in payload:c[key]=payload[key]
            try:ce.sync_interval(c,payload,previous)
            except ce.ClipEditError as error:raise TimelineError(str(error)) from error
    elif action=='add_marker':
        t['markers'].append({'id':identifier('mark-'),'name':text(payload.get('name'),'название маркера'),'frame':integer(payload.get('frame'),'Позиция маркера')})
    elif action in ['update_marker','remove_marker']:
        m=find(t['markers'],payload.get('id'),'Маркер')
        if action=='remove_marker':t['markers'].remove(m)
        else:
            for key in ['name','frame']:
                if key in payload:m[key]=payload[key]
    else:raise TimelineError('Неизвестная операция таймлайна.')
    from .transitions import prune
    prune(t);validate(project,t)
    if content(t)!=before:
        t['undo']=(t.get('undo',[])+[{'state':before,'action':action}])[-HISTORY_LIMIT:];t['redo']=[]
    return t

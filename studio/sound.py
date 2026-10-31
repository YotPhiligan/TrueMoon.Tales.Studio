"""Segment-relative sound events. Sources and native clip audio remain independent."""
import copy,math,re,subprocess
from . import timeline as tl

FIELDS=('segment_id','name','lane','asset_id','stream','offset_frames','source_in_frame','duration_frames','gain','muted')
LANES={'voice','sfx','ambience'}

def events(t):return t.get('sound_events',[])

def placed(t):
    segments={s['id']:s for s in t['segments']}
    return [{**e,'start_frame':segments[e['segment_id']]['start_frame']+e['offset_frames']} for e in events(t) if e['segment_id'] in segments]

def validate(project,t):
    rows=events(t)
    if not isinstance(rows,list) or len(rows)>64:raise tl.TimelineError('Допускается до 64 звуковых событий.')
    ids=set()
    for e in rows:
        if not isinstance(e,dict) or not isinstance(e.get('id'),str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',e['id']) or e['id'] in ids:raise tl.TimelineError('Некорректный ID звука.')
        ids.add(e['id']);tl.text(e.get('name'),'название звука')
        if not isinstance(e.get('lane'),str) or e['lane'] not in LANES:raise tl.TimelineError('Неизвестная звуковая дорожка.')
        s=tl.find(t['segments'],e.get('segment_id'),'Сегмент звука')
        aid=e.get('asset_id');a=project['assets'].get(aid) if isinstance(aid,str) else None
        if not a or a.get('kind') not in ['audio','video']:raise tl.TimelineError('Выберите аудио или видео для звука.')
        if type(e.get('stream')) is not int or not 0<=e['stream']<64:raise tl.TimelineError('Некорректный звуковой поток.')
        for key in ['offset_frames','source_in_frame']:tl.integer(e.get(key),'Позиция звука')
        tl.integer(e.get('duration_frames'),'Длина звука',1)
        seconds=e.get('source_seconds')
        if type(seconds) not in [int,float] or not math.isfinite(seconds) or seconds<=0:raise tl.TimelineError('Неизвестна длина звука.')
        if e['source_in_frame']+e['duration_frames']>math.floor(seconds*t['fps']+1e-6):raise tl.TimelineError('Участок выходит за длину звука.')
        if s['start_frame']+e['offset_frames']+e['duration_frames']>tl.MAX_FRAME:raise tl.TimelineError('Звук выходит за пределы таймлайна.')
        g=e.get('gain')
        if type(g) not in [int,float] or not math.isfinite(g) or not 0<=g<=1:raise tl.TimelineError('Громкость звука должна быть от 0 до 100%.')
        if type(e.get('muted')) is not bool:raise tl.TimelineError('Выключение звука задаётся переключателем.')

def apply(t,action,payload):
    rows=t.setdefault('sound_events',[])
    if action=='add_sound':
        e={'id':tl.identifier('sound-'),'lane':'sfx','stream':0,'offset_frames':0,'source_in_frame':0,'gain':1,'muted':False}
        e.update({k:copy.deepcopy(v) for k,v in payload.items() if k in (*FIELDS,'source_seconds')});rows.append(e)
    else:
        e=tl.find(rows,payload.get('id'),'Звуковое событие')
        if action=='remove_sound':rows.remove(e)
        else:e.update({k:copy.deepcopy(v) for k,v in payload.items() if k in (*FIELDS,'source_seconds')})

def streams(path):
    import av
    try:
        with av.open(str(path)) as c:
            return [{'index':i,'codec':s.codec_context.name,'channels':len(s.layout.channels),'sample_rate':s.rate,
                     'language':s.metadata.get('language',''),'title':s.metadata.get('title','')} for i,s in enumerate(c.streams.audio)]
    except Exception as error:raise tl.TimelineError('Не удалось прочитать звуковые потоки файла.') from error

def preview(path,target,stream,start,duration):
    import imageio_ffmpeg
    try:
        result=subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(),'-hide_banner','-loglevel','error','-nostdin','-threads','2',
            '-i',str(path),'-map',f'0:a:{stream}','-vn','-af',f'atrim=start={start:.12f}:duration={duration:.12f},asetpts=PTS-STARTPTS',
            '-ar','48000','-ac','2','-c:a','pcm_s16le','-n',str(target)],capture_output=True,timeout=45,
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    except subprocess.TimeoutExpired:raise tl.TimelineError('Подготовка звука остановлена по лимиту 45 секунд.')
    if result.returncode:raise tl.TimelineError('Не удалось подготовить выбранный звуковой участок.')

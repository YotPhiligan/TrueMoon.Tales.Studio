"""Immutable CPU draft plan using the same absolute montage clock as preview."""
import copy,math
from .model import StudioError
from . import scenario,timeline
from .clip_edit import speed,source_out
from .sound import placed
from .transitions import windows

MAX_SECONDS=600
TIMEOUT=300


def plan(store,pid,revision,profile='draft'):
    with store.lock:
        p=store.get(pid)
        if type(revision) is not int or revision!=p['revision']:raise StudioError('Монтаж изменился. Обновите проект перед экспортом.',409)
        if profile not in ['draft','project']:raise StudioError('Выберите черновой размер или размер проекта.',422)
        view=scenario.get(p)
        if view['issues']:raise StudioError('Исправьте неопределённое время и конфликты сценария перед экспортом.',422,view['issues'])
        t=view['timeline'] if view['active'] else timeline.get(p)
        try:timeline.validate(p,t)
        except timeline.TimelineError as e:raise StudioError(str(e),422) from e
        sounds=placed(t)
        count=max([0]+[timeline.end(s) for s in t['segments']]+[timeline.end(c) for c in t['clips']]+[timeline.end(e) for e in sounds]+[timeline.end(r) for r in t.get('titles',[])]+([timeline.end(t['music'])] if t.get('music') else []))
        if not count:raise StudioError('Таймлайн пуст.',422)
        if count>MAX_SECONDS*p['fps']:raise StudioError('Черновой экспорт поддерживает таймлайн до 10 минут.',422)
        size=p['output_size']
        if not isinstance(size,list) or len(size)!=2 or any(type(n) is not int or n<2 or n>4096 for n in size):raise StudioError('Некорректный размер проекта.',422)
        scale=min(1,896/max(size)) if profile=='draft' else 1
        size=[max(2,round(n*scale/2)*2) for n in size]
        assets={}
        def asset(aid,kind):
            a=p['assets'].get(aid)
            if not a or a['kind']!=kind:raise StudioError('Не найден материал экспорта.',422)
            path=store.resolve_asset(p,aid)
            assets[aid]={'id':aid,'path':str(path),'kind':kind}
            return aid
        clips=[]
        for c in sorted(t['clips'],key=lambda c:c['start_frame']):
            if not c['take_id']:raise StudioError('Назначьте дубль блоку перед экспортом: '+c.get('story_title',c['id']),422)
            b=store.find_block(p,c['block_id']);take=next(x for x in b['takes'] if x['id']==c['take_id'])
            clips.append({**copy.deepcopy(c),'asset_id':asset(take['video_asset_id'],'video'),'speed':speed(c),'source_out_frame':source_out(c)})
        music=copy.deepcopy(t.get('music'))
        if music:asset(music['asset_id'],'audio')
        for e in sounds:asset(e['asset_id'],p['assets'][e['asset_id']]['kind'])
        title_layers=[]
        for layer in store.render_title_layers(p,t.get('titles',[])):
            aid='title-layer-'+layer['id']
            assets[aid]={'id':aid,'path':layer['path'],'kind':'image','expected_sha256':layer['sha256']}
            title_layers.append({**{k:v for k,v in layer.items() if k not in ['path','url']},'asset_id':aid})
        return {'version':1,'project_id':pid,'project_name':p['name'],'project_revision':revision,'book':str(store.book),'path_policy':store.paths.export_policy(),
                'fps':p['fps'],'frames':count,'seconds':count/p['fps'],'size':size,'profile':profile,
                'clips':clips,'music':music,'clip_audio':t.get('clip_audio',False),'assets':assets,'timeout_seconds':TIMEOUT,**({'sounds':sounds} if sounds else {}),
                **({'transitions':copy.deepcopy(t['transitions']),'transition_windows':windows(t)} if t.get('transitions') else {}),
                **({'titles':copy.deepcopy(t['titles']),'title_layers':title_layers} if title_layers else {})}


def source_position(clip,frame,fps):
    return min((clip['source_in_frame']+(frame-clip['start_frame'])*clip['speed'])/fps,
               max(clip['source_in_frame']/fps,clip['source_out_frame']/fps-1e-7))


def parts(request):
    """A complete nonoverlapping partition, including black pauses and music tail."""
    cursor=0;result=[]
    for clip in request['clips']:
        if clip['start_frame']>cursor:result.append({'start_frame':cursor,'duration_frames':clip['start_frame']-cursor,'clip':None})
        result.append({'start_frame':clip['start_frame'],'duration_frames':clip['duration_frames'],'clip':clip});cursor=timeline.end(clip)
    if cursor<request['frames']:result.append({'start_frame':cursor,'duration_frames':request['frames']-cursor,'clip':None})
    return result

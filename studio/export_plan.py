"""Immutable CPU draft plan using the same absolute montage clock as preview."""
import copy
from .model import StudioError
from . import scenario,timeline
from .clip_edit import speed,source_out
from .sound import placed
from .transitions import windows

MAX_SECONDS=600
TIMEOUT=300
DRAFT_ENCODING = {
    'video_codec': 'libx264', 'pixel_format': 'yuv420p', 'preset': 'veryfast', 'crf': 25,
    'audio_codec': 'aac', 'audio_bitrate_kbps': 192,
}
FINAL_ENCODING = {**DRAFT_ENCODING, 'preset': 'medium', 'crf': 18, 'audio_bitrate_kbps': 320}


def inspect_montage(store, project):
    """Resolve the montage inputs without rendering titles or starting an export."""
    view = scenario.get(project)
    t = view['timeline'] if view['active'] else timeline.get(project)
    if not view['issues']:
        try:
            timeline.validate(project, t)
        except timeline.TimelineError as error:
            raise StudioError(str(error), 422) from error

    issues = []
    segments = {row['id']: row for row in view['segments']}
    for issue in view['issues']:
        segment = segments[issue['segment_id']]
        item = next((row for row in segment['items'] if row['id'] == issue['item_id']), None)
        frame = item['start_frame'] if item else segment['start_frame']
        issues.append({**issue, 'kind': 'scenario', 'frame': frame, 'location': segment['name']})

    sounds = placed(t)
    count = max([0] + [timeline.end(s) for s in t['segments']] +
                [timeline.end(c) for c in t['clips']] + [timeline.end(e) for e in sounds] +
                [timeline.end(r) for r in t.get('titles', [])] +
                ([timeline.end(t['music'])] if t.get('music') else []))
    if not count and not issues:
        issues.append({'code': 'empty_timeline', 'message': 'Таймлайн пуст.'})
    if count > MAX_SECONDS * project['fps']:
        issues.append({'code': 'duration_limit', 'message': 'Экспорт поддерживает таймлайн до 10 минут.'})
    size = project['output_size']
    if not isinstance(size, list) or len(size) != 2 or any(type(n) is not int or n < 2 or n > 4096 for n in size):
        issues.append({'code': 'invalid_size', 'message': 'Некорректный размер проекта.'})

    assets = {}
    asset_errors = {}

    def resolve(aid, kinds, target):
        key = (aid, kinds)
        if key not in asset_errors:
            asset = project['assets'].get(aid)
            if not asset or asset['kind'] not in kinds:
                asset_errors[key] = (422, 'Не найден материал экспорта.')
            elif aid not in assets:
                try:
                    path = store.resolve_asset(project, aid)
                    if not path.is_file():
                        raise StudioError('Материал должен быть файлом.', 422)
                    assets[aid] = {'id': aid, 'path': str(path), 'kind': asset['kind']}
                except (StudioError, OSError) as error:
                    name = asset.get('name') or aid
                    asset_errors[key] = (getattr(error, 'status', 422), f'Недоступен файл «{name}».')
        if key in asset_errors:
            status, message = asset_errors[key]
            issues.append({**target, 'code': 'unavailable_asset', 'status': status, 'message': message})

    blocks = {row['id']: row for row in project['blocks']}
    clip_assets = {}
    for segment in view['segments']:
        for item in segment['items']:
            target = {'kind': 'clip', 'id': item['id'], 'item_id': item['id'], 'segment_id': segment['id'],
                      'frame': item['start_frame'], 'location': f'{segment["name"]} / {item["title"]}'}
            if not item['take_id']:
                issues.append({**target, 'code': 'missing_take',
                               'message': f'Назначьте дубль блоку «{item["title"]}».'})
                continue
            block = blocks[item['block_id']]
            take = next(row for row in block['takes'] if row['id'] == item['take_id'])
            aid = take['video_asset_id']
            resolve(aid, ('video',), target)
            clip_assets[item['id']] = aid

    if music := t.get('music'):
        resolve(music['asset_id'], ('audio',), {'kind': 'music', 'id': music.get('id', 'music'),
                'frame': music['start_frame'], 'location': 'Музыка проекта'})
    for event in sounds:
        resolve(event['asset_id'], ('audio', 'video'), {'kind': 'sound', 'id': event['id'],
                'segment_id': event['segment_id'], 'item_id': event.get('clip_id'),
                'frame': event['start_frame'], 'location': event['name']})
    return t, sounds, count, assets, clip_assets, issues


def readiness(store, pid):
    project = store.get(pid)
    try:
        _, _, frames, _, _, issues = inspect_montage(store, project)
    except StudioError as error:
        frames = None
        issues = [{'code': 'invalid_timeline', 'message': str(error)}]
    return {'project_id': pid, 'project_revision': project['revision'], 'fps': project['fps'],
            'frames': frames, 'ready': not issues, 'issues': issues}


def plan(store,pid,revision,profile='draft'):
    with store.lock:
        p=store.get(pid)
        if type(revision) is not int or revision!=p['revision']:raise StudioError('Монтаж изменился. Обновите проект перед экспортом.',409)
        if profile not in ('draft','project','final'):raise StudioError('Выберите профиль экспорта.',422)
        t, sounds, count, assets, clip_assets, issues = inspect_montage(store, p)
        if issues:
            first = issues[0]
            raise StudioError(first['message'], first.get('status', 422), issues)
        size=p['output_size']
        scale=min(1,896/max(size)) if profile=='draft' else 1
        size=[max(2,round(n*scale/2)*2) for n in size]
        clips=[{**copy.deepcopy(c),'asset_id':clip_assets[c['id']],'speed':speed(c),'source_out_frame':source_out(c)}
               for c in sorted(t['clips'],key=lambda c:c['start_frame'])]
        music=copy.deepcopy(t.get('music'))
        title_layers=[]
        for layer in store.render_title_layers(p,t.get('titles',[])):
            aid='title-layer-'+layer['id']
            assets[aid]={'id':aid,'path':layer['path'],'kind':'image','expected_sha256':layer['sha256']}
            title_layers.append({**{k:v for k,v in layer.items() if k not in ['path','url']},'asset_id':aid})
        encoding = FINAL_ENCODING if profile == 'final' else DRAFT_ENCODING
        return {'version':1,'encoding':encoding.copy(),'project_id':pid,'project_name':p['name'],'project_revision':revision,'book':str(store.book),'path_policy':store.paths.export_policy(),
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

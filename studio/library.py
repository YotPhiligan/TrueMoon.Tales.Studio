"""Read-only discovery and reverse links for the project asset library."""
from collections import defaultdict
import copy
import os
from pathlib import Path, PureWindowsPath

from . import scenario, sound, timeline
from .model import AUDIO, IMAGES, MEDIA, VIDEOS, StudioError, timing, uid

FOLDERS = ('video', 'Frames', 'References', 'Audio')
KINDS = {suffix: kind for kind, extensions in [('video', VIDEOS), ('image', IMAGES), ('audio', AUDIO)]
         for suffix in extensions}


def preview_path(store, pid, name):
    return local_media(store, store.project_dir(pid), name)


def local_media(store, root, name):
    parts = name.split('/') if isinstance(name, str) else []
    if (len(parts) < 2 or parts[0] not in FOLDERS
            or any(not part or part in ('.', '..') or part.endswith((' ', '.'))
                   or any(char in part for char in '\\:\x00') or PureWindowsPath(part).is_reserved() for part in parts)):
        raise StudioError('Недопустимый путь предпросмотра.', 403)
    candidate = root.joinpath(*parts)
    if candidate.suffix.lower() not in MEDIA:
        raise StudioError('Предпросмотр доступен для видео, изображений и звука.', 415)
    path = store.safe(candidate)
    if path != candidate or not path.is_file():
        raise StudioError('Файл недоступен для предпросмотра.', 404)
    return path


def usage_index(project):
    uses = defaultdict(list)

    def add(aid, role, label, **target):
        if aid:
            uses[aid].append({'role': role, 'label': label, **target})

    takes = {}
    for block in project['blocks']:
        bid = block['id']
        label = block['label']
        guides = defaultdict(list)
        for guide in block['guides']:
            guides[guide['reference_id']].append(guide['frame'])
        for reference in block['references']:
            aid = reference['asset_id']
            frames = guides.get(reference['id'])
            if frames:
                add(aid, 'guide', label, block_id=bid, reference_id=reference['id'], frames=frames, detail=reference.get('role', ''))
            else:
                add(aid, 'reference', label, block_id=bid, reference_id=reference['id'], detail=reference.get('role', ''))
        for take in block['takes']:
            tid = take['id']
            takes[bid, tid] = take.get('video_asset_id')
            target = {'block_id': bid, 'take_id': tid, 'detail': take['name']}
            add(take.get('video_asset_id'), 'take', label, **target)
            add(take.get('last_frame_asset_id'), 'take_frame', label, **target)

    view = scenario.get(project)
    segments = {s['id']: s for s in view['segments']}
    placements = {}
    for segment in view['segments']:
        for item in segment['items']:
            placements[item['id']] = (segment, item)
            add(takes.get((item['block_id'], item['take_id'])), 'montage_video', item['title'],
                segment_id=segment['id'], item_id=item['id'], detail=segment['name'], start_frame=item['start_frame'])
            add(item.get('animatic_asset_id'), 'animatic_frame', item['title'],
                segment_id=segment['id'], item_id=item['id'], detail=segment['name'], start_frame=item['start_frame'])
    timeline = view['timeline']
    for event in timeline.get('sound_events', []):
        target = placements.get(event.get('clip_id'))
        if target:
            segment, item = target
            add(event['asset_id'], 'block_sound', item['title'], segment_id=segment['id'], item_id=item['id'],
                detail=event['name'])
        elif not event.get('clip_id') and event['segment_id'] in segments:
            segment = segments[event['segment_id']]
            add(event['asset_id'], 'segment_sound', segment['name'], segment_id=segment['id'], detail=event['name'])
    if music := timeline.get('music'):
        add(music['asset_id'], 'music', project['name'], start_frame=music['start_frame'])
    return uses


def physical_directory(path):
    try:
        return path.resolve(strict=True) == path and path.is_dir()
    except (OSError, RuntimeError):
        return False


def catalog(store, pid):
    project = store.get(pid)
    root = store.project_dir(pid)
    uses = usage_index(project)
    rows = []
    registered = set()
    for aid, asset in project['assets'].items():
        if asset.get('kind') not in ('video', 'image', 'audio'):
            continue
        location, path, available = 'unavailable', asset['path'], False
        try:
            file = store.resolve_asset(project, aid)
            if file.is_file() and file.suffix.lower() in MEDIA:
                registered.add(file)
                available = True
                if file.is_relative_to(root):
                    location, path = 'project', file.relative_to(root).as_posix()
                else:
                    location, path = 'external', store.relative(file)
        except (StudioError, OSError, ValueError):
            pass
        rows.append({'id': aid, 'asset_id': aid, 'name': asset['name'], 'kind': asset['kind'], 'path': path,
                     'location': location, 'available': available, 'uses': uses.get(aid, [])})

    for folder in FOLDERS:
        base = root / folder
        if not physical_directory(base):
            continue
        for directory, folders, filenames in os.walk(base):
            path = Path(directory)
            folders[:] = [name for name in folders if physical_directory(path / name)]
            for filename in filenames:
                file = path / filename
                kind = KINDS.get(file.suffix.lower())
                if not kind or file in registered or file.is_symlink():
                    continue
                relative = file.relative_to(root).as_posix()
                try:
                    local_media(store, root, relative)
                except (StudioError, OSError, ValueError):
                    continue
                rows.append({'id': 'file:' + relative, 'asset_id': None, 'name': file.name, 'kind': kind, 'path': relative,
                             'location': 'project', 'available': True, 'uses': []})
    rows.sort(key=lambda row: (row['name'].casefold(), row['path'].casefold(), row['id']))
    return {'project_revision': project['revision'], 'assets': rows}


def select(store, project, selection, kinds):
    if not isinstance(selection, dict) or set(selection) not in ({'file'}, {'asset_id'}):
        raise StudioError('Выберите один файл из библиотеки ассетов.', 422)
    if 'asset_id' in selection:
        aid = selection['asset_id']
        if not isinstance(aid, str):
            raise StudioError('Некорректный ID ассета.', 422)
        path = store.resolve_asset(project, aid)
        if project['assets'][aid]['kind'] not in kinds:
            raise StudioError('Этот тип материала здесь не поддерживается.', 422)
    else:
        path = preview_path(store, project['id'], selection['file'])
        aid = None
    if not path.is_file() or KINDS.get(path.suffix.lower()) not in kinds:
        raise StudioError('Этот тип материала здесь не поддерживается.', 422)
    return aid or store.asset(project, path), path


def assign_image(store, project, payload):
    block = store.find_block(project, payload.get('block_id'))
    aid, _ = select(store, project, payload.get('asset_selection'), ('image',))
    frame = payload.get('guide_frame')
    ref = next((ref for ref in block['references'] if ref['asset_id'] == aid), None)
    if not ref:
        ref = {'id': uid('ref-'), 'asset_id': aid, 'role': 'Опорный кадр' if frame is not None else 'Референс'}
        block['references'].append(ref)
    if frame is not None:
        settings = block['settings']
        count = timing(settings['seconds'], settings['mode'])['frames']
        if type(frame) is not int or not -1 <= frame < count:
            raise StudioError('Индекс опоры выходит за длину источника.', 422)
        position = count - 1 if frame == -1 else frame
        for guide in block['guides']:
            existing = count - 1 if guide['frame'] == -1 else guide['frame']
            if existing != position:
                continue
            if guide['reference_id'] == ref['id']:
                return
            raise StudioError('На этот кадр уже назначена опора.', 422)
        block['guides'].append({'reference_id': ref['id'], 'frame': frame})
    store.validate_shape(project, block)
    block['status'] = 'ready' if not store.validate(project, block)['errors'] else 'draft'


def add_video(store, project, payload):
    if not isinstance(payload, dict):
        raise StudioError('Ожидаются параметры монтажа видео.', 422)
    project = copy.deepcopy(project)
    aid, path = select(store, project, payload.get('asset_selection'), ('video',))
    stream = payload.get('audio_stream')
    if stream is not None and (type(stream) is not int or not 0 <= stream < 64):
        raise StudioError('Некорректный звуковой поток.', 422)
    uses = [(block, take) for block in project['blocks'] for take in block['takes'] if take.get('video_asset_id') == aid]
    imported = next(((block, take) for block, take in uses if take.get('imported')), None)
    source = imported or (uses[0] if len(uses) == 1 else None)
    fps = project['fps']
    info = source[1].get('video_info') if source else None
    try:
        timeline.capacity({'video_info': info}, fps)
    except timeline.TimelineError:
        info = store.probe_video(path)
    try:
        start = timeline.integer(payload.get('source_in_frame', 0), 'Вход в видео')
        length = timeline.integer(payload.get('duration_frames'), 'Длина видео', 1)
        if start + length > timeline.capacity({'video_info': info}, fps):
            raise StudioError('Выбранный участок выходит за длину видео.', 422)
    except timeline.TimelineError as error:
        raise StudioError(str(error), 422) from error

    if source:
        block, take = source
        take['video_info'] = info
    else:
        block, take = store.video_source(project, aid, path.name, info)

    sid = payload.get('segment_id')
    before = scenario.find(scenario.get(project)['segments'], sid)['items']
    result = scenario.apply(store, project, 'add_item', {
        **{key: payload[key] for key in ('title', 'description', 'gap_before_frames', 'index', *scenario.ITEM_NOTE_FIELDS) if key in payload},
        'segment_id': sid, 'block_id': block['id'], 'take_id': take['id'], 'source_in_frame': start,
        'duration_frames': length, 'mute_native_audio': True})
    if stream is None:
        return result

    montage = result['timeline']
    previous_ids = {item['id'] for item in before}
    clip = next((clip for clip in montage['clips'] if clip['segment_id'] == sid and clip['id'] not in previous_ids), None)
    if clip is None:
        raise StudioError('Для видео со звуком сначала задайте допустимое окно блока.', 422)
    wave = store.waveform_path(result['id'], path, stream)
    duration = min(length, timeline.capacity({'video_info': wave}, fps) - start)
    if duration < 1:
        raise StudioError('Звуковой поток заканчивается до входа в видео.', 422)
    try:
        sound.apply(montage, 'add_sound', {'segment_id': sid, 'clip_id': clip['id'], 'asset_id': aid,
                    'name': path.stem[:160], 'lane': 'sfx', 'stream': stream, 'source_in_frame': start,
                    'duration_frames': duration, 'source_seconds': wave['seconds']})
        timeline.validate(result, montage)
    except timeline.TimelineError as error:
        raise StudioError(str(error), 422) from error
    return result

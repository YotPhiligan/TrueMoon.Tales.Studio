"""Scenario intentions and sequential placements inside fixed segment windows."""
from __future__ import annotations
import copy
from .model import StudioError, uid
from . import timeline as tl
from . import clip_edit as ce


def number(value, label, minimum=0, optional=False):
    if optional and value is None: return None
    try: return tl.integer(value, label, minimum)
    except tl.TimelineError as exc: raise StudioError(str(exc), 422) from exc


def label(value, empty=False):
    if not isinstance(value, str) or len(value) > (12000 if empty else 160) or (not empty and not value.strip()):
        raise StudioError('Некорректное название или описание сценария.', 422)
    return value if empty else value.strip()


def find(rows, value):
    result = next((row for row in rows if row['id'] == value), None)
    if result is None: raise StudioError('Элемент сценария не найден.', 404)
    return result


def adopt(project, timeline=None):
    t = timeline or tl.get(project)
    result = {'version': 1, 'segments': []}
    for segment in t['segments']:
        row = {**copy.deepcopy(segment), 'description': '', 'items': []}
        cursor = segment['start_frame']
        for clip in sorted((c for c in t['clips'] if c['segment_id'] == segment['id']), key=lambda c: (c['start_frame'], c['id'])):
            block = find(project['blocks'], clip['block_id'])
            row['items'].append({'id': clip['id'], 'block_id': clip['block_id'], 'title': clip.get('story_title') or block['title'] or block['label'],
                'description': '', 'duration_frames': clip['duration_frames'], 'gap_before_frames': clip['start_frame'] - cursor,
                'take_id': clip['take_id'], 'source_in_frame': clip['source_in_frame'], **tl.edit_fields(clip)})
            cursor = tl.end(clip)
        result['segments'].append(row)
    return result


def validate(project, story):
    if not isinstance(story, dict) or story.get('version') != 1 or not isinstance(story.get('segments'), list) or len(story['segments']) > 200:
        raise StudioError('Некорректная структура сценария.', 422)
    ids = []
    count = 0
    for segment in story['segments']:
        if not isinstance(segment,dict) or not {'id','name','description','start_frame','duration_frames','color','items'}<=segment.keys():raise StudioError('Некорректные поля сегмента.',422)
        label(segment['name']); label(segment['description'], True)
        number(segment['start_frame'], 'Начало', optional=True); number(segment['duration_frames'], 'Длина', 1, True)
        if segment['color'] not in tl.COLORS or not isinstance(segment['items'], list): raise StudioError('Некорректный сегмент.', 422)
        ids.append(segment['id'])
        for item in segment['items']:
            if not isinstance(item,dict) or not {'id','block_id','title','description','duration_frames','gap_before_frames','take_id','source_in_frame'}<=item.keys():raise StudioError('Некорректные поля блока.',422)
            count += 1; ids.append(item['id']); label(item['title']); label(item['description'], True)
            number(item['duration_frames'], 'Длина блока', 1, True); number(item['gap_before_frames'], 'Пауза')
            try:
                tl.validate_edit_fields(item)
                if any(k in item for k in ['speed','source_out_frame']):ce.source_frame(item['source_in_frame'],'Вход в дубль')
                else:number(item['source_in_frame'], 'Вход в дубль')
                if 'source_out_frame' in item:ce.source_frame(item['source_out_frame'],'Выход из дубля')
            except (tl.TimelineError,ce.ClipEditError) as error:raise StudioError(str(error),422) from error
            block = find(project['blocks'], item['block_id'])
            if item['take_id'] is not None: find(block['takes'], item['take_id'])
            elif item['source_in_frame'] != 0 or item.get('speed',1)!=1 or 'source_out_frame' in item: raise StudioError('У заглушки нет участка и скорости видео.', 422)
    if count > 2000 or any(not isinstance(i, str) or not i or len(i) > 80 for i in ids) or len(set(ids)) != len(ids):
        raise StudioError('Превышен размер сценария или повторяются ID.', 422)


def get(project):
    t = tl.get(project)
    story = copy.deepcopy(t.get('scenario') or adopt(project, t))
    validate(project, story)
    issues = []; rows = []; projected = {**t, 'segments': [], 'clips': []}
    def issue(code, sid, message, iid=None):
        issues.append({'code': code, 'segment_id': sid, 'item_id': iid, 'message': message})
    for segment in story['segments']:
        sid = segment['id']; start = segment['start_frame']; length = segment['duration_frames']; cursor = start
        used = 0; unknown = False; items = []
        if start is None or length is None: issue('unplaced_segment', sid, 'Не задано окно сегмента.')
        if start is not None and length is not None:
            if start + length > tl.MAX_FRAME: issue('range', sid, 'Конец сегмента превышает предел таймлайна.')
            projected['segments'].append({k: segment[k] for k in ['id', 'name', 'start_frame', 'duration_frames', 'color']})
        for item in segment['items']:
            duration = item['duration_frames']; used += item['gap_before_frames']
            if cursor is not None: cursor += item['gap_before_frames']
            if item.get('anchor_frame') is not None:
                anchor=item['anchor_frame']
                if start is not None and anchor<start:issue('anchor_range',sid,'Закреплённое начало раньше сегмента.',item['id'])
                if cursor is not None:
                    if cursor>anchor:issue('anchor_overlap',sid,'Не хватает места перед закреплённым блоком «'+item['title']+'».',item['id'])
                    else:used+=anchor-cursor;cursor=anchor
                else:cursor=anchor
            items.append({**item, 'start_frame': cursor, 'end_frame': cursor + duration if cursor is not None and duration is not None else None})
            if duration is None:
                unknown = True; cursor = None; issue('unknown_duration', sid, 'Не задана длительность блока «' + item['title'] + '».', item['id'])
            else:
                used += duration
                if cursor is not None and length is not None:
                    projected['clips'].append({'id': item['id'], 'segment_id': sid, 'block_id': item['block_id'], 'take_id': item['take_id'],
                        'source_in_frame': item['source_in_frame'], 'start_frame': cursor, 'duration_frames': duration, 'story_title': item['title'], **tl.edit_fields(item)})
                if item['take_id']:
                    take = find(find(project['blocks'], item['block_id'])['takes'], item['take_id'])
                    try:
                        try:ce.validate(item,tl.capacity(take,project['fps']))
                        except ce.ClipEditError as error:issue('source_short',sid,str(error),item['id'])
                    except tl.TimelineError: issue('source_metadata', sid, 'Неизвестна длина выбранного дубля.', item['id'])
                if cursor is not None: cursor += duration
        remaining = length - used if length is not None and not unknown else None
        if remaining is not None and remaining < 0: issue('overflow', sid, 'Не хватает ' + f'{-remaining/project["fps"]:.3f}' + ' с в окне сегмента.')
        rows.append({**segment, 'items': items, 'used_frames': used if not unknown else None, 'remaining_frames': remaining})
    ordered = sorted(projected['segments'], key=lambda s: s['start_frame'])
    for a, b in zip(ordered, ordered[1:]):
        if tl.end(a) > b['start_frame']: issue('segment_overlap', b['id'], 'Окна сегментов пересекаются.')
    return {'version': 1, 'segments': rows, 'issues': issues, 'timeline': projected, 'active': bool(t.get('scenario'))}


def record(t, before, action):
    if tl.content(t) != before:
        t['undo'] = (t.get('undo', []) + [{'state': before, 'action': action}])[-tl.HISTORY_LIMIT:]; t['redo'] = []


def apply(store, project, action, payload):
    if not isinstance(payload, dict): raise StudioError('Ожидаются параметры сценария.', 422)
    p = copy.deepcopy(project); t = tl.get(p); before = tl.content(t)
    story = copy.deepcopy(t.get('scenario') or adopt(p, t)); segments = story['segments']
    if action == 'add_segment':
        segments.append({'id': uid('tseg-'), 'name': label(payload.get('name')), 'description': label(payload.get('description', ''), True),
                         'start_frame': payload.get('start_frame'), 'duration_frames': payload.get('duration_frames'),
                         'color': payload.get('color', tl.COLORS[len(segments) % len(tl.COLORS)]), 'items': []})
    elif action == 'reorder_segments':
        story['segments'] = reorder(segments, payload.get('order'))
    elif action in ['update_segment', 'remove_segment']:
        s = find(segments, payload.get('id'))
        if action == 'remove_segment':
            segments.remove(s)
            if 'sound_events' in t:t['sound_events']=[e for e in t['sound_events'] if e['segment_id']!=s['id']]
        else:
            if 'start_frame' in payload and s['start_frame'] is not None and payload['start_frame'] is not None:
                delta=number(payload['start_frame'],'Начало')-s['start_frame']
                for i in s['items']:
                    if i.get('anchor_frame') is not None:i['anchor_frame']+=delta
            for key in ['name', 'description', 'start_frame', 'duration_frames', 'color']:
                if key in payload: s[key] = payload[key]
    elif action in ['add_item', 'update_item', 'remove_item', 'reorder_items']:
        s = find(segments, payload.get('segment_id'))
        if action == 'reorder_items': s['items'] = reorder(s['items'], payload.get('order'))
        elif action == 'add_item':
            block = find(p['blocks'], payload['block_id']) if payload.get('block_id') else None
            if block is None:
                group = next((g for g in p['segments'] if g.get('scenario_drafts')), None)
                if group is None:
                    group = {'id': uid('seg-'), 'name': 'Сценарные заготовки', 'notes': '', 'block_ids': [], 'scenario_drafts': True}; p['segments'].append(group)
                block = store.new_block(group['id'], 'SB-' + str(len(p['blocks']) + 1).zfill(3)); block['title'] = label(payload.get('title'))
                p['blocks'].append(block); group['block_ids'].append(block['id'])
            item = {'id': uid('clip-'), 'block_id': block['id'], 'title': payload.get('title') or block['title'] or block['label'],
                    'description': payload.get('description', ''), 'duration_frames': payload.get('duration_frames'),
                    'gap_before_frames': payload.get('gap_before_frames', 0), 'take_id': payload.get('take_id'), 'source_in_frame': payload.get('source_in_frame', 0), **tl.edit_fields(payload)}
            index = payload.get('index', len(s['items']))
            if type(index) is not int or not 0 <= index <= len(s['items']): raise StudioError('Некорректная позиция блока.', 422)
            s['items'].insert(index, item)
        else:
            item = find(s['items'], payload.get('id'))
            if action == 'remove_item': s['items'].remove(item)
            else:
                previous=copy.deepcopy(item)
                for key in ['title', 'description', 'duration_frames', 'gap_before_frames', 'take_id', 'source_in_frame',*tl.EDIT_FIELDS]:
                    if key in payload: item[key] = payload[key]
                if not item['take_id']: item['source_in_frame'] = 0
                try:ce.sync_interval(item,payload,previous)
                except ce.ClipEditError as error:raise StudioError(str(error),422) from error
    elif action in ['edit_clip_source','slip_clip','split_clip','set_clip_anchor','set_gap_protected','compact_segment']:
        edit_montage(store,p,story,action,payload)
    else: raise StudioError('Неизвестная операция сценария.', 422)
    validate(p, story)
    # Probe only on explicit take assignment, never on a read or a text-only edit.
    if action in ['add_item', 'update_item'] and payload.get('take_id'):
        take = find(find(p['blocks'], item['block_id'])['takes'], item['take_id'])
        try: tl.capacity(take, p['fps'])
        except tl.TimelineError: take['video_info'] = store.probe_video(store.resolve_asset(p, take['video_asset_id']))
    t['scenario'] = story; p['timeline'] = t; view = get(p)
    if not view['issues']:
        t['segments'] = view['timeline']['segments']; t['clips'] = view['timeline']['clips']
        from .transitions import prune
        prune(t)
        try:tl.validate(p,t)
        except tl.TimelineError as error:raise StudioError(str(error),422) from error
    record(t, before, 'scenario:' + action)
    return p


def reorder(rows, order):
    if not isinstance(order, list) or len(order) != len(rows) or any(not isinstance(i, str) for i in order) or set(order) != {r['id'] for r in rows}:
        raise StudioError('Перестановка должна содержать все ID ровно один раз.', 422)
    return [find(rows, value) for value in order]


def edit_montage(store, project, story, action, payload):
    view=get({**project,'timeline':{**tl.get(project),'scenario':story}})
    if action=='compact_segment':
        s=find(story['segments'],payload.get('segment_id',payload.get('id')))
        if s['start_frame'] is None or s['duration_frames'] is None or any(i['duration_frames'] is None for i in s['items']):
            raise StudioError('Перед удалением промежутков задайте окно и длины блоков.',422)
        for item in s['items'][1:]:
            if not item.get('gap_protected'):item['gap_before_frames']=0
        return
    s=next((s for s in story['segments'] if any(i['id']==payload.get('id') for i in s['items'])),None)
    if s is None:raise StudioError('Монтажный блок не найден.',404)
    item=find(s['items'],payload['id']);index=s['items'].index(item)
    shown=find(find(view['segments'],s['id'])['items'],item['id'])
    if shown['start_frame'] is None or item['duration_frames'] is None:raise StudioError('Сначала задайте окно и длины блоков.',422)
    try:
        if action in ['edit_clip_source','slip_clip']:
            if not item['take_id']:raise ce.ClipEditError('Сначала назначьте видео.')
            block=find(project['blocks'],item['block_id']);take=find(block['takes'],item['take_id'])
            try:cap=tl.capacity(take,project['fps'])
            except tl.TimelineError:
                take['video_info']=store.probe_video(store.resolve_asset(project,take['video_asset_id']));cap=tl.capacity(take,project['fps'])
            start=payload.get('source_in_frame',item['source_in_frame']);end=payload.get('source_out_frame',ce.source_out(item));rate=payload.get('speed',ce.speed(item))
            if action=='slip_clip':
                delta=payload.get('delta_frames')
                if type(delta) not in [int,float] or not -tl.MAX_FRAME<=delta<=tl.MAX_FRAME:raise ce.ClipEditError('Укажите допустимый сдвиг источника в кадрах.')
                start=item['source_in_frame']+delta;end=ce.source_out(item)+delta;rate=ce.speed(item)
            item.update(ce.source_patch(item,start,end,rate,cap))
        elif action=='split_clip':
            left,right=ce.split(shown,payload.get('frame'));copy_item=copy.deepcopy(item)
            item.update(left);copy_item.update(right,id=uid('clip-'),title=item['title'][:156]+' · 2',gap_before_frames=0)
            copy_item.pop('anchor_frame',None);copy_item.pop('gap_protected',None)
            s['items'].insert(index+1,copy_item)
        elif action in ['set_clip_anchor','set_gap_protected']:
            enabled=payload.get('enabled')
            if type(enabled) is not bool:raise ce.ClipEditError('Укажите состояние переключателя.')
            previous=find(view['segments'],s['id'])['items'][index-1]['end_frame'] if index else s['start_frame']
            if previous is None:raise ce.ClipEditError('Сначала задайте времена предыдущих блоков.')
            gap=max(0,shown['start_frame']-previous)
            if action=='set_clip_anchor':
                if enabled:
                    item['anchor_frame']=shown['start_frame']
                    if not item.get('gap_protected'):item['gap_before_frames']=0
                else:item.pop('anchor_frame',None);item['gap_before_frames']=gap
            else:
                item['gap_protected']=enabled
                if enabled:item['gap_before_frames']=gap
                elif item.get('anchor_frame') is not None:item['gap_before_frames']=0
    except (ce.ClipEditError,tl.TimelineError) as error:raise StudioError(str(error),422) from error


def translate(store, project, action, payload):
    """Route spatial edits through the same scenario intentions, preserving ripple."""
    if not isinstance(payload,dict):raise StudioError('Ожидаются параметры таймлайна.',422)
    if action in ['edit_clip_source','slip_clip','split_clip','set_clip_anchor','set_gap_protected','compact_segment']:
        return apply(store,project,action,payload)
    if not tl.get(project).get('scenario'): return None
    view = get(project); segments = view['segments']
    if action == 'add_segment': return apply(store, project, 'add_segment', payload)
    if action in ['move_segment', 'resize_segment', 'update_segment', 'edit_segment', 'remove_segment']:
        return apply(store, project, 'remove_segment' if action == 'remove_segment' else 'update_segment', payload)
    if action == 'add_clip':
        s = find(segments, payload.get('segment_id')); start = number(payload.get('start_frame'),'Начало блока')
        index = sum(1 for i in s['items'] if i['start_frame'] is not None and i['start_frame'] < start)
        previous=s['items'][index-1]['end_frame'] if index else s['start_frame']
        if previous is None:raise StudioError('Сначала задайте времена предшествующих блоков.',422)
        return apply(store, project, 'add_item', {**payload, 'index': index,'gap_before_frames':max(0,start-previous)})
    if action in ['update_clip', 'remove_clip']:
        s = next((s for s in segments if any(i['id'] == payload.get('id') for i in s['items'])), None)
        if s is None: raise StudioError('Сценарный блок не найден.', 404)
        item = find(s['items'], payload['id'])
        if payload.get('segment_id', s['id']) != s['id']: raise StudioError('Перенос между сегментами: добавьте использование источника в нужной карточке.', 422)
        if action == 'remove_clip': return apply(store, project, 'remove_item', {'segment_id': s['id'], 'id': item['id']})
        patch = {**payload, 'segment_id': s['id']}
        start = payload.get('start_frame', item['start_frame'])
        if start != item['start_frame']:
            if item.get('anchor_frame') is not None:raise StudioError('Начало блока закреплено. Снимите закрепление перед переносом.',422)
            number(start, 'Начало блока')
            if payload.get('source_in_frame', item['source_in_frame']) != item['source_in_frame']:
                if item['start_frame'] is None:raise StudioError('Сначала задайте окно и длительности.',422)
                patch['gap_before_frames'] = max(0, item['gap_before_frames'] + start - item['start_frame'])
            else:
                order = [i['id'] for i in s['items'] if i['id'] != item['id']]
                index = sum(1 for i in s['items'] if i['id'] != item['id'] and i['start_frame'] is not None and i['start_frame'] < start)
                order.insert(index, item['id'])
                intermediate = apply(store, project, 'reorder_items', {'segment_id': s['id'], 'order': order})
                # One user operation, one undo entry.
                result = apply(store, intermediate, 'update_item', patch)
                result['timeline']['undo'] = copy.deepcopy(tl.get(project)['undo']); record(result['timeline'], tl.content(tl.get(project)), 'scenario:update_clip')
                return result
        return apply(store, project, 'update_item', patch)
    return None

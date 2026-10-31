"""Black transitions inside fixed clip windows. Audio and montage positions never move."""
from . import timeline as tl

FIELDS=('left_clip_id','right_clip_id','fade_out_frames','pause_frames','fade_in_frames')

def joins(t):
    clips=sorted(t['clips'],key=lambda c:(c['start_frame'],c['id']))
    return [{'left':a,'right':b,'gap_frames':b['start_frame']-tl.end(a)} for a,b in zip(clips,clips[1:])]

def windows(t):
    pairs={(p['left']['id'],p['right']['id']):p for p in joins(t)};result=[]
    for row in t.get('transitions',[]):
        pair=pairs.get((row['left_clip_id'],row['right_clip_id']))
        if not pair:continue
        start=pair['right']['start_frame'];black=start-row['pause_frames']
        result.append({**row,'gap_frames':pair['gap_frames'],'out_start_frame':black-row['fade_out_frames'],
                       'black_start_frame':black,'right_start_frame':start,'in_end_frame':start+row['fade_in_frames']})
    return sorted(result,key=lambda w:w['out_start_frame'])

def brightness(frame,compiled):
    for w in compiled:
        if w['out_start_frame']<=frame<w['black_start_frame']:
            return (w['black_start_frame']-frame-.5)/w['fade_out_frames']
        if w['black_start_frame']<=frame<w['right_start_frame']:return 0
        if w['right_start_frame']<=frame<w['in_end_frame']:
            return (frame-w['right_start_frame']+.5)/w['fade_in_frames']
    return 1

def validate(t):
    rows=t.get('transitions',[])
    if not isinstance(rows,list) or len(rows)>2000:raise tl.TimelineError('Некорректный список переходов.')
    pairs={(p['left']['id'],p['right']['id']):p for p in joins(t)};seen=set()
    for row in rows:
        if not isinstance(row,dict) or not set(FIELDS)<=row.keys():raise tl.TimelineError('Некорректные поля перехода.')
        if any(not isinstance(row[k],str) for k in FIELDS[:2]):raise tl.TimelineError('Некорректный стык перехода.')
        pair=pairs.get((row['left_clip_id'],row['right_clip_id']))
        if not pair:raise tl.TimelineError('Переход должен соединять соседние блоки.')
        if row['right_clip_id'] in seen:raise tl.TimelineError('На одном стыке может быть только один переход.')
        seen.add(row['right_clip_id'])
        for key in FIELDS[2:]:tl.integer(row[key],'Длительность перехода')
        if row['pause_frames']<pair['gap_frames']:raise tl.TimelineError('Пауза короче существующего промежутка. Измените размещение блоков отдельно.')
        if row['fade_out_frames']+row['pause_frames']-pair['gap_frames']>pair['left']['duration_frames']:
            raise tl.TimelineError('Затемнение и пауза не помещаются в предыдущий блок.')
        if row['fade_in_frames']>pair['right']['duration_frames']:raise tl.TimelineError('Появление не помещается в следующий блок.')
    compiled=windows(t);incoming={w['right_clip_id']:w['in_end_frame'] for w in compiled};outgoing={w['left_clip_id']:w['out_start_frame'] for w in compiled}
    for cid in incoming.keys()&outgoing.keys():
        if incoming[cid]>outgoing[cid]:raise tl.TimelineError('Появление и следующий переход перекрываются в одном блоке.')

def prune(t):
    if 'transitions' not in t:return
    if not isinstance(t['transitions'],list):return
    pairs={(p['left']['id'],p['right']['id']) for p in joins(t)}
    def keep(row):
        if not isinstance(row,dict) or any(not isinstance(row.get(k),str) for k in FIELDS[:2]):return True
        return (row['left_clip_id'],row['right_clip_id']) in pairs
    t['transitions']=[row for row in t['transitions'] if keep(row)]

def set_transition(t,payload):
    row={key:payload.get(key,0 if key in FIELDS[2:] else None) for key in FIELDS}
    rows=[r for r in t.get('transitions',[]) if r['right_clip_id']!=row['right_clip_id']]+[row]
    validate({**t,'transitions':rows})
    if not any(row[k] for k in FIELDS[2:]):rows=rows[:-1]
    if rows or 'transitions' in t:t['transitions']=rows

def remove_transition(t,payload):
    right=payload.get('right_clip_id')
    if not isinstance(right,str) or not any(r['right_clip_id']==right for r in t.get('transitions',[])):raise tl.TimelineError('Переход не найден.')
    t['transitions']=[r for r in t['transitions'] if r['right_clip_id']!=right]

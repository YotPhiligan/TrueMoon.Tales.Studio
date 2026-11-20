"""Source intervals and constant rate, independent of generated media."""
import math

MAX_FRAME = 518400


class ClipEditError(ValueError): pass


def source_frame(value, label='Кадр источника'):
    if type(value) not in (int, float) or not math.isfinite(value) or not 0 <= value <= MAX_FRAME:
        raise ClipEditError(label + ' вне допустимого диапазона.')
    return value


def speed(item):
    value = item.get('speed', 1)
    if type(value) not in (int, float) or not math.isfinite(value) or not .25 <= value <= 4:
        raise ClipEditError('Скорость должна быть от 0,25× до 4×.')
    return value


def source_out(item):
    return item.get('source_out_frame', item['source_in_frame'] + item['duration_frames'] * speed(item))


def duration(span, rate):
    return math.ceil(span / rate - 1e-8)


def validate(item, capacity):
    rate = speed(item)
    start = source_frame(item['source_in_frame'], 'Вход в дубль')
    end = source_frame(source_out(item), 'Выход из дубля')
    if end <= start or end > capacity + 1e-7:
        raise ClipEditError('Выбранный участок выходит за длину дубля.')
    if duration(end - start, rate) != item['duration_frames']:
        raise ClipEditError('Длина клипа не согласована с участком и скоростью.')


def source_patch(item, start, end, rate, capacity):
    if not item.get('take_id'): raise ClipEditError('Сначала назначьте видео: у заглушки нет участка источника.')
    start = source_frame(start, 'Вход в дубль'); end = source_frame(end, 'Выход из дубля')
    n = duration(end - start, speed({'speed': rate}))
    if not 1 <= n <= MAX_FRAME: raise ClipEditError('Участок должен занимать хотя бы один кадр монтажа.')
    patch = {'source_in_frame': start, 'source_out_frame': end, 'speed': rate, 'duration_frames': n}
    validate({**item, **patch}, capacity)
    return patch


def split(item, frame):
    if type(frame) is not int:raise ClipEditError('Укажите кадр разреза на сетке монтажа.')
    delta = frame - item['start_frame']
    if type(frame) is not int or not 0 < delta < item['duration_frames']:
        raise ClipEditError('Курсор должен находиться внутри блока, между его границами.')
    left = {'duration_frames': delta}; right = {'duration_frames': item['duration_frames'] - delta, 'source_in_frame': 0}
    if item.get('take_id'):
        cut = item['source_in_frame'] + delta * speed(item)
        right['source_in_frame'] = cut
        if 'speed' in item or 'source_out_frame' in item:
            left.update(speed=speed(item), source_out_frame=cut)
            right.update(speed=speed(item), source_out_frame=source_out(item))
    return left, right


def sync_interval(item, payload, previous=None):
    if not item.get('take_id'):
        item.pop('speed', None); item.pop('source_out_frame', None)
    elif 'source_out_frame' not in payload and previous is not None and ('source_out_frame' in item or 'speed' in item):
        if item['duration_frames'] is None:return
        if all(item.get(k)==previous.get(k) for k in ['source_in_frame','duration_frames','speed']):return
        if item['duration_frames'] != previous['duration_frames']:
            item['source_out_frame'] = item['source_in_frame'] + item['duration_frames'] * speed(item)
        else:
            item['source_out_frame'] = source_out(previous) + item['source_in_frame'] - previous['source_in_frame']
            if speed(item) != speed(previous):item['duration_frames']=duration(item['source_out_frame']-item['source_in_frame'],speed(item))

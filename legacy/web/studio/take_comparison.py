"""Read immutable generation inputs for the two-take review."""
from pathlib import Path, PureWindowsPath
import hashlib
import os
import re

from .model import StudioError, read


def _object(store, path):
    try:
        data = read(store.safe(path))
        return data if isinstance(data, dict) else {}
    except (StudioError, OSError, ValueError):
        return {}


def _path_key(path):
    return os.path.normcase(os.path.normpath(str(path)))


def _references(store, config_file, config, build):
    rows = config.get('references')
    if not isinstance(rows, list):
        return None
    source_rows = build.get('source_input_hashes')
    source_rows = source_rows if isinstance(source_rows, list) else []
    hashes = {
        _path_key(row['Source']): row['SHA256'] for row in source_rows
        if isinstance(row, dict) and isinstance(row.get('Source'), str)
        and isinstance(row.get('SHA256'), str) and re.fullmatch(r'[0-9a-fA-F]{64}', row['SHA256'])
    }
    guides = config.get('guides')
    guides = guides if isinstance(guides, list) else []
    result = []
    for index, row in enumerate(rows, 1):
        if not isinstance(row, dict) or not isinstance(row.get('path'), str) or not row['path']:
            return None
        raw = row['path']
        source = raw if Path(raw).is_absolute() or raw.startswith('@') else config_file.parent / raw
        digest = hashes.get(_path_key(source))
        try:
            key = _path_key(store.safe(source, False))
            digest = digest or hashes.get(key)
        except StudioError:
            key = _path_key(source)
        result.append({
            'file': PureWindowsPath(raw).name,
            'role': row.get('role') if isinstance(row.get('role'), str) else None,
            'source_key': hashlib.sha256(key.encode('utf-8')).hexdigest(),
            'sha256': digest.lower() if digest else None,
            'guides': [guide['frame'] for guide in guides if isinstance(guide, dict)
                       and guide.get('picture') == index and type(guide.get('frame')) is int],
        })
    return result


def describe(store, jobs, pid, take_id):
    project = store.get(pid)
    take = store.find_take(project, take_id)
    if take is None:
        raise StudioError('Дубль не найден.', 404)
    video = take.get('video_info')
    video = video if isinstance(video, dict) else {}
    if not all(video.get(k) for k in ('fps', 'seconds')):
        try:
            video = store.probe_video(store.resolve_asset(project, take['video_asset_id']))
        except StudioError:
            pass
    # A missing archive is common for imported MP4. Current block settings
    # may have changed and cannot fill in its historical inputs.
    config, build = {}, {}
    config_file = None
    try:
        if take.get('job_id'):
            folder = jobs.directory(pid, take['job_id'])
            config_file = folder / 'Input' / 'Config.json'
            build = _object(store, folder / 'Run' / 'Build.json') or _object(store, folder / 'Preview' / 'Build.json')
        elif take.get('job_path'):
            folder = store.safe(take['job_path'], False)
            config_file = folder / 'Job.json'
            build = _object(store, folder / 'Build.json')
        if config_file is not None:
            config = _object(store, config_file)
        if not config and take.get('job_id') and config_file is not None:
            config_file = folder / 'Run' / 'Job.json'
            config = _object(store, config_file)
    except (StudioError, OSError, ValueError):
        pass

    seed = config.get('seed')
    return {
        'video': {k: video[k] for k in ('seconds', 'fps', 'frames') if k in video},
        'snapshot': bool(config),
        'seed': str(seed) if type(seed) in (int, str) else None,
        'requested_seconds': config.get('seconds'),
        'profile': config.get('profile') if isinstance(config.get('profile'), str) else None,
        'models': build.get('models') if isinstance(build.get('models'), str) else None,
        'references': _references(store, config_file, config, build) if config else None,
    }

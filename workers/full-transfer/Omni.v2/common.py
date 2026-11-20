"""File contracts for the candidate Draft / Full Transfer profiles; no queue calls."""
from pathlib import Path
import hashlib
import json
import math
import re
import os

ROOT = Path(__file__).resolve().parent
COMFY = Path(os.environ.get('TMT_COMFY_ROOT','E:/ComfyUI_windows_portable/ComfyUI'))
SIZES = {'draft': [896, 512], 'full-transfer': [1280, 736]}
FIELDS = ['subject_definitions', 'summary', 'retention_analysis',
          'detailed_description', 'overall_soundscape', 'non_diegetic_music']


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def atomic_write(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.pending')
    write(temporary, value)
    temporary.replace(path)


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def frames(seconds):
    require(math.isfinite(seconds) and 2 <= seconds <= 8, 'Candidate duration: 2-8 seconds')
    count = round(seconds * 24)
    return count + (5 - count % 17) % 17


def prompt_contract(prompt, pictures, transfer):
    names = re.findall(r'^([a-z_]+):', prompt, re.M)
    require(names == FIELDS, 'Use exactly the six Ref2VA sections, in order')
    found = {int(n) for n in re.findall(r'<Picture (\d+)>', prompt)}
    require(found == set(range(1, pictures + 1)), 'Picture labels must match the ordered images')
    videos = {int(n) for n in re.findall(r'<Video (\d+)>', prompt)}
    require(videos == ({1} if transfer else set()), 'Only Full Transfer has exactly Video 1')
    require(not re.search(r'<Audio \d+>', prompt), 'Audio references are outside these profiles')
    for name in FIELDS[-2:]:
        value = prompt.split(name + ':', 1)[1].split('\n\nnon_diegetic_music:', 1)[0].strip()
        require(value == 'N/A', 'Both sound fields must be N/A')


def video_info(path):
    import av
    with av.open(str(path)) as container:
        require(len(container.streams.video) == 1, 'Exactly one motion video stream is required')
        stream = container.streams.video[0]
        rate = stream.average_rate
        require(rate is not None and float(rate) == 24, 'Motion reference must be 24 fps')
        times = []
        for frame in container.decode(video=0):
            require(frame.time is not None, 'Motion frames need timestamps')
            times.append(float(frame.time))
            require(len(times) <= 209, 'Motion reference exceeds bounded 8-second profile')
        require(len(times) >= 2, 'Motion reference has too few frames')
        require(all(abs((b - a) - 1 / 24) < 0.0001 for a, b in zip(times, times[1:])),
                'Motion reference must have constant 24 fps timestamps')
        return {'frames': len(times), 'fps': 24, 'size': [stream.width, stream.height],
                'seconds': len(times) / 24, 'has_audio': bool(container.streams.audio),
                'audio_connected': False}


def verify_job(job, staged=True):
    job = Path(job).resolve()
    build = read(job / 'Build.json')
    require(build['version'] == 2, 'Wrong job version')
    if staged:
        require(build['staged'], 'Prepare a new job with --stage before validation/run')
    for name, digest in build['file_hashes'].items():
        require(sha(job / name) == digest, name + ' changed; prepare a new job')
    for name, digest in build['implementation_hashes'].items():
        require(sha(ROOT / name) == digest, 'Implementation changed: ' + name)
    for row in build['source_input_hashes']:
        require(sha(row['Source']) == row['SHA256'], 'Source changed: ' + row['Source'])
        if staged:
            target = Path(row['Target']).resolve()
            require(target.parent == (COMFY / 'input').resolve(), 'Unexpected staged directory')
            require(sha(target) == row['SHA256'], 'Staged input changed: ' + str(target))
    for row in build['model_files']:
        stat = Path(row['path']).stat()
        require([stat.st_size, stat.st_mtime_ns] == [row['bytes'], row['mtime_ns']],
                'Model metadata changed: ' + row['path'])
    return build

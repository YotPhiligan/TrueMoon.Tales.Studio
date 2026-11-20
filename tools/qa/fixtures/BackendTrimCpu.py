"""Bounded proof using the existing CPU renderer on an independent frozen request.

This does not open schema2 or add an export endpoint. Only observed SourceFrames
PTS is recorded; selection and encoding remain the production CPU renderer.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(sys.argv[1]) / 'workers/export'))
os.environ['IMAGEIO_FFMPEG_EXE'] = os.environ['STUDIO_TEST_FFMPEG']
from studio import export_worker

folder = Path(sys.argv[2]).resolve()
request = json.loads((folder / 'Request.json').read_text(encoding='utf-8'))
source_hashes = {key: hashlib.sha256(Path(row['path']).read_bytes()).hexdigest() for key, row in request['assets'].items()}
source_probes = {key: json.loads(subprocess.check_output([os.environ['STUDIO_TEST_FFPROBE'], '-v', 'error', '-show_format', '-show_streams', '-of', 'json', row['path']], timeout=10)) for key, row in request['assets'].items()}
versions = {key: subprocess.check_output([os.environ[key], '-version'], timeout=10).decode('utf-8', errors='replace') for key in ['STUDIO_TEST_FFMPEG', 'STUDIO_TEST_FFPROBE']}
observations = []
commands = []
original = export_worker.SourceFrames

class ObservedFrames(original):
    def at(self, seconds):
        frame = super().at(seconds)
        observations.append({'requested_seconds': seconds, 'actual_seconds': float(frame.time) - self.origin})
        return frame

export_worker.SourceFrames = ObservedFrames
class ObservedRenderer(export_worker.DraftRenderer):
    def command(self, args):
        commands.append([self.ffmpeg, '-hide_banner', '-loglevel', 'error', '-nostdin', '-filter_threads', '1', *args])
        return super().command(args)

renderer = ObservedRenderer(folder)
result = renderer.run()
probe = json.loads(subprocess.check_output([os.environ['STUDIO_TEST_FFPROBE'], '-v', 'error', '-show_format', '-show_streams', '-of', 'json', str(folder / 'Draft.mp4')], timeout=10))
subprocess.run([os.environ['STUDIO_TEST_FFMPEG'], '-nostdin', '-v', 'error', '-i', str(folder / 'Draft.mp4'), '-map', '0:v:0', '-map', '0:a:0', '-f', 'null', '-'], check=True, timeout=10, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
assert source_hashes == {key: hashlib.sha256(Path(row['path']).read_bytes()).hexdigest() for key, row in request['assets'].items()}
proof = {'result': result, 'probe': probe, 'source_hashes': source_hashes, 'source_probes': source_probes, 'tool_versions': versions,
         'commands': commands, 'request_sha256': hashlib.sha256((folder / 'Request.json').read_bytes()).hexdigest(),
         'source_samples': observations, 'boundary': 'Existing CPU renderer plus local decode; no physical player or whole-editor export acceptance.'}
with (folder / 'Acceptance.json').open('x', encoding='utf-8') as out:
    json.dump(proof, out, ensure_ascii=False, indent=2)
print(json.dumps({'state': result['state'], 'sha256': result['sha256'], 'samples_observed': len(observations)}))

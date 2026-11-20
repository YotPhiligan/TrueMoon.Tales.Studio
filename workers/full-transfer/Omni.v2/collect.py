"""Collect one successful silent Draft / Full Transfer result; never enqueue."""
from pathlib import Path
import argparse
import sys
import av
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import read, require, sha, verify_job, write


def collect(job):
    job = Path(job).resolve()
    build = verify_job(job)
    history = read(job / 'History.json')
    require(history['status']['status_str'] == 'success', 'Generation did not succeed')
    require(read(job / 'Cleanup.json')['stopped'], 'Owned server has not stopped')
    require(read(job / 'Submission.json')['submissions'] == 1, 'Expected exactly one submission')
    runtime = read(job / 'Runtime.json')
    telemetry = read(job / 'Telemetry.summary.json')
    outputs = {}
    output_dir = (job / 'output').resolve()
    for node, values in history['outputs'].items():
        for index, row in enumerate(values.get('images', [])):
            path = (output_dir / row['subfolder'] / row['filename']).resolve()
            require(path.is_relative_to(output_dir) and path.is_file(), 'Unexpected output path')
            outputs[node + '.' + str(index)] = {'path': str(path), 'sha256': sha(path), 'bytes': path.stat().st_size}
    require('92.0' in outputs and '202.0' in outputs, 'Missing raw MP4 or final PNG')
    with av.open(outputs['92.0']['path']) as container:
        require(float(container.streams.video[0].average_rate) == 24, 'Unexpected output fps')
        require(not container.streams.audio, 'Output must be silent')
        images = [frame.to_image() for frame in container.decode(video=0)]
    require(len(images) == build['frames'], 'Unexpected frame count')
    require(all(list(image.size) == build['size'] for image in images), 'Unexpected output size')
    with Image.open(outputs['202.0']['path']) as image:
        require(list(image.size) == build['size'], 'Final PNG size differs')
    canvas = Image.new('RGB', (1920, 608), '#101015')
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.truetype('C:/Windows/Fonts/consola.ttf', 16)
    for slot, index in enumerate(round(i*(len(images)-1)/7) for i in range(8)):
        x, y = slot % 4 * 480, slot // 4 * 304
        thumb = images[index].copy()
        thumb.thumbnail((480, 276), Image.Resampling.LANCZOS)
        canvas.paste(thumb, (x+(480-thumb.width)//2, y))
        draw.text((x+5, y+280), f'frame {index} / {index/24:.3f}s', font=font, fill='white')
    canvas.save(job / 'Contact.png')
    finite = bool(runtime['h3_forwards']) and all(all(row['output_finite']) for row in runtime['h3_forwards'])
    require(finite and runtime['sage_success'] > 0 and not runtime['sdpa_calls'], 'Inspect attention / finite output diagnostics')
    messages = dict(history['status']['messages'])
    result = {'id': build['id'], 'profile': build['profile'], 'outputs': outputs,
              'frames': len(images), 'fps': 24, 'size': build['size'], 'silent': True,
              'server_wall_seconds': (messages['execution_success']['timestamp']-messages['execution_start']['timestamp'])/1000,
              'sage_success': runtime['sage_success'], 'sdpa_calls': runtime['sdpa_calls'], 'all_h3_finite': finite,
              'rss_peak_GiB': telemetry['process_rss_peak_bytes']/2**30,
              'device_gpu_peak_GiB': telemetry['device_gpu_peak_MiB']/1024,
              'motion_video': build['motion_video'], 'generation_executed': True, 'production_accepted': False}
    write(job / 'Result.json', result)
    print(result)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('job')
    args = parser.parse_args()
    collect(args.job)

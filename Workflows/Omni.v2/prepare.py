"""Prepare a single candidate profile. --stage copies inputs, never submits work."""
from pathlib import Path
import argparse
import copy
import re
import shutil
import uuid
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import COMFY, ROOT, SIZES, frames, prompt_contract, read, require, sha, video_info, write


def prepare(config, destination, stage=False):
    config = Path(config).resolve()
    dest = Path(destination).resolve()
    cfg = read(config)
    global COMFY
    COMFY = Path(cfg.get('runtime', {}).get('comfy_root', COMFY)).resolve()
    require(cfg['version'] == 2 and cfg['profile'] in SIZES, 'Unknown v2 profile')
    require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,80}', cfg['id']), 'Use a simple unique job ID')
    require(not dest.exists(), 'Destination exists; preserve earlier job')
    require(cfg.get('fps') == 24 and cfg.get('silent') is True, 'These profiles are silent at 24 fps')
    require(1 <= len(cfg['references']) <= 8, 'Use 1-8 ordered image references')
    require(all(str(r['role']).strip() for r in cfg['references']), 'Every image needs an explicit role')
    sec = float(cfg['seconds'])
    length = frames(sec)
    width, height = SIZES[cfg['profile']]
    require(cfg.get('size', [width, height]) == [width, height], 'Profile resolution is fixed')
    require(type(cfg['seed']) is int and 0 <= cfg['seed'] < 2**64, 'Invalid seed')
    guides = cfg['guides']
    require(len(guides) == 1 and type(guides[0]['picture']) is int
            and type(guides[0]['frame']) is int and guides[0]['frame'] == 0,
            'This candidate supports one first-frame Guide 0')
    require(1 <= guides[0]['picture'] <= len(cfg['references']), 'Guide picture is out of range')
    prompt = (config.parent / cfg['prompt']).resolve().read_text(encoding='utf-8-sig').rstrip('\n')
    transfer = cfg['profile'] == 'full-transfer'
    prompt_contract(prompt, len(cfg['references']), transfer)
    inputs = []

    def register(source, role):
        path = (config.parent / source).resolve()
        require(path.is_file(), 'Missing input: ' + str(path))
        digest = sha(path)
        name = 'TMT.' + cfg['id'] + '.' + str(len(inputs) + 1) + '.' + digest[:10] + path.suffix.lower()
        target = COMFY / 'input' / name
        inputs.append({'role': role, 'Source': str(path), 'Target': str(target), 'SHA256': digest})
        return name

    api = read(ROOT / 'Templates/Native.api.json')
    ui = read(ROOT / 'Templates/Native.ui.json')
    definitions = {node['type']: node for node in ui['nodes'] + read(ROOT / 'Templates/OutputNodes.ui.json')}
    api = {key: spec for key, spec in api.items()
           if spec['class_type'] not in ('LoadImage', 'MiniMaxH3AddGuide', 'LoadVideo', 'GetVideoComponents')}
    api['136']['inputs'] = {key: value for key, value in api['136']['inputs'].items()
                            if not key.startswith('ref_') or key == 'ref_image_size'}
    api['136']['inputs'].update(width=width, height=height, ref_image_size='match')
    api['138']['inputs']['value'] = prompt
    api['132']['inputs']['value'] = sec
    api['129']['inputs']['noise_seed'] = cfg['seed']
    api['92']['inputs']['filename_prefix'] = 'video/' + cfg['id'] + '.Raw'
    for index, ref in enumerate(cfg['references'], 1):
        node = str(1000 + index)
        api[node] = {'class_type': 'LoadImage', 'inputs': {'image': register(ref['path'], ref['role'])}}
        api['136']['inputs'][f'ref_images.ref_image_{index-1}'] = [node, 0]
    api['170'] = {'class_type': 'MiniMaxH3AddGuide', 'inputs': {
        'positive': ['136', 0], 'latent': ['136', 1], 'vae': ['119', 0],
        'audio_vae': ['120', 0], 'image': [str(1000 + guides[0]['picture']), 0], 'frame_idx': 0}}
    api['126']['inputs']['conditioning'] = ['170', 0]
    motion = None
    if transfer:
        spec = cfg['motion_video']
        require(str(spec['role']).strip() and str(spec['review_notes']).strip(),
                'Record the selected video role and actual motion phases before transfer')
        require(Path(spec['path']).suffix.lower() == '.mp4', 'Use a selected MP4')
        motion = video_info((config.parent / spec['path']).resolve())
        require(motion['frames'] == length, 'Motion reference frame count must match the rounded output length')
        name = register(spec['path'], 'Video 1 / ' + spec['role'])
        api['171'] = {'class_type': 'LoadVideo', 'inputs': {'file': name}}
        api['172'] = {'class_type': 'GetVideoComponents', 'inputs': {'video': ['171', 0]}}
        api['136']['inputs']['ref_videos.ref_video_0'] = ['172', 0]
    else:
        require('motion_video' not in cfg, 'Draft has no external motion video')
    api['201'] = {'class_type': 'ImageFromBatch', 'inputs': {
        'image': ['122', 0], 'batch_index': length - 1, 'length': 1}}
    api['202'] = {'class_type': 'SaveImage', 'inputs': {
        'images': ['201', 0], 'filename_prefix': 'frames/' + cfg['id'] + '.Last'}}

    nodes = []
    for key, spec in api.items():
        typ, values = spec['class_type'], spec['inputs']
        node = copy.deepcopy(definitions[typ])
        nid = int(key)
        node.update(id=nid, order=nid, mode=0, flags={})
        for slot in node.get('inputs', []):
            slot['link'] = None
        for slot in node.get('outputs', []):
            slot['links'] = []
        if typ == 'MiniMaxH3ReferenceToVideo':
            node['inputs'] = [p for p in node['inputs'] if not p['name'].startswith('ref_') or p['name'] == 'ref_image_size']
            for index in range(len(cfg['references'])):
                node['inputs'].insert(3 + index, {'name': f'ref_images.ref_image_{index}', 'type': 'IMAGE', 'link': None})
            if transfer:
                node['inputs'].insert(3 + len(cfg['references']), {'name': 'ref_videos.ref_video_0', 'type': 'IMAGE', 'link': None})
        node['widgets_values_named'] = {k: v for k, v in values.items() if type(v) in (str, int, float, bool)}
        widgets = {
            'VAELoader': lambda: [values['vae_name']],
            'UNETLoader': lambda: [values['unet_name'], values['weight_dtype']],
            'CLIPLoader': lambda: [values['clip_name'], values['type'], values['device']],
            'LoadImage': lambda: [values['image'], 'image'],
            'LoadVideo': lambda: [values['file']],
            'PrimitiveStringMultiline': lambda: [values['value']],
            'PrimitiveFloat': lambda: [values['value']],
            'PrimitiveInt': lambda: [values['value']],
            'RandomNoise': lambda: [values['noise_seed'], 'fixed'],
            'MiniMaxH3ReferenceToVideo': lambda: ['', width, height, length, 'match'],
            'MiniMaxH3AddGuide': lambda: [0],
            'ImageFromBatch': lambda: [length - 1, 1],
            'SaveImage': lambda: [values['filename_prefix']],
            'SaveVideo': lambda: [values['filename_prefix'], 'auto', 'auto', 'auto']}
        if typ in widgets:
            node['widgets_values'] = widgets[typ]()
        if typ == 'MiniMaxH3ReferenceToVideo':
            node['widgets_values_named'].update(prompt='', length=length)
        node['title'] = key + ' / ' + typ
        if typ == 'LoadImage':
            node['title'] = f'Picture {nid-1000} / ' + cfg['references'][nid-1001]['role']
        nodes.append(node)
    ns = {node['id']: node for node in nodes}
    links = []
    for key, spec in api.items():
        for name, value in spec['inputs'].items():
            if not isinstance(value, list):
                continue
            src, slot, dst = int(value[0]), value[1], int(key)
            target = next(i for i, p in enumerate(ns[dst]['inputs']) if p['name'] == name)
            lid = len(links) + 1
            links.append([lid, src, slot, dst, target, ns[src]['outputs'][slot]['type']])
            ns[src]['outputs'][slot]['links'].append(lid)
            ns[dst]['inputs'][target]['link'] = lid
    positions = {127:(0,0),128:(0,150),119:(0,330),120:(0,440),138:(720,0),132:(0,600),
                 131:(330,600),143:(0,720),129:(330,720),165:(720,700),123:(720,880),124:(1100,700),
                 126:(1500,700),125:(1900,700),122:(2250,700),130:(2600,700),92:(2950,700),
                 136:(720,1200),170:(1500,1200),171:(0,1700),172:(400,1700),201:(2600,1150),202:(2950,1150)}
    for nid, node in ns.items():
        node['pos'] = list(positions.get(nid, ((nid-1001)%4*450, 2150+(nid-1001)//4*400)))
        if 1000 < nid < 1100:
            node['size'] = [400, 320]
        if nid == 138:
            node.update(size=[850, 620], title='PROMPT / ordered Picture roles; Video 1 only for transfer')
        if nid == 92:
            node['size'] = [800, 360]
        if nid == 171:
            node['title'] = 'Video 1 / selected motion / IMAGE batch only; audio disconnected'
    ui.update(id=str(uuid.uuid4()), revision=0, nodes=nodes, links=links, groups=[],
              last_node_id=max(ns), last_link_id=len(links),
              extra={'ds': {'scale': 0.45, 'offset': [50, 50]}, 'tmt_pipeline': {
                  'version': 2, 'profile': cfg['profile'], 'job': cfg['id'], 'silent': True}})
    model_files = []
    for key, folder, name in [('127','diffusion_models','unet_name'),('128','text_encoders','clip_name'),
                              ('119','vae','vae_name'),('120','vae','vae_name')]:
        path = COMFY / 'models' / folder / api[key]['inputs'][name]
        stat = path.stat()
        model_files.append({'path': str(path), 'bytes': stat.st_size, 'mtime_ns': stat.st_mtime_ns})
    # All configuration and video checks above precede any staging or job-directory mutation.
    if stage:
        for row in inputs:
            target = Path(row['Target'])
            if target.exists():
                require(sha(target) == row['SHA256'], 'Preserve conflicting staged input')
            else:
                shutil.copy2(row['Source'], target)
            require(sha(target) == row['SHA256'], 'Input changed while copying')
    dest.mkdir(parents=True)
    for name, value in [('Job.json',cfg),('Prepared.api.json',api),('Workflow.ui.json',ui),('Inputs.prepared.json',inputs)]:
        write(dest / name, value)
    (dest / 'Prompt.txt').write_text(prompt, encoding='utf-8')
    build = {'version': 2, 'id': cfg['id'], 'profile': cfg['profile'], 'config_source': str(config),
             'frames': length, 'fps': 24, 'size': [width,height], 'requested_seconds': sec,
             'duration_seconds': length/24, 'silent': True, 'motion_video': motion,
             'source_input_hashes': inputs, 'model_files': model_files, 'staged': stage,
             'generation_executed': False, 'production_accepted': False,
             'file_hashes': {name: sha(dest/name) for name in
                             ['Job.json','Prepared.api.json','Workflow.ui.json','Inputs.prepared.json','Prompt.txt']},
             'implementation_hashes': {str(path.relative_to(ROOT)): sha(path)
                 for path in sorted(ROOT.rglob('*')) if path.is_file() and path.suffix in ('.py','.ps1')},
             'template_hashes': {path.name: sha(path) for path in sorted((ROOT/'Templates').glob('*.json'))},
             'model_hash_scope': 'Size and nanosecond mtime only; weights not rehashed'}
    write(dest / 'Build.json', build)
    print({'prepared': str(dest), 'frames': length, 'seconds': length/24, 'staged': stage, 'enqueued': False})


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config')
    parser.add_argument('destination')
    parser.add_argument('--stage', action='store_true')
    args = parser.parse_args()
    prepare(args.config, args.destination, args.stage)

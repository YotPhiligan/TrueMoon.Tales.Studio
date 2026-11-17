"""Small standard-library CLI for an external local agent. Never retries writes."""
from __future__ import annotations
import argparse, base64, hashlib, json, math, os, struct, sys, zlib
from pathlib import Path
from urllib import error, parse, request


class ClientError(Exception):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


class StudioClient:
    def __init__(self, url='http://127.0.0.1:8787', actor='agent:cli'):
        parsed = parse.urlsplit(url)
        if parsed.scheme != 'http' or parsed.hostname not in ['127.0.0.1', 'localhost'] or parsed.path not in ['', '/'] or parsed.query or parsed.fragment or parsed.username or parsed.password:
            raise ClientError('Адрес Studio должен быть локальным HTTP-адресом без пути и учётных данных.')
        self.url = url.rstrip('/')
        self.actor = actor
        self.token = None

    def call(self, method, path, body=None):
        headers = {}
        data = None
        if method != 'GET':
            if self.token is None:
                session = self.call('GET', '/api/bootstrap')
                self.token = session['token']
            headers = {'X-Studio-Token': self.token, 'Content-Type': 'application/json'}
            data = json.dumps({**(body or {}), 'actor': self.actor}, ensure_ascii=False).encode('utf-8')
        req = request.Request(self.url + path, data=data, headers=headers, method=method)
        try:
            with request.urlopen(req, timeout=120) as response:
                return json.load(response)
        except error.HTTPError as exc:
            try:
                message = json.load(exc).get('error', str(exc))
            except (ValueError, OSError):
                message = str(exc)
            raise ClientError(message, exc.code) from exc
        except (OSError, ValueError) as exc:
            raise ClientError('Нет ответа Studio. Запустите Start-Studio.cmd. Запись автоматически не повторяется; перед повтором проверьте состояние проекта/задания.') from exc


class _NoBackendRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class BackendClient(StudioClient):
    """Versioned schema2 adapter; a caller-supplied baseline is never refreshed for a write."""
    def __init__(self, url, token):
        super().__init__(url)
        if parse.urlsplit(url).hostname != '127.0.0.1':
            raise ClientError('Backend API требует literal 127.0.0.1.')
        if not token or len(token) != 64 or any(c not in '0123456789abcdefABCDEF' for c in token):
            raise ClientError('Укажите token сессии через --token или STUDIO_BACKEND_HTTP_TOKEN.')
        self.token=token
        self.opener=request.build_opener(request.ProxyHandler({}),_NoBackendRedirect())

    def call(self, method, path, body=None):
        if path=='/api/backend/v1/trim-clip':
            if method!='POST':raise ClientError('Backend trim требует POST.')
            _backend_trim_query(body)
        if path in ['/api/backend/v1/media-info','/api/backend/v1/frame']:
            if method!='POST':raise ClientError('Backend media требует POST.')
            _backend_media_query(body,path.endswith('/frame'))
        headers={'X-Studio-Token':self.token}
        data=None
        if body is not None:
            data=json.dumps(body,ensure_ascii=False,allow_nan=False).encode('utf-8')
            headers['Content-Type']='application/json'
        req=request.Request(self.url+path,data=data,headers=headers,method=method)
        try:
            with self.opener.open(req,timeout=15) as response:
                encoded=response.read(2*1024*1024+1)
                if len(encoded)>2*1024*1024:raise ClientError('Слишком большой ответ backend. Перечитайте проект перед следующей записью.')
                snapshot=json.loads(encoded,object_pairs_hook=_backend_json_object)
            if not isinstance(snapshot,dict) or type(snapshot.get('api_version')) is not int or snapshot['api_version']!=1 or type(snapshot.get('schema_version')) is not int or snapshot['schema_version']!=2:
                raise ValueError('Unsupported backend envelope')
            if path in ['/api/backend/v1/media-info','/api/backend/v1/frame']:
                return _backend_media_response(snapshot,body,path.endswith('/frame'))
            project=snapshot.get('project'); fingerprint=snapshot.get('fingerprint')
            if not isinstance(project,dict) or type(project.get('contract_version')) is not int or project['contract_version']!=1 or type(project.get('revision')) is not int or project['revision']<0 or not isinstance(project.get('id'),str):
                raise ValueError('Invalid backend project')
            if not isinstance(fingerprint,str) or len(fingerprint)!=64 or any(c not in '0123456789abcdefABCDEF' for c in fingerprint):
                raise ValueError('Invalid backend fingerprint')
            if path=='/api/backend/v1/trim-clip':_backend_trim_response(snapshot,body)
            return snapshot
        except error.HTTPError as exc:
            try:message=json.loads(exc.read(4096)).get('error',str(exc))
            except (ValueError,OSError,AttributeError):message=str(exc)
            raise ClientError(message,exc.code) from exc
        except (OSError,ValueError) as exc:
            raise ClientError('Ответ backend не получен или недостоверен. Запись автоматически не повторяется; перечитайте проект для сверки результата.') from exc



def _backend_json_object(pairs):
    result={}
    for key,value in pairs:
        if key in result:raise ValueError('Duplicate backend JSON field')
        result[key]=value
    return result


def _sha256_text(value):
    return isinstance(value,str) and len(value)==64 and all(c in '0123456789abcdefABCDEF' for c in value)



def _backend_number(value):
    try:return type(value) in (int,float) and math.isfinite(value)
    except OverflowError:return False


def _backend_trim_query(body):
    fields={'clip_id','source_in_frame','source_out_frame','start_frame','expected_revision','fingerprint'}
    if not isinstance(body,dict) or set(body)!=fields:raise ClientError('Некорректная trim query.')
    clip=body['clip_id'];revision=body['expected_revision']
    if not isinstance(clip,str) or not clip.strip() or chr(0) in clip or len(clip.encode('utf-16-le'))>320:
        raise ClientError('Нужен существующий clip ID до160 UTF-16 символов.')
    if type(revision) is not int or not 0<=revision<=2**63-1 or not _sha256_text(body['fingerprint']):
        raise ClientError('Нужны observed revision/SHA.')
    if type(body['start_frame']) is not int or not 0<=body['start_frame']<=2**31-1:
        raise ClientError('Нужен неотрицательный start в диапазоне Int32.')
    start,end=body['source_in_frame'],body['source_out_frame']
    if not _backend_number(start) or not _backend_number(end) or start<0 or end<=start:
        raise ClientError('Нужен конечный участок source-in < source-out, начиная с нуля.')


def _backend_trim_response(snapshot,body):
    project=snapshot['project'];revision=project['revision'];fingerprint=snapshot['fingerprint']
    if revision not in (body['expected_revision'],body['expected_revision']+1):
        raise ValueError('Unexpected backend trim revision')
    same=fingerprint.lower()==body['fingerprint'].lower()
    if same!=(revision==body['expected_revision']):raise ValueError('Invalid trim baseline transition')
    montage=project.get('montage')
    if not isinstance(montage,dict) or not isinstance(montage.get('clips'),list):raise ValueError('Missing saved montage')
    clips=[clip for clip in montage['clips'] if isinstance(clip,dict) and clip.get('id')==body['clip_id']]
    if len(clips)!=1:raise ValueError('Missing or duplicate saved trim clip')
    clip=clips[0];speed=clip.get('speed',1);source=clip.get('source_in_frame');end=clip.get('source_out_frame')
    duration=clip.get('duration_frames');start=clip.get('start_frame')
    if type(duration) is not int or not 1<=duration<=2**31-1 or type(start) is not int or start!=body['start_frame']:
        raise ValueError('Unexpected saved trim placement')
    if not _backend_number(speed) or not .25<=speed<=4 or not _backend_number(source) or source!=body['source_in_frame']:
        raise ValueError('Unexpected saved trim source')
    if end is None:end=source+duration*speed
    if not _backend_number(end) or end!=body['source_out_frame']:raise ValueError('Unexpected saved trim extent')
    frames=(end-source)/speed
    if not _backend_number(frames) or frames>2**31-1 or duration!=max(1,math.ceil(frames-1e-8)):
        raise ValueError('Unexpected saved trim extent')


def _backend_media_query(body,frame):
    fields={'asset_id','expected_revision','fingerprint'}|({'frame'} if frame else set())
    if not isinstance(body,dict) or set(body)!=fields:
        raise ClientError('Некорректная media query.')
    asset=body['asset_id'];revision=body['expected_revision']
    if not isinstance(asset,str) or not asset.strip() or chr(0) in asset or len(asset.encode('utf-16-le'))>320:
        raise ClientError('Нужен существующий asset ID до160 UTF-16 символов.')
    if type(revision) is not int or not 0<=revision<=2**63-1 or not _sha256_text(body['fingerprint']):
        raise ClientError('Нужны observed revision/SHA.')
    if frame and (type(body['frame']) is not int or not 0<=body['frame']<=2**31-1):
        raise ClientError('Нужен неотрицательный frame в диапазоне Int32.')


def _backend_media_response(snapshot,body,frame):
    fields={'error_code','asset_id','revision','fingerprint','fps','frame_count','source_width','source_height',
            'frame_index','width','height','pixels','pixel_sha256'}
    media=snapshot.get('media')
    if set(snapshot)!={'api_version','schema_version','media'} or not isinstance(media,dict) or set(media)!=fields or not isinstance(body,dict):
        raise ValueError('Invalid backend media envelope')
    expected_frame=body.get('frame') if frame else -1
    integer_bounds={'revision':(0,2**63-1),'frame_count':(1,10_000_000),'source_width':(1,8192),
                    'source_height':(1,8192),'width':(480,480),'height':(270,270),'frame_index':(-1,2**31-1)}
    if any(type(media.get(key)) is not int or not low<=media[key]<=high for key,(low,high) in integer_bounds.items()):
        raise ValueError('Invalid backend media dimensions or integers')
    if media['error_code']!='' or not isinstance(media['error_code'],str) or not isinstance(media['asset_id'],str) or media['asset_id']!=body.get('asset_id') or media['revision']!=body.get('expected_revision'):
        raise ValueError('Backend media identity does not match the requested baseline')
    if not _sha256_text(media['fingerprint']) or not _sha256_text(body.get('fingerprint')) or media['fingerprint'].lower()!=body['fingerprint'].lower():
        raise ValueError('Backend media baseline SHA does not match')
    if type(media['fps']) not in (int,float) or not 0<media['fps']<=240 or not math.isfinite(media['fps']) or media['source_width']*media['source_height']>16_777_216:
        raise ValueError('Invalid backend source extent or rate')
    if type(expected_frame) is not int or media['frame_index']!=expected_frame or expected_frame>=media['frame_count']:
        raise ValueError('Unexpected backend frame index')
    pixels=media['pixels'];digest=media['pixel_sha256']
    if not isinstance(pixels,str) or not isinstance(digest,str):
        raise ValueError('Invalid backend pixel encoding')
    if not frame:
        if pixels!='' or digest!='':raise ValueError('Pixels in metadata-only reply')
    else:
        if len(pixels)!=691200 or not _sha256_text(digest):raise ValueError('Invalid backend frame size or SHA')
        decoded=base64.b64decode(pixels,validate=True)
        if len(decoded)!=518400 or hashlib.sha256(decoded).hexdigest()!=digest.lower():
            raise ValueError('Corrupt backend frame bytes')
    return snapshot


def _backend_output_path(value):
    output=Path(value).expanduser().absolute()
    if os.name=='nt' and (':' in output.name or output.name.endswith(('.', ' '))):
        raise ClientError('ADS и неоднозначные Windows имена не являются новым PNG-файлом.')
    if output.suffix.lower()!='.png' or not output.parent.is_dir() or os.path.lexists(output):
        raise ClientError('--output должен указывать на новый PNG в существующем каталоге; файлы не перезаписываются.')
    if os.name=='nt' and output.name.split('.',1)[0].rstrip(' .').upper() in {'CON','PRN','AUX','NUL',*(f'COM{i}' for i in range(1,10)),*(f'LPT{i}' for i in range(1,10))}:
        raise ClientError('Device path не является PNG-файлом.')
    return output


def _backend_frame_png(pixels):
    if len(pixels)!=518400:raise ValueError('Unexpected frame bytes')
    rgba=bytearray(len(pixels))
    rgba[0::4]=pixels[2::4];rgba[1::4]=pixels[1::4];rgba[2::4]=pixels[0::4];rgba[3::4]=pixels[3::4]
    stride=480*4
    rows=b''.join(b'\0'+rgba[start:start+stride] for start in range(0,len(rgba),stride))
    def chunk(kind,data):
        return struct.pack('!I',len(data))+kind+data+struct.pack('!I',zlib.crc32(kind+data)&0xffffffff)
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('!IIBBBBB',480,270,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(rows))+chunk(b'IEND',b'')


def _backend_save_frame(snapshot,output):
    png=_backend_frame_png(base64.b64decode(snapshot['media']['pixels'],validate=True))
    # Exclusive creation also closes a race between the preflight and the HTTP response.
    with output.open('xb') as handle:handle.write(png)
    return {'api_version':snapshot['api_version'],'schema_version':snapshot['schema_version'],
            'media':{key:value for key,value in snapshot['media'].items() if key!='pixels'},
            'output_path':str(output),'png_sha256':hashlib.sha256(png).hexdigest()}

def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--url', default='http://127.0.0.1:8787')
    p.add_argument('--project', help='ID проекта из команды projects')
    p.add_argument('--token', default=os.environ.get('STUDIO_BACKEND_HTTP_TOKEN'), help='Token versioned backend HTTP API')
    p.add_argument('--actor', default='agent:cli')
    commands = p.add_subparsers(dest='command', required=True)
    commands.add_parser('backend-project')
    sub=commands.add_parser('backend-move')
    sub.add_argument('--clip',required=True);sub.add_argument('--start-frame',type=int,required=True)
    sub.add_argument('--revision',type=int,required=True);sub.add_argument('--sha256',required=True)
    sub=commands.add_parser('backend-trim')
    sub.add_argument('--clip',required=True);sub.add_argument('--source-in',type=float,required=True)
    sub.add_argument('--source-out',type=float,required=True)
    sub.add_argument('--start','--start-frame',dest='start_frame',type=int,required=True)
    sub.add_argument('--revision',type=int,required=True);sub.add_argument('--sha256',required=True)
    for name in ['backend-media','backend-frame']:
        sub=commands.add_parser(name)
        sub.add_argument('--asset',required=True);sub.add_argument('--revision',type=int,required=True);sub.add_argument('--sha256',required=True)
        if name=='backend-frame':sub.add_argument('--frame',type=int,required=True);sub.add_argument('--output',required=True)
    for name in ['doctor', 'projects', 'requests', 'exports']:
        commands.add_parser(name)
    sub=commands.add_parser('export');sub.add_argument('--revision',type=int,required=True);sub.add_argument('--profile',choices=['draft','project','final'],default='draft');sub.add_argument('--client-key')
    sub=commands.add_parser('export-status');sub.add_argument('--id',required=True)
    sub=commands.add_parser('export-stop');sub.add_argument('--id',required=True);sub.add_argument('--revision',type=int,required=True)
    sub = commands.add_parser('context'); sub.add_argument('--block'); sub.add_argument('--segment'); sub.add_argument('--item')
    sub = commands.add_parser('brief'); sub.add_argument('--file'); sub.add_argument('--revision', type=int)
    sub = commands.add_parser('scenario'); sub.add_argument('--file'); sub.add_argument('--revision', type=int)
    sub = commands.add_parser('timeline'); sub.add_argument('--file'); sub.add_argument('--revision', type=int)
    sub = commands.add_parser('request'); sub.add_argument('--block'); sub.add_argument('--goal-file', required=True)
    sub.add_argument('--mode', choices=['propose', 'apply'], default='propose'); sub.add_argument('--revision', type=int, required=True)
    sub.add_argument('--scope-file'); sub.add_argument('--client-key')
    sub = commands.add_parser('task'); sub.add_argument('--id', required=True)
    for name in ['claim', 'finish', 'fail', 'cancel']:
        sub = commands.add_parser(name); sub.add_argument('--id', required=True); sub.add_argument('--revision', type=int, required=True)
        if name in ['finish', 'fail']:
            sub.add_argument('--result-file', required=True)
        if name == 'finish':
            sub.add_argument('--target-file'); sub.add_argument('--proposal-file')
    for name in ['patch-block', 'prepare', 'continue']:
        sub = commands.add_parser(name); sub.add_argument('--block', required=True); sub.add_argument('--revision', type=int, required=True)
        if name == 'patch-block': sub.add_argument('--file', required=True)
        if name == 'continue': sub.add_argument('--take', required=True)
    for name in ['launch', 'stop', 'job']:
        sub = commands.add_parser(name); sub.add_argument('--id', required=True)
    return p


def file_text(path):
    return Path(path).read_text(encoding='utf-8-sig')


def task_summary(task):
    return {**{key: value for key, value in task.items() if key != 'context'},
            'snapshot_sha256': task.get('context', {}).get('snapshot_sha256')}


def execute(client, args):
    command = args.command
    if command in ['backend-project','backend-move','backend-trim','backend-media','backend-frame']:
        if args.project:raise ClientError('Backend project выбирается владельцем --serve-project, без --project в запросе.')
        if command=='backend-project':return client.call('GET','/api/backend/v1/project')
        if type(args.revision) is not int or not 0<=args.revision<=2**63-1 or not _sha256_text(args.sha256):
            raise ClientError('Нужны observed --revision и 64 hex --sha256 из backend-project.')
        if command=='backend-trim':
            body={'clip_id':args.clip,'source_in_frame':args.source_in,'source_out_frame':args.source_out,
                  'start_frame':args.start_frame,'expected_revision':args.revision,'fingerprint':args.sha256}
            _backend_trim_query(body)
            return client.call('POST','/api/backend/v1/trim-clip',body)
        if command=='backend-move':
            return client.call('POST','/api/backend/v1/move-clip',{'clip_id':args.clip,'start_frame':args.start_frame,
                'expected_revision':args.revision,'fingerprint':args.sha256})
        if not isinstance(args.asset,str) or not args.asset.strip() or len(args.asset.encode('utf-16-le'))>320 or chr(0) in args.asset:
            raise ClientError('Нужен --asset существующего материала (до160 UTF-16 символов).')
        body={'asset_id':args.asset,'expected_revision':args.revision,'fingerprint':args.sha256}
        if command=='backend-media':return client.call('POST','/api/backend/v1/media-info',body)
        if type(args.frame) is not int or not 0<=args.frame<=2**31-1:raise ClientError('Нужен неотрицательный --frame в диапазоне Int32.')
        output=_backend_output_path(args.output)
        body['frame']=args.frame
        return _backend_save_frame(client.call('POST','/api/backend/v1/frame',body),output)
    if command == 'doctor':
        health = client.call('GET', '/api/health')
        if health.get('app') != 'true-moon-studio':
            raise ClientError('На указанном адресе работает другое приложение.')
        session = client.call('GET', '/api/bootstrap')
        return {'health': health, 'projects': session['projects'], 'profile': session['profile'],
                'generation_available': session['generation_available'], 'agent': session.get('agent')}
    if command == 'projects':
        return client.call('GET', '/api/projects')
    if not args.project:
        raise ClientError('Для этой команды укажите --project перед именем команды.')
    base = '/api/projects/' + parse.quote(args.project, safe='')
    if command=='exports':return client.call('GET',base+'/exports')
    if command=='export':
        payload={'expected_revision':args.revision,'profile':args.profile}
        if args.client_key:payload['client_key']=args.client_key
        return client.call('POST',base+'/exports',payload)
    if command in ['export-status','export-stop']:
        path=base+'/exports/'+parse.quote(args.id,safe='')
        return client.call('GET',path) if command=='export-status' else client.call('POST',path+'/stop',{'expected_revision':args.revision})
    if command in ['scenario','timeline']:
        endpoint=base+'/'+command
        if not args.file:return client.call('GET',endpoint)
        if args.revision is None:raise ClientError('Для записи сценария укажите --revision.')
        operation=json.loads(file_text(args.file))
        if not isinstance(operation,dict) or set(operation)!={'action','payload'}:raise ClientError('Ожидается JSON с action и payload.')
        return client.call('POST',endpoint,{'expected_revision':args.revision,**operation})
    if command == 'context':
        query = {key: getattr(args, key) for key in ['block', 'segment', 'item'] if getattr(args, key)}
        suffix = '?' + parse.urlencode(query) if query else ''
        return client.call('GET', base + '/agent/context' + suffix)
    if command == 'brief':
        if not args.file:
            return client.call('GET', base + '/brief')
        if args.revision is None:
            raise ClientError('Для записи карточки укажите --revision из актуального контекста.')
        return client.call('PATCH', base + '/brief', {'expected_revision': args.revision, 'brief': json.loads(file_text(args.file))})
    if command == 'requests':
        return client.call('GET', base + '/agent/requests')
    if command == 'request':
        payload = {'expected_revision': args.revision, 'goal': file_text(args.goal_file), 'mode': args.mode, 'block_id': args.block}
        if args.scope_file: payload['scope'] = json.loads(file_text(args.scope_file))
        if args.client_key: payload['client_key'] = args.client_key
        return task_summary(client.call('POST', base + '/agent/requests', payload))
    if command in ['task', 'claim', 'finish', 'fail', 'cancel']:
        path = base + '/agent/requests/' + parse.quote(args.id, safe='')
        if command == 'task':
            return client.call('GET', path)
        payload = {'expected_revision': args.revision, 'action': command,
                   'result': file_text(args.result_file) if command in ['finish', 'fail'] else ''}
        if command == 'finish' and args.target_file: payload['result_target'] = json.loads(file_text(args.target_file))
        if command == 'finish' and args.proposal_file: payload['proposal'] = json.loads(file_text(args.proposal_file))
        return task_summary(client.call('POST', path, payload))
    if command in ['patch-block', 'prepare', 'continue']:
        context = client.call('GET', base + '/agent/context?' + parse.urlencode({'block': args.block}))
        bid = context['block']['id']
        if command == 'continue':
            return client.call('POST', base + '/operations', {'expected_revision': args.revision, 'operation': 'create_continuation',
                               'payload': {'block_id': bid, 'take_id': args.take}})
        path = base + '/blocks/' + parse.quote(bid, safe='')
        if command == 'prepare':
            return client.call('POST', path + '/prepare', {'expected_revision': args.revision})
        return client.call('PATCH', path, {'expected_revision': args.revision, 'block': json.loads(file_text(args.file))})
    if command in ['launch', 'stop', 'job']:
        path = base + '/jobs/' + parse.quote(args.id, safe='')
        return client.call('GET', path) if command == 'job' else client.call('POST', path + '/' + command, {})
    raise ClientError('Неизвестная команда.')


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        client = BackendClient(args.url,args.token) if args.command.startswith('backend-') else StudioClient(args.url,args.actor)
        result = execute(client, args)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (ClientError, OSError, ValueError) as exc:
        print(json.dumps({'error': str(exc), 'status': getattr(exc, 'status', None), 'automatic_retry': False}, ensure_ascii=False), file=sys.stderr)
        return 3 if getattr(exc, 'status', None) == 409 else 1


if __name__ == '__main__':
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(encoding='utf-8')
    if hasattr(sys.stderr, 'reconfigure'): sys.stderr.reconfigure(encoding='utf-8')
    raise SystemExit(main())

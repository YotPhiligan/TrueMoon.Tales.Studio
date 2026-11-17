"""Small standard-library CLI for an external local agent. Never retries writes."""
from __future__ import annotations
import argparse, json, os, sys
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
                snapshot=json.loads(encoded)
            if not isinstance(snapshot,dict) or type(snapshot.get('api_version')) is not int or snapshot['api_version']!=1 or type(snapshot.get('schema_version')) is not int or snapshot['schema_version']!=2:
                raise ValueError('Unsupported backend envelope')
            project=snapshot.get('project'); fingerprint=snapshot.get('fingerprint')
            if not isinstance(project,dict) or type(project.get('contract_version')) is not int or project['contract_version']!=1 or type(project.get('revision')) is not int or project['revision']<0 or not isinstance(project.get('id'),str):
                raise ValueError('Invalid backend project')
            if not isinstance(fingerprint,str) or len(fingerprint)!=64 or any(c not in '0123456789abcdefABCDEF' for c in fingerprint):
                raise ValueError('Invalid backend fingerprint')
            return snapshot
        except error.HTTPError as exc:
            try:message=json.loads(exc.read(4096)).get('error',str(exc))
            except (ValueError,OSError,AttributeError):message=str(exc)
            raise ClientError(message,exc.code) from exc
        except (OSError,ValueError) as exc:
            raise ClientError('Ответ backend не получен или недостоверен. Запись автоматически не повторяется; перечитайте проект для сверки результата.') from exc


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
    if command in ['backend-project','backend-move']:
        if args.project:raise ClientError('Backend project выбирается владельцем --serve-project, без --project в запросе.')
        if command=='backend-project':return client.call('GET','/api/backend/v1/project')
        if args.revision<0 or len(args.sha256)!=64 or any(c not in '0123456789abcdefABCDEF' for c in args.sha256):
            raise ClientError('Нужны observed --revision и 64 hex --sha256 из backend-project.')
        return client.call('POST','/api/backend/v1/move-clip',{'clip_id':args.clip,'start_frame':args.start_frame,
            'expected_revision':args.revision,'fingerprint':args.sha256})
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

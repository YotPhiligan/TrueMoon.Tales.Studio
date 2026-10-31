"""Small standard-library CLI for an external local agent. Never retries writes."""
from __future__ import annotations
import argparse, json, sys
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


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--url', default='http://127.0.0.1:8787')
    p.add_argument('--project', help='ID проекта из команды projects')
    p.add_argument('--actor', default='agent:cli')
    commands = p.add_subparsers(dest='command', required=True)
    for name in ['doctor', 'projects', 'requests', 'exports']:
        commands.add_parser(name)
    sub=commands.add_parser('export');sub.add_argument('--revision',type=int,required=True);sub.add_argument('--profile',choices=['draft','project'],default='draft');sub.add_argument('--client-key')
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
        if name == 'finish': sub.add_argument('--target-file')
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
        result = execute(StudioClient(args.url, args.actor), args)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (ClientError, OSError, ValueError) as exc:
        print(json.dumps({'error': str(exc), 'status': getattr(exc, 'status', None), 'automatic_retry': False}, ensure_ascii=False), file=sys.stderr)
        return 3 if getattr(exc, 'status', None) == 409 else 1


if __name__ == '__main__':
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(encoding='utf-8')
    if hasattr(sys.stderr, 'reconfigure'): sys.stderr.reconfigure(encoding='utf-8')
    raise SystemExit(main())

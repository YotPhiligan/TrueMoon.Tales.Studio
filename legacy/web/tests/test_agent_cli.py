import io
import json
from contextlib import redirect_stderr, redirect_stdout
from unittest.mock import Mock, call, patch
from urllib.error import URLError

from agent_fixtures import AgentFixture, AgentHttpFixture
from studio import agent
import studio_agent as cli


class AgentCliRouteTests(AgentFixture):
    def setUp(self):
        super().setUp()
        self.goal = self.base / 'goal text.txt'
        self.goal.write_text('\ufeffУскорить\nснижение', encoding='utf-8')
        self.result_file = self.base / 'result text.txt'
        self.result_file.write_text('\ufeffПроверено\n2 детали', encoding='utf-8')
        self.patch_file = self.base / 'patch.json'
        self.patch_file.write_text(json.dumps({'notes': 'Правка'}), encoding='utf-8')
        self.brief_file = self.base / 'brief.json'
        self.brief_file.write_text(json.dumps({'synopsis': 'Тизер'}), encoding='utf-8')

    def test_every_cli_command_maps_to_explicit_route_payload_and_separate_prepare_launch(self):
        base = '/api/projects/project%20with%20space'
        selector = 'CL-001 A'
        context_path = base + '/agent/context?block=CL-001+A'
        block_path = base + '/blocks/block%2Fresolved'
        rid = 'req-0123456789abcdef'
        cases = [(['projects'], [call('GET', '/api/projects')]),
                 (['context', '--block', selector], [call('GET', context_path)]),
                 (['requests'], [call('GET', base + '/agent/requests')]),
                 (['task', '--id', rid], [call('GET', base + '/agent/requests/' + rid)]),
                 (['brief'], [call('GET', base + '/brief')]),
                 (['brief', '--file', str(self.brief_file), '--revision', '7'], [call('PATCH', base + '/brief', {'expected_revision': 7, 'brief': {'synopsis': 'Тизер'}})]),
                 (['request', '--block', selector, '--goal-file', str(self.goal), '--mode', 'apply', '--revision', '7'],
                  [call('POST', base + '/agent/requests', {'expected_revision': 7, 'goal': 'Ускорить\nснижение', 'mode': 'apply', 'block_id': selector})]),
                 (['patch-block', '--block', selector, '--file', str(self.patch_file), '--revision', '7'],
                  [call('GET', context_path), call('PATCH', block_path, {'expected_revision': 7, 'block': {'notes': 'Правка'}})]),
                 (['prepare', '--block', selector, '--revision', '7'], [call('GET', context_path), call('POST', block_path + '/prepare', {'expected_revision': 7})]),
                 (['continue', '--block', selector, '--take', 'take/full', '--revision', '7'],
                  [call('GET', context_path), call('POST', base + '/operations', {'expected_revision': 7, 'operation': 'create_continuation', 'payload': {'block_id': 'block/resolved', 'take_id': 'take/full'}})])]
        for action in ['claim', 'finish', 'fail', 'cancel']:
            argv = [action, '--id', rid, '--revision', '7']
            result = ''
            if action in ['finish', 'fail']:
                argv += ['--result-file', str(self.result_file)]
                result = 'Проверено\n2 детали'
            cases.append((argv, [call('POST', base + '/agent/requests/' + rid, {'expected_revision': 7, 'action': action, 'result': result})]))
        for action in ['launch', 'stop', 'job']:
            path = base + '/jobs/job-0123456789abcdef'
            expected = call('GET', path) if action == 'job' else call('POST', path + '/' + action, {})
            cases.append(([action, '--id', 'job-0123456789abcdef'], [expected]))
        response = {'id': rid, 'state': 'pending', 'context': {'snapshot_sha256': 'fixture-sha', 'private': 'FULL_CONTEXT'}}
        def route(method, path, *body):
            return {'block': {'id': 'block/resolved'}} if '/agent/context' in path else response
        for argv, expected in cases:
            with self.subTest(command=argv[0]):
                client = Mock()
                client.call.side_effect = route
                args = cli.parser().parse_args(['--project', 'project with space', *argv])
                result = cli.execute(client, args)
                self.assertEqual(expected, client.call.call_args_list)
                if argv[0] in ['request', 'claim', 'finish', 'fail', 'cancel']:
                    self.assertNotIn('context', result)
                    self.assertEqual('fixture-sha', result['snapshot_sha256'])
                elif argv[0] == 'task':
                    self.assertEqual(response, result)

    def test_doctor_sanitizes_token_and_rejects_other_app_without_project_writes(self):
        client = Mock()
        client.call.side_effect = [{'app': 'true-moon-studio', 'ok': True},
                                   {'token': 'PRIVATE_SESSION_TOKEN', 'projects': [], 'profile': {'name': 'Omni'}, 'generation_available': True, 'agent': {'context_version': 2}}]
        result = cli.execute(client, cli.parser().parse_args(['doctor']))
        self.assertNotIn('PRIVATE_SESSION_TOKEN', json.dumps(result))
        self.assertEqual({'name': 'Omni'}, result['profile'])
        self.assertEqual([call('GET', '/api/health'), call('GET', '/api/bootstrap')], client.call.call_args_list)
        client.call.reset_mock()
        client.call.side_effect = [{'app': 'foreign'}]
        with self.assertRaises(cli.ClientError):
            cli.execute(client, cli.parser().parse_args(['doctor']))
        self.assertEqual([call('GET', '/api/health')], client.call.call_args_list)

    def test_cli_requires_project_and_explicit_revision_before_network(self):
        client = Mock()
        with self.assertRaises(cli.ClientError):
            cli.execute(client, cli.parser().parse_args(['context']))
        client.call.assert_not_called()
        with self.assertRaises(cli.ClientError):
            cli.execute(client, cli.parser().parse_args(['--project', self.pid, 'brief', '--file', str(self.brief_file)]))
        client.call.assert_not_called()
        for argv in [['request', '--goal-file', str(self.goal)], ['claim', '--id', 'req-x'], ['prepare', '--block', 'CL-T01']]:
            with self.subTest(command=argv[0]), redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
                cli.parser().parse_args(['--project', self.pid, *argv])
            self.assertEqual(2, caught.exception.code)

    def test_client_rejects_nonlocal_or_credential_path_addresses_before_network(self):
        for url in ['https://localhost:8787', 'http://remote.invalid', 'http://user:pass@localhost:8787',
                    'http://127.0.0.1:8787/api', 'http://localhost:8787?query=x', 'http://localhost:8787#fragment']:
            with self.subTest(url=url), patch('studio_agent.request.urlopen') as network, self.assertRaises(cli.ClientError):
                cli.StudioClient(url)
            network.assert_not_called()

    def test_client_network_or_bad_json_after_write_never_retries_and_uses_actor_token(self):
        for effect in [URLError('connection reset'), io.BytesIO(b'{invalid JSON')]:
            client = cli.StudioClient('http://127.0.0.1:12345', actor='agent:fixed')
            client.token = 'FIXTURE_TOKEN'
            kwargs = {'side_effect': effect} if isinstance(effect, Exception) else {'return_value': effect}
            with self.subTest(effect=type(effect).__name__), patch('studio_agent.request.urlopen', **kwargs) as network, self.assertRaises(cli.ClientError):
                client.call('POST', '/api/projects/p/agent/requests', {'actor': 'ignored', 'goal': 'Task'})
            self.assertEqual(1, network.call_count)
            request = network.call_args.args[0]
            self.assertEqual('POST', request.method)
            self.assertEqual('FIXTURE_TOKEN', request.get_header('X-studio-token'))
            self.assertEqual({'actor': 'agent:fixed', 'goal': 'Task'}, json.loads(request.data))


class AgentCliIntegrationTests(AgentHttpFixture):
    def run_cli(self, argv):
        output, errors = io.StringIO(), io.StringIO()
        with redirect_stdout(output), redirect_stderr(errors):
            status = cli.main(['--url', self.url, '--project', self.pid, '--actor', 'agent:cli-test', *argv])
        return status, json.loads(output.getvalue()) if output.getvalue() else None, json.loads(errors.getvalue()) if errors.getvalue() else None

    def test_cli_request_task_claim_finish_round_trip_is_compact_and_project_read_only(self):
        goal, result_file = self.base / 'goal.txt', self.base / 'result.txt'
        goal.write_text('Проверить динамику', encoding='utf-8')
        result_file.write_text('Проверено без генерации', encoding='utf-8')
        before = self.project_state()
        status, created, error = self.run_cli(['request', '--block', 'CL-T01', '--goal-file', str(goal), '--revision', str(self.current()['revision'])])
        self.assertEqual((0, None), (status, error))
        self.assertEqual('pending', created['state'])
        self.assertNotIn('context', created)
        self.assertRegex(created['snapshot_sha256'], r'^[0-9a-f]{64}$')
        status, full, _ = self.run_cli(['task', '--id', created['id']])
        self.assertEqual(0, status)
        self.assertEqual(self.bid, full['context']['block']['id'])
        self.assertEqual(created['snapshot_sha256'], full['context']['snapshot_sha256'])
        status, running, _ = self.run_cli(['claim', '--id', created['id'], '--revision', '1'])
        self.assertEqual(0, status)
        self.assertEqual(('running', 2, 'agent:cli-test'), (running['state'], running['revision'], running['owner']))
        status, completed, _ = self.run_cli(['finish', '--id', created['id'], '--revision', '2', '--result-file', str(result_file)])
        self.assertEqual(0, status)
        self.assertEqual(('completed', 3, 'Проверено без генерации'), (completed['state'], completed['revision'], completed['result']))
        self.assertEqual(before, self.project_state())
        self.assert_no_generation()

    def test_cli_conflict_exits_three_without_refetch_retry_or_mutating_request(self):
        created = agent.create_request(self.store, self.pid, self.current()['revision'], 'Task')
        agent.update_request(self.store, self.pid, created['id'], 1, 'claim', 'agent:owner')
        before, requests = self.project_state(), self.request_state()
        status, output, error = self.run_cli(['cancel', '--id', created['id'], '--revision', '1'])
        self.assertEqual(3, status)
        self.assertIsNone(output)
        self.assertEqual((409, False), (error['status'], error['automatic_retry']))
        self.assertEqual([('GET', '/api/bootstrap'), ('POST', f'/api/projects/{self.pid}/agent/requests/{created["id"]}')], self.http_calls)
        self.assertEqual(requests, self.request_state())
        self.assertEqual(before, self.project_state())
        self.assert_no_generation()

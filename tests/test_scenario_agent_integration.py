import io
import json
from contextlib import redirect_stdout, redirect_stderr
from unittest.mock import Mock, call

from scenario_fixtures import ScenarioFixture
from agent_fixtures import AgentHttpFixture
import studio_agent as cli


class ScenarioAgentIntegrationTests(ScenarioFixture, AgentHttpFixture):
    def scope(self):
        return {'segment_id': 's1', 'intent': 'prepare_block', 'components': ['prompt', 'guides'],
                'draft': {'title': 'Новый блок', 'description': 'Резкое снижение', 'duration_frames': 12, 'after_item_id': 'c1'}}

    def test_scoped_http_context_and_client_key_retry_preserve_current_project_and_snapshot(self):
        before, sources = self.project_state(), self.source_state()
        status, context = self.http('GET', f'/api/projects/{self.pid}/agent/context?segment=s1&item=c1')
        self.assertEqual(200, status); self.assertEqual(('c1', self.bid, 2), (context['scenario_item']['id'], context['block']['id'], context['source_usage_count']))
        path = f'/api/projects/{self.pid}/agent/requests'
        payload = {'expected_revision': self.current()['revision'], 'goal': 'Подготовить', 'mode': 'apply',
                   'scope': self.scope(), 'client_key': 'http_scoped_submit_1'}
        status, created = self.http('POST', path, payload)
        self.assertEqual(201, status); self.assertEqual(self.scope()['draft'], created['scope']['draft'])
        self.assertEqual(['c1', 'c2'], [n['item']['id'] for n in created['context']['scenario_neighbors']])
        self.assertEqual(before, self.project_state()); self.assertEqual(sources, self.source_state())
        self.save({'notes': 'New revision'})
        before, records = self.project_state(), self.request_state()
        status, replay = self.http('POST', path, payload)
        self.assertEqual(201, status); self.assertEqual(created, replay)
        conflict, _ = self.http('POST', path, {**payload, 'goal': 'Changed'})
        self.assertEqual(409, conflict); self.assertEqual(records, self.request_state()); self.assertEqual(before, self.project_state())
        self.assert_no_generation()

    def test_cli_new_scope_and_target_flags_map_json_files_to_exact_api_fields(self):
        scope, goal, result, target = self.base / 'scope.json', self.base / 'goal.txt', self.base / 'result.txt', self.base / 'target.json'
        scope.write_text('\ufeff' + json.dumps(self.scope()), encoding='utf-8'); goal.write_text('Подготовить', encoding='utf-8')
        result.write_text('Готово', encoding='utf-8'); target.write_text(json.dumps({'segment_id': 's1', 'item_id': 'fresh'}))
        client = Mock(); client.call.return_value = {'id': 'req-test', 'context': {'snapshot_sha256': 'sha'}}
        args = cli.parser().parse_args(['--project', self.pid, 'context', '--segment', 's1', '--item', 'c1'])
        cli.execute(client, args)
        self.assertEqual([call('GET', f'/api/projects/{self.pid}/agent/context?segment=s1&item=c1')], client.call.call_args_list)
        client.call.reset_mock()
        args = cli.parser().parse_args(['--project', self.pid, 'request', '--goal-file', str(goal), '--scope-file', str(scope), '--client-key', 'cli_scope_submit_1', '--mode', 'apply', '--revision', '7'])
        cli.execute(client, args)
        self.assertEqual([call('POST', f'/api/projects/{self.pid}/agent/requests', {'expected_revision': 7, 'goal': 'Подготовить', 'mode': 'apply', 'block_id': None,
                          'scope': self.scope(), 'client_key': 'cli_scope_submit_1'})], client.call.call_args_list)
        client.call.reset_mock()
        args = cli.parser().parse_args(['--project', self.pid, 'finish', '--id', 'req-test', '--result-file', str(result), '--target-file', str(target), '--revision', '2'])
        cli.execute(client, args)
        self.assertEqual([call('POST', f'/api/projects/{self.pid}/agent/requests/req-test', {'expected_revision': 2, 'action': 'finish', 'result': 'Готово',
                          'result_target': {'segment_id': 's1', 'item_id': 'fresh'}})], client.call.call_args_list)

    def test_real_cli_scoped_request_external_scenario_edit_and_target_finish_round_trip(self):
        scope, goal, result, target = self.base / 'scope.json', self.base / 'goal.txt', self.base / 'result.txt', self.base / 'target.json'
        scope.write_text(json.dumps(self.scope()), encoding='utf-8'); goal.write_text('Подготовить', encoding='utf-8'); result.write_text('Материалы готовы', encoding='utf-8')
        def invoke(argv):
            output, errors = io.StringIO(), io.StringIO()
            with redirect_stdout(output), redirect_stderr(errors):
                status = cli.main(['--url', self.url, '--project', self.pid, '--actor', 'agent:scoped', *argv])
            self.assertEqual('', errors.getvalue()); self.assertEqual(0, status)
            return json.loads(output.getvalue())
        before = self.project_state()
        task = invoke(['request', '--goal-file', str(goal), '--scope-file', str(scope), '--client-key', 'real_cli_submit_1', '--mode', 'apply', '--revision', str(self.current()['revision'])])
        self.assertEqual('prepare_block', task['scope']['intent']); self.assertNotIn('context', task)
        running = invoke(['claim', '--id', task['id'], '--revision', '1'])
        self.assertEqual('agent:scoped', running['owner']); self.assertEqual(before, self.project_state())
        status, project = self.http('POST', f'/api/projects/{self.pid}/scenario', {'expected_revision': self.current()['revision'], 'action': 'add_item',
                                  'payload': {'segment_id': 's1', 'title': 'Новый блок', 'description': 'Резкое снижение', 'duration_frames': 12, 'index': 1}})
        self.assertEqual(200, status)
        item = project['timeline']['scenario']['segments'][0]['items'][1]
        target.write_text(json.dumps({'segment_id': 's1', 'item_id': item['id']}))
        before = self.project_state()
        finished = invoke(['finish', '--id', task['id'], '--revision', '2', '--result-file', str(result), '--target-file', str(target)])
        self.assertEqual({'segment_id': 's1', 'item_id': item['id'], 'block_id': item['block_id']}, finished['result_target'])
        self.assertEqual('completed', finished['state']); self.assertEqual(before, self.project_state())
        context = invoke(['context', '--segment', 's1', '--item', item['id']])
        self.assertEqual(item['block_id'], context['block']['id']); self.assert_no_generation()

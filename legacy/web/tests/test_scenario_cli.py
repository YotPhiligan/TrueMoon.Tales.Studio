import io
import json
from contextlib import redirect_stdout, redirect_stderr
from unittest.mock import Mock, call
from scenario_fixtures import ScenarioFixture
from agent_fixtures import AgentHttpFixture
import studio_agent as cli


class ScenarioCliTests(ScenarioFixture, AgentHttpFixture):
    def test_cli_scenario_get_and_json_operation_use_exact_routes_and_explicit_revision(self):
        client = Mock()
        args = cli.parser().parse_args(['--project', 'project space', 'scenario'])
        cli.execute(client, args)
        self.assertEqual([call('GET', '/api/projects/project%20space/scenario')], client.call.call_args_list)
        operation = {'action': 'update_segment', 'payload': {'id': 's1', 'name': 'Новая сцена'}}
        file = self.base / 'scenario operation.json'
        file.write_text('\ufeff' + json.dumps(operation), encoding='utf-8')
        client.call.reset_mock()
        args = cli.parser().parse_args(['--project', 'project space', 'scenario', '--file', str(file), '--revision', '7'])
        cli.execute(client, args)
        self.assertEqual([call('POST', '/api/projects/project%20space/scenario', {'expected_revision': 7, **operation})], client.call.call_args_list)

    def test_cli_scenario_write_without_revision_or_wrong_operation_shape_never_calls_network(self):
        file = self.base / 'scenario.json'
        file.write_text('{}')
        client = Mock()
        args = cli.parser().parse_args(['--project', self.pid, 'scenario', '--file', str(file)])
        with self.assertRaises(cli.ClientError):
            cli.execute(client, args)
        client.call.assert_not_called()
        for value in [[], {}, {'action': 'add_segment'}, {'action': 'remove_segment', 'payload': {}, 'unexpected': True}]:
            file.write_text(json.dumps(value))
            args = cli.parser().parse_args(['--project', self.pid, 'scenario', '--file', str(file), '--revision', '7'])
            with self.subTest(value=value), self.assertRaises(cli.ClientError):
                cli.execute(client, args)
            client.call.assert_not_called()

    def test_cli_scenario_round_trip_is_real_local_http_without_automatic_generation(self):
        file = self.base / 'scenario.json'
        operation = {'action': 'update_segment', 'payload': {'id': 's1', 'name': 'Новая сцена'}}
        file.write_text(json.dumps(operation), encoding='utf-8')
        output, errors = io.StringIO(), io.StringIO()
        before = self.current()
        with redirect_stdout(output), redirect_stderr(errors):
            status = cli.main(['--url', self.url, '--project', self.pid, '--actor', 'agent:scenario', 'scenario', '--file', str(file), '--revision', str(before['revision'])])
        self.assertEqual(0, status)
        self.assertEqual('', errors.getvalue())
        project = json.loads(output.getvalue())
        self.assertEqual((before['revision'] + 1, 'Новая сцена'), (project['revision'], project['timeline']['scenario']['segments'][0]['name']))
        self.assertEqual('agent:scenario', project['activity'][-1]['actor'])
        self.assertEqual(before['blocks'], project['blocks'])
        output = io.StringIO()
        with redirect_stdout(output):
            status = cli.main(['--url', self.url, '--project', self.pid, 'scenario'])
        self.assertEqual(0, status)
        self.assertIs(json.loads(output.getvalue())['active'], True)
        self.assert_no_generation()

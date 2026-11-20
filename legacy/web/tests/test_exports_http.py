import http.client
import json
import threading
from types import SimpleNamespace
from unittest.mock import Mock, patch

from test_exports import ExportFixture
from studio.exports import Exports
from studio.server import LocalServer, handler_for


class ExportHttpTests(ExportFixture):
    def setUp(self):
        super().setUp()
        self.exports = Exports(self.store)
        static = self.base / 'static';static.mkdir();(static / 'index.html').write_text('<title>Isolated export test</title>')
        handler = handler_for(self.store, self.jobs, static, self.exports);handler.log_message = lambda *args: None
        self.server = LocalServer(('127.0.0.1', 0), handler);self.port = self.server.server_address[1]
        self.server_thread = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .02}, daemon=True)
        self.server_thread.start();self.addCleanup(self.close_server)
        self.popen = self.enterContext(patch('studio.exports.subprocess.Popen', return_value=Mock(pid=1001)))
        process = self.enterContext(patch('psutil.Process'));process.return_value.create_time.return_value = 42
        self.enterContext(patch('studio.exports.threading', SimpleNamespace(Thread=Mock())))
        self.token = json.loads(self.http('GET', '/api/bootstrap', auth=False)[2])['token']
        self.url = f'/api/projects/{self.pid}/exports'

    def close_server(self):
        self.server.shutdown();self.server.server_close();self.server_thread.join(2)

    def http(self, method, path, body=None, auth=True, headers=None):
        fields = {'Content-Type': 'application/json', **(headers or {})}
        if auth:fields['X-Studio-Token'] = self.token
        conn = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        try:
            conn.request(method, path, json.dumps(body) if body is not None else None, fields)
            response = conn.getresponse();return response.status, dict(response.getheaders()), response.read()
        finally:conn.close()

    def create(self):
        status, _, data = self.http('POST', self.url, {'expected_revision': self.current()['revision'], 'client_key': 'http-export-key-001'})
        self.assertEqual(202, status);return json.loads(data)

    def test_export_routes_enforce_token_revision_and_list_status_without_project_or_generation_changes(self):
        before, sources = self.project_state(), self.source_state()
        payload = {'expected_revision': self.current()['revision']}
        self.assertEqual(403, self.http('POST', self.url, payload, auth=False)[0]);self.popen.assert_not_called()
        self.assertEqual(409, self.http('POST', self.url, {'expected_revision': payload['expected_revision'] - 1})[0])
        row = self.create();self.assertEqual('running', row['state'])
        status, _, data = self.http('GET', self.url);self.assertEqual(200, status);self.assertEqual([row['id']], [r['id'] for r in json.loads(data)])
        status, _, data = self.http('GET', self.url + '/' + row['id']);self.assertEqual(200, status);self.assertEqual(row, json.loads(data))
        self.popen.assert_called_once();self.assertEqual(before, self.project_state());self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_readiness_reports_multiple_blockers_and_targets_without_starting_export(self):
        ready_status, _, body = self.http('GET', self.url + '/readiness', auth=False)
        self.assertEqual(200, ready_status)
        self.assertTrue(json.loads(body)['ready'])

        self.activate()
        p = self.edit('update_item', segment_id='s1', id='c2', duration_frames=None, take_id=None)
        self.store.resolve_asset(p, self.take['video_asset_id']).unlink()
        before = self.project_state()
        with patch.object(self.store, 'render_title_layers') as render_titles:
            status, _, body = self.http('GET', self.url + '/readiness', auth=False)
            self.assertEqual(200, status)
            report = json.loads(body)
            self.assertFalse(report['ready'])
            self.assertEqual(p['revision'], report['project_revision'])
            issues = {row['code']: row for row in report['issues']}
            self.assertEqual({'unknown_duration', 'missing_take', 'unavailable_asset'}, set(issues))
            self.assertEqual(('s1', 'c2'), (issues['unknown_duration']['segment_id'], issues['unknown_duration']['item_id']))
            self.assertEqual(('clip', 'c1', 12), tuple(issues['unavailable_asset'][field] for field in ('kind', 'id', 'frame')))
            status, _, body = self.http('POST', self.url, {'expected_revision': p['revision']})
            self.assertEqual(422, status)
            self.assertEqual(report['issues'], json.loads(body)['details'])
            render_titles.assert_not_called()
        self.assertEqual(before, self.project_state())
        self.assertEqual([], self.exports.list(self.pid))
        self.popen.assert_not_called()
        self.assert_no_generation()

    def test_stop_route_uses_export_revision_and_requires_session(self):
        before = self.project_state();row = self.create();url = self.url + '/' + row['id'] + '/stop'
        self.assertEqual(403, self.http('POST', url, {'expected_revision': row['revision']}, auth=False)[0])
        self.assertEqual(409, self.http('POST', url, {'expected_revision': row['revision'] - 1})[0])
        status, _, data = self.http('POST', url, {'expected_revision': row['revision']})
        self.assertEqual(202, status);self.assertEqual('stopping', json.loads(data)['state']);self.assertTrue(self.exports.stop_event.is_set())
        self.assertEqual(before, self.project_state());self.assert_no_generation()

    def test_result_video_is_unavailable_until_completed_then_range_and_head_match_bytes(self):
        before = self.project_state();row = self.create();url = self.url + '/' + row['id'] + '/video'
        self.assertEqual(409, self.http('GET', url)[0])
        folder = self.exports.folder(self.pid, row['id']);body = bytes(range(64));(folder / 'Draft.mp4').write_bytes(body)
        self.exports.save(folder, row, state='completed')
        status, headers, data = self.http('GET', url, headers={'Range': 'bytes=9-20'})
        self.assertEqual(206, status);self.assertEqual(body[9:21], data);self.assertEqual('bytes 9-20/64', headers['Content-Range'])
        status, headers, data = self.http('HEAD', url)
        self.assertEqual(200, status);self.assertEqual('64', headers['Content-Length']);self.assertEqual(b'', data)
        self.assertEqual('video/mp4', headers['Content-Type']);self.assertEqual(before, self.project_state());self.assert_no_generation()

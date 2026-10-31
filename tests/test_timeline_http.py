"""Timeline HTTP contract over an ephemeral, isolated loopback server."""
import http.client
import json
import threading
from unittest.mock import patch

from fixtures import Fixture
from studio.server import LocalServer, handler_for
from studio.model import digest


class TimelineHttpTests(Fixture):
    def setUp(self):
        super().setUp()
        static = self.base / 'static'
        static.mkdir()
        (static / 'index.html').write_text('<!doctype html><title>Timeline fixture</title>')
        handler = handler_for(self.store, self.jobs, static)
        handler.log_message = lambda *args: None
        self.server = LocalServer(('127.0.0.1', 0), handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .02}, daemon=True)
        self.thread.start()
        self.addCleanup(self.close_server)
        _, body = self.request('GET', '/api/bootstrap')
        self.token = body['token']
        self.path = f'/api/projects/{self.pid}/timeline'

    def close_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)

    def request(self, method, path, payload=None, authorized=False):
        headers = {'Content-Type': 'application/json'}
        if authorized:
            headers['X-Studio-Token'] = self.token
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        try:
            connection.request(method, path, body=json.dumps(payload) if payload is not None else None, headers=headers)
            response = connection.getresponse()
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
        finally:
            connection.close()

    def post(self, action, payload, revision=None, actor='author', authorized=True):
        return self.request('POST', self.path, {
            'expected_revision': self.current()['revision'] if revision is None else revision,
            'action': action, 'payload': payload, 'actor': actor}, authorized)

    def test_get_legacy_timeline_returns_empty_without_writing_or_starting_jobs(self):
        before = self.store.location(self.pid).read_bytes()
        with patch.object(self.jobs, 'prepare') as prepare, patch.object(self.jobs, 'launch') as launch:
            status, timeline = self.request('GET', self.path)
        self.assertEqual(200, status)
        self.assertEqual({'version': 1, 'fps': 24, 'segments': [], 'clips': [], 'markers': [], 'music': None, 'clip_audio': False, 'undo': [], 'redo': []}, timeline)
        self.assertEqual(before, self.store.location(self.pid).read_bytes())
        prepare.assert_not_called()
        launch.assert_not_called()

    def test_post_returns_full_project_with_actor_and_get_returns_same_timeline(self):
        original = self.current()
        status, project = self.post('add_segment', {'name': 'Action', 'start_frame': 24, 'duration_frames': 48}, actor='agent')
        self.assertEqual(200, status)
        self.assertEqual(self.pid, project['id'])
        self.assertEqual(original['revision'] + 1, project['revision'])
        self.assertEqual('agent', project['activity'][-1]['actor'])
        self.assertEqual(original['blocks'], project['blocks'])
        status, timeline = self.request('GET', self.path)
        self.assertEqual(200, status)
        self.assertEqual(project['timeline'], timeline)
        self.assertEqual((24, 48), tuple(timeline['segments'][0][k] for k in ['start_frame', 'duration_frames']))

    def test_missing_token_stale_revision_and_invalid_frames_never_commit(self):
        revision = self.current()['revision']
        denied, _ = self.post('add_marker', {'name': 'Denied', 'frame': 0}, authorized=False)
        self.assertEqual(403, denied)
        self.assertEqual(revision, self.current()['revision'])
        status, _ = self.post('add_marker', {'name': 'Winner', 'frame': 24})
        self.assertEqual(200, status)
        before = self.store.location(self.pid).read_bytes()
        history = self.store.history(self.pid)
        stale, _ = self.post('add_marker', {'name': 'Lost', 'frame': 48}, revision=revision)
        invalid, _ = self.post('add_marker', {'name': 'Invalid', 'frame': 1.5})
        invalid_payload, _ = self.post('add_marker', [])
        self.assertEqual(409, stale)
        self.assertEqual(422, invalid)
        self.assertEqual(422, invalid_payload)
        self.assertEqual(before, self.store.location(self.pid).read_bytes())
        self.assertEqual(history, self.store.history(self.pid))

    def test_placeholder_edit_and_undo_through_http_do_not_start_generation(self):
        with patch.object(self.jobs, 'prepare') as prepare, patch.object(self.jobs, 'launch') as launch, patch.object(self.jobs, 'command') as command:
            status, p = self.post('add_segment', {'name': 'Draft', 'start_frame': 0, 'duration_frames': 48})
            self.assertEqual(200, status)
            sid = p['timeline']['segments'][0]['id']
            status, p = self.post('add_clip', {'segment_id': sid, 'block_id': self.bid, 'take_id': None,
                                             'start_frame': 0, 'duration_frames': 48, 'source_in_frame': 0})
            self.assertEqual(200, status)
            self.assertEqual(1, len(p['timeline']['clips']))
            status, p = self.post('undo', {})
            self.assertEqual(200, status)
            self.assertEqual([], p['timeline']['clips'])
            self.assertEqual([sid], [s['id'] for s in p['timeline']['segments']])
        prepare.assert_not_called()
        launch.assert_not_called()
        command.assert_not_called()
        self.assertFalse((self.store.location(self.pid).parent / 'Jobs').exists())

    def test_media_info_get_is_read_only_and_never_starts_generation(self):
        take, _ = self.add_take()
        p = self.current()
        self.block(p)['takes'][0]['video_info'] = {}
        self.store.commit(p, p['revision'], 'fixture', 'Legacy metadata')
        video = self.store.resolve_asset(self.current(), take['video_asset_id'])
        video_hash = digest(video)
        before = self.store.location(self.pid).read_bytes()
        history = self.store.history(self.pid)
        info = {'frames': 60, 'fps': 24.0, 'seconds': 2.5, 'width': 32, 'height': 24, 'silent': True, 'metadata_source': 'media_header'}
        with patch.object(self.store, 'probe_video', return_value=info) as probe, patch.object(self.jobs, 'prepare') as prepare, patch.object(self.jobs, 'launch') as launch, patch.object(self.jobs, 'command') as command:
            status, response = self.request('GET', f'/api/projects/{self.pid}/media-info?asset={take["video_asset_id"]}')
            missing_status, _ = self.request('GET', f'/api/projects/{self.pid}/media-info?asset=missing')
        self.assertEqual(200, status)
        self.assertEqual(info, response)
        self.assertEqual(404, missing_status)
        probe.assert_called_once_with(video)
        self.assertEqual(before, self.store.location(self.pid).read_bytes())
        self.assertEqual(history, self.store.history(self.pid))
        self.assertEqual(video_hash, digest(video))
        self.assertEqual({}, self.block()['takes'][0]['video_info'])
        prepare.assert_not_called()
        launch.assert_not_called()
        command.assert_not_called()
        self.assertFalse((self.store.location(self.pid).parent / 'Jobs').exists())

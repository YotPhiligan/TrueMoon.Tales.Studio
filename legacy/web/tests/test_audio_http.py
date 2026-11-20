import http.client
import json
import threading
from unittest.mock import patch

from audio_fixtures import AudioFixture, wave_data
from studio.audio import AudioError
from studio.model import digest
from studio.server import LocalServer, handler_for


class WaveformHttpTests(AudioFixture):
    def setUp(self):
        super().setUp()
        static = self.base / 'static'
        static.mkdir()
        (static / 'index.html').write_text('<!doctype html><title>Audio fixture</title>')
        handler = handler_for(self.store, self.jobs, static)
        handler.log_message = lambda *args: None
        self.server = LocalServer(('127.0.0.1', 0), handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .02}, daemon=True)
        self.thread.start()
        self.addCleanup(self.close_server)

    def close_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)

    def get(self, aid):
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        try:
            connection.request('GET', f'/api/projects/{self.pid}/waveform?asset={aid}')
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_waveform_get_is_hash_cached_project_read_only_and_never_starts_gpu(self):
        before = self.state_bytes()
        source_hash = digest(self.audio)
        with patch('studio.audio.analyse_bounded', side_effect=lambda path: wave_data()) as analyse, patch.object(self.jobs, 'prepare') as prepare, patch.object(self.jobs, 'launch') as launch, patch.object(self.jobs, 'command') as command:
            first_status, first = self.get(self.aid)
            second_status, second = self.get(self.aid)
        self.assertEqual((200, 200), (first_status, second_status))
        self.assertEqual({**wave_data(), 'source_sha256': source_hash}, first)
        self.assertEqual(first, second)
        analyse.assert_called_once_with(self.audio.resolve())
        self.assertEqual(before, self.state_bytes())
        self.assertEqual(source_hash, digest(self.audio))
        self.assertNotIn('audio_info', self.current()['assets'][self.aid])
        prepare.assert_not_called()
        launch.assert_not_called()
        command.assert_not_called()
        self.assertFalse((self.store.location(self.pid).parent / 'Jobs').exists())

    def test_waveform_get_missing_or_image_assets_do_not_start_analysis(self):
        before = self.state_bytes()
        image_aid = self.block()['references'][0]['asset_id']
        with patch('studio.audio.analyse_bounded') as analyse:
            missing_status, _ = self.get('missing')
            image_status, _ = self.get(image_aid)
        self.assertEqual(404, missing_status)
        self.assertEqual(422, image_status)
        analyse.assert_not_called()
        self.assertEqual(before, self.state_bytes())

    def test_waveform_decode_failure_returns_422_without_cache_or_project_changes(self):
        before = self.state_bytes()
        with patch('studio.audio.analyse_bounded', side_effect=AudioError('Decode failed')):
            status, response = self.get(self.aid)
        self.assertEqual(422, status)
        self.assertIn('Decode failed', response['error'])
        self.assertEqual(before, self.state_bytes())
        self.assertEqual([], list((self.store.location(self.pid).parent / 'Preview/Audio').glob('*.json')))

import http.client
import json
from unittest.mock import Mock, patch

from agent_fixtures import AgentHttpFixture
from audio_fixtures import wave_data, write_wav
from test_sound import SoundFixture
import studio_agent as cli


class SoundHttpTests(SoundFixture, AgentHttpFixture):
    def test_audio_stream_waveform_selected_query_and_preview_range_head_do_not_commit_project(self):
        e = self.add();before, sources = self.project_state(), self.source_state()
        base = f'/api/projects/{self.pid}'
        status, data = self.http('GET', base + '/audio-streams?asset=' + self.aid)
        self.assertEqual(200, status);self.assertEqual([0], [s['index'] for s in data])
        with patch.object(self.store, 'waveform', return_value=wave_data()) as waveform:
            status, data = self.http('GET', base + '/waveform?asset=' + self.aid + '&stream=1')
            self.assertEqual(200, status);self.assertEqual(200, len(data['peaks']))
            waveform.assert_called_once_with(self.pid, self.aid, 1)
            self.assertEqual(422, self.http('GET', base + '/waveform?asset=' + self.aid + '&stream=-1')[0])
            self.assertEqual(1, waveform.call_count)
        with patch('studio.sound.preview', side_effect=lambda _path, target, *_: write_wav(target, [(8192,)] * 160)) as preview:
            for method, headers in [('GET', {'Range': 'bytes=0-15'}), ('HEAD', {})]:
                conn = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
                try:
                    conn.request(method, base + '/sound-preview?event=' + e['id'], headers=headers)
                    response = conn.getresponse();body = response.read()
                    self.assertEqual(206 if method == 'GET' else 200, response.status)
                    self.assertEqual(16 if method == 'GET' else 0, len(body))
                    if method == 'GET':self.assertEqual(b'RIFF', body[:4])
                finally:conn.close()
            preview.assert_called_once()
        self.assertEqual(before, self.project_state());self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_cli_timeline_read_and_explicit_file_write_keep_literal_sound_operation_and_revision(self):
        client = Mock();client.call.return_value = {'ok': True}
        args = cli.parser().parse_args(['--project', self.pid, 'timeline'])
        self.assertEqual({'ok': True}, cli.execute(client, args));client.call.assert_called_once_with('GET', f'/api/projects/{self.pid}/timeline')
        operation = {'action': 'add_sound', 'payload': self.payload(name='Реплика: «Принято»')}
        path = self.base / 'sound operation.json';path.write_text(json.dumps(operation, ensure_ascii=False), encoding='utf-8-sig')
        client.call.reset_mock()
        args = cli.parser().parse_args(['--project', self.pid, 'timeline', '--file', str(path), '--revision', '7'])
        cli.execute(client, args)
        client.call.assert_called_once_with('POST', f'/api/projects/{self.pid}/timeline', {'expected_revision': 7, **operation})
        client.call.reset_mock()
        args = cli.parser().parse_args(['--project', self.pid, 'timeline', '--file', str(path)])
        with self.assertRaises(cli.ClientError):cli.execute(client, args)
        client.call.assert_not_called();self.assert_no_generation()

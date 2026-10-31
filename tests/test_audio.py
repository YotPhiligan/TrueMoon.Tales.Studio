import json
import math
import os
import struct
import subprocess
import sys
from unittest.mock import Mock, patch

from audio_fixtures import AudioFixture, wave_data, write_wav
from studio import audio
from studio.model import StudioError, atomic, digest, read


class AudioAnalysisTests(AudioFixture):
    def test_real_stereo_wav_keeps_opposite_phase_peaks_at_100hz_and_partial_tail(self):
        path = self.book / 'opposite.wav'
        rows = [(16384, -16384)] * 160 + [(24576, -24576)] * 160 + [(8192, -8192)] * 80
        write_wav(path, rows)
        before = digest(path)
        result = audio.analyse(path)
        self.assertEqual([.5, .75, .25], result['peaks'])
        self.assertEqual((.025, 16000, 16000, 2, 100), tuple(result[k] for k in ['seconds', 'sample_rate', 'source_sample_rate', 'channels', 'peaks_per_second']))
        self.assertTrue(audio.valid(result))
        self.assertEqual(before, digest(path))

    def test_real_wav_resamples_48khz_to_16khz_without_changing_duration(self):
        path = self.book / '48k.wav'
        write_wav(path, [(16384,)] * 960, rate=48000)
        result = audio.analyse(path)
        self.assertEqual((.02, 16000, 48000, 1), tuple(result[k] for k in ['seconds', 'sample_rate', 'source_sample_rate', 'channels']))
        self.assertEqual(2, len(result['peaks']))
        for peak in result['peaks']:
            self.assertAlmostEqual(.5, peak, delta=.001)

    def test_real_silence_is_a_valid_waveform_not_an_empty_track(self):
        path = self.book / 'silence.wav'
        write_wav(path, [(0,)] * 160)
        result = audio.analyse(path)
        self.assertEqual(.01, result['seconds'])
        self.assertEqual([0.0], result['peaks'])
        self.assertTrue(audio.valid(result))

    def test_decoder_elapsed_deadline_aborts_without_real_waiting(self):
        clock = Mock(monotonic=Mock(side_effect=[0, 41]))
        with patch.object(audio, 'time', clock), self.assertRaises(audio.AudioError):
            audio.analyse(self.audio)

    def test_bounded_analysis_passes_literal_path_and_45_second_subprocess_deadline(self):
        expected = wave_data()
        completed = Mock(returncode=0, stdout=json.dumps(expected))
        with patch('studio.audio.subprocess.run', return_value=completed) as run:
            result = audio.analyse_bounded(self.audio)
        self.assertEqual(expected, result)
        args, options = run.call_args.args[0], run.call_args.kwargs
        self.assertEqual([sys.executable, '-s', '-B'], args[:3])
        self.assertEqual(str(self.audio.resolve()), args[-1])
        self.assertEqual(45, options['timeout'])
        self.assertFalse(options.get('shell', False))
        self.assertTrue(options['capture_output'])
        self.assertEqual('utf-8', options['encoding'])

    def test_real_cpu_worker_round_trips_waveform_json_with_space_in_filename(self):
        before = digest(self.audio)
        result = audio.analyse_bounded(self.audio)
        self.assertEqual((2.0, 16000, 1, 100), tuple(result[k] for k in ['seconds', 'sample_rate', 'channels', 'peaks_per_second']))
        self.assertEqual([.25] * 200, result['peaks'])
        self.assertTrue(audio.valid(result))
        self.assertEqual(before, digest(self.audio))

    def test_bounded_analysis_maps_timeout_process_json_and_schema_failures(self):
        cases = [subprocess.TimeoutExpired('audio', 45), Mock(returncode=1, stdout=''),
                 Mock(returncode=0, stdout='not JSON'), Mock(returncode=0, stdout=json.dumps({'version': 1}))]
        for result in cases:
            with self.subTest(result=type(result).__name__):
                kwargs = {'side_effect': result} if isinstance(result, Exception) else {'return_value': result}
                with patch('studio.audio.subprocess.run', **kwargs), self.assertRaises(audio.AudioError):
                    audio.analyse_bounded(self.audio)

    def test_waveform_schema_rejects_invalid_bin_count_nonfinite_peaks_and_types(self):
        good = wave_data(seconds=.025)
        self.assertTrue(audio.valid(good))
        self.assertTrue(audio.valid({**good, 'channels': 64, 'peaks': [0, 1, .25]}))
        invalid = [{'version': True}, {'seconds': 0}, {'seconds': math.nan}, {'seconds': 3600.01},
                   {'channels': 0}, {'channels': 65}, {'channels': True}, {'sample_rate': 8000},
                   {'peaks_per_second': 99}, {'peaks': [.25, .25]}, {'peaks': [.25, math.inf, .25]},
                   {'peaks': [.25, True, .25]}, {'peaks': [.25, -0.001, .25]}, {'peaks': [.25, 1.001, .25]}]
        for change in invalid:
            with self.subTest(change=change):
                self.assertFalse(audio.valid({**good, **change}))
        self.assertFalse(audio.valid([]))
        self.assertTrue(audio.valid(wave_data(seconds=3600)))


class WaveformCacheTests(AudioFixture):
    def test_hash_cache_reuses_analysis_and_invalidates_changed_bytes_with_same_size_mtime(self):
        original_state = self.state_bytes()
        stat = self.audio.stat()
        first_hash = digest(self.audio)
        changed = wave_data()
        changed['peaks'][-1] = .75
        with patch('studio.audio.analyse_bounded', side_effect=[wave_data(), changed]) as analyse:
            first = self.store.waveform(self.pid, self.aid)
            cached = self.store.waveform(self.pid, self.aid)
            self.assertEqual(first, cached)
            self.assertEqual(1, analyse.call_count)
            content = self.audio.read_bytes()
            self.audio.write_bytes(content[:-2] + struct.pack('<h', 24576))
            os.utime(self.audio, ns=(stat.st_atime_ns, stat.st_mtime_ns))
            second_hash = digest(self.audio)
            self.assertEqual(stat.st_size, self.audio.stat().st_size)
            self.assertEqual(stat.st_mtime_ns, self.audio.stat().st_mtime_ns)
            self.assertNotEqual(first_hash, second_hash)
            second = self.store.waveform(self.pid, self.aid)
        self.assertEqual(2, analyse.call_count)
        self.assertEqual(first_hash, first['source_sha256'])
        self.assertEqual(second_hash, second['source_sha256'])
        self.assertEqual(.75, second['peaks'][-1])
        folder = self.store.location(self.pid).parent / 'Preview/Audio'
        self.assertEqual({f'{first_hash}.v1.json', f'{second_hash}.v1.json'}, {f.name for f in folder.glob('*.json')})
        self.assertEqual(original_state, self.state_bytes())
        self.assertNotIn('audio_info', self.current()['assets'][self.aid])

    def test_corrupt_invalid_or_wrong_hash_cache_is_recomputed(self):
        before = self.state_bytes()
        fingerprint = digest(self.audio)
        cache = self.store.location(self.pid).parent / 'Preview/Audio' / f'{fingerprint}.v1.json'
        variants = ['bad JSON', {**wave_data(), 'source_sha256': 'wrong'},
                    {**wave_data(), 'source_sha256': fingerprint, 'peaks': []}]
        with patch('studio.audio.analyse_bounded', side_effect=lambda path: wave_data()) as analyse:
            for index, value in enumerate(variants):
                with self.subTest(cache=value if isinstance(value, str) else 'invalid object'):
                    cache.parent.mkdir(parents=True, exist_ok=True)
                    if isinstance(value, str):
                        cache.write_text(value, encoding='utf-8')
                    else:
                        atomic(cache, value)
                    result = self.store.waveform(self.pid, self.aid)
                    self.assertEqual(fingerprint, result['source_sha256'])
                    self.assertTrue(audio.valid(read(cache)))
                    self.assertEqual(index + 1, analyse.call_count)
        self.assertEqual(before, self.state_bytes())

    def test_source_change_during_analysis_refuses_cache_and_project_write(self):
        before = self.state_bytes()
        def changed_during_analysis(path):
            data = path.read_bytes()
            path.write_bytes(data[:-2] + struct.pack('<h', 24576))
            return wave_data()
        with patch('studio.audio.analyse_bounded', side_effect=changed_during_analysis), self.assertRaises(StudioError) as caught:
            self.store.waveform(self.pid, self.aid)
        self.assertEqual(409, caught.exception.status)
        self.assertEqual(before, self.state_bytes())
        self.assertEqual([], list((self.store.location(self.pid).parent / 'Preview/Audio').glob('*.json')))

    def test_unknown_and_non_audio_assets_are_rejected_before_analysis(self):
        image_aid = self.block()['references'][0]['asset_id']
        before = self.state_bytes()
        with patch('studio.audio.analyse_bounded') as analyse:
            for aid, status in [('missing', 404), (image_aid, 422)]:
                with self.subTest(aid=aid), self.assertRaises(StudioError) as caught:
                    self.store.waveform(self.pid, aid)
                self.assertEqual(status, caught.exception.status)
        analyse.assert_not_called()
        self.assertEqual(before, self.state_bytes())

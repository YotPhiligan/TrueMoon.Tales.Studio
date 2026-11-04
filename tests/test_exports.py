import copy
import fractions
import json
import math
from pathlib import Path
import unittest
import wave
from types import SimpleNamespace
from unittest.mock import Mock, patch

from scenario_fixtures import ScenarioFixture
from studio import export_plan, exports
from studio.export_worker import DraftRenderer, SourceFrames
from studio.model import StudioError, atomic, digest, read


class ExportFixture(ScenarioFixture):
    def setUp(self):
        super().setUp()
        p = self.current()
        p['timeline']['clips'] = [c for c in p['timeline']['clips'] if c['take_id']]
        p['output_size'] = [1920, 1080]
        self.store.commit(p, p['revision'], 'fixture', 'Exportable montage')

    def request(self, profile='draft'):
        return export_plan.plan(self.store, self.pid, self.current()['revision'], profile)

    def refused_plan(self, revision=None, profile='draft', status=422):
        before, sources = self.project_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            export_plan.plan(self.store, self.pid, self.current()['revision'] if revision is None else revision, profile)
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()


class ExportPlanTests(ExportFixture):
    def test_plan_is_detached_read_only_with_draft_even_size_and_exact_project_profile(self):
        before, sources = self.project_state(), self.source_state()
        request = self.request()
        self.assertEqual((192, 8, 24, [896, 504], 300), tuple(request[k] for k in ['frames', 'seconds', 'fps', 'size', 'timeout_seconds']))
        project = self.request('project')
        final = self.request('final')
        self.assertEqual([1920, 1080], project['size'])
        self.assertEqual(project['size'], final['size'])
        self.assertEqual(project['clips'], final['clips'])
        self.assertEqual({'video_codec':'libx264', 'pixel_format':'yuv420p', 'preset':'veryfast',
                          'crf':25, 'audio_codec':'aac', 'audio_bitrate_kbps':192}, project['encoding'])
        self.assertEqual(project['encoding'], request['encoding'])
        self.assertEqual({**project['encoding'], 'preset':'medium', 'crf':18, 'audio_bitrate_kbps':320}, final['encoding'])
        final['encoding']['crf'] = 7
        self.assertEqual(18, self.request('final')['encoding']['crf'])
        self.assertEqual(['c1', 'c2'], [c['id'] for c in request['clips']])
        self.assertEqual([(1, 24), (1, 36)], [(c['speed'], c['source_out_frame']) for c in request['clips']])
        asset = request['assets'][self.take['video_asset_id']]
        self.assertEqual(str(self.store.resolve_asset(self.current(), self.take['video_asset_id'])), asset['path'])
        request['clips'][0]['source_in_frame'] = 99
        request['music']['gain'] = .1
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()

    def test_extent_includes_empty_segment_and_music_tail_but_not_markers_and_accepts_600_seconds(self):
        p = self.current()
        p['timeline']['markers'][0]['frame'] = 518400
        p['assets'][p['timeline']['music']['asset_id']]['audio_info']['seconds'] = 700
        p['timeline']['music']['duration_frames'] = 200
        self.store.commit(p, p['revision'], 'fixture', 'Music tail')
        self.assertEqual(200, self.request()['frames'])
        p = self.current()
        p['timeline']['segments'] = p['timeline']['segments'][:1]
        p['timeline']['segments'][0]['duration_frames'] = 600 * p['fps']
        p['timeline']['music'] = None
        self.store.commit(p, p['revision'], 'fixture', 'Exact maximum')
        self.assertEqual((14400, 600), tuple(self.request()[k] for k in ['frames', 'seconds']))
        p = self.current();p['timeline']['segments'][0]['duration_frames'] += 1
        self.store.commit(p, p['revision'], 'fixture', 'One frame too long')
        self.refused_plan()

    def test_stale_revision_invalid_profile_and_empty_timeline_are_atomic(self):
        self.refused_plan(self.current()['revision'] - 1, status=409)
        self.refused_plan(True, status=409)
        for profile in ['full', '', None, []]:
            self.refused_plan(profile=profile)
        p = self.current();p['timeline'].update(segments=[], clips=[], music=None)
        self.store.commit(p, p['revision'], 'fixture', 'Empty except marker')
        self.refused_plan()

    def test_placeholder_wrong_take_metadata_and_global_scenario_issue_refuse_without_writes(self):
        baseline = self.current()
        candidates = []
        placeholder = copy.deepcopy(baseline);placeholder['timeline']['clips'][0]['take_id'] = None;candidates.append((placeholder, 422))
        wrong = copy.deepcopy(baseline);wrong['timeline']['clips'][0]['take_id'] = 'foreign-take';candidates.append((wrong, 404))
        metadata = copy.deepcopy(baseline);self.block(metadata)['takes'][0]['video_info'] = {};candidates.append((metadata, 422))
        wrong_kind = copy.deepcopy(baseline);wrong_kind['assets'][self.take['video_asset_id']]['kind'] = 'image';candidates.append((wrong_kind, 422))
        for changed, status in candidates:
            changed['revision'] = self.current()['revision']
            self.store.commit(changed, changed['revision'], 'fixture', 'Unexportable')
            self.refused_plan(status=status)
        baseline['revision'] = self.current()['revision'];self.store.commit(baseline, baseline['revision'], 'fixture', 'Restore fixture')
        self.activate();self.edit('update_segment', id='s2', start_frame=None)
        self.refused_plan()

    def test_source_position_retime_and_parts_cover_leading_internal_and_music_tail_pauses(self):
        clip = {'start_frame': 24, 'duration_frames': 7, 'source_in_frame': 2.25, 'source_out_frame': 10.375, 'speed': 1.25}
        self.assertEqual(6 / 24, export_plan.source_position(clip, 27, 24))
        self.assertAlmostEqual(10.375 / 24 - 1e-7, export_plan.source_position(clip, 30.75, 24))
        request = self.request()
        parts = export_plan.parts(request)
        self.assertEqual([(0, 12, None), (12, 24, 'c1'), (36, 12, None), (48, 24, 'c2'), (72, 120, None)],
                         [(p['start_frame'], p['duration_frames'], p['clip']['id'] if p['clip'] else None) for p in parts])
        self.assertEqual(request['frames'], sum(p['duration_frames'] for p in parts))
        self.assertEqual([{'start_frame': 0, 'duration_frames': 8, 'clip': None}], export_plan.parts({'clips': [], 'frames': 8}))
        p = self.current()
        p = self.store.timeline_operation(self.pid, p['revision'], 'edit_clip_source',
                                          {'id': 'c1', 'source_in_frame': 2.25, 'source_out_frame': 10.375, 'speed': 1.25})
        before = self.project_state();retimed = self.request()
        self.assertEqual((2.25, 10.375, 1.25, 7), tuple(retimed['clips'][0][k] for k in
                         ['source_in_frame', 'source_out_frame', 'speed', 'duration_frames']))
        self.assertEqual(192, retimed['frames']);self.assertEqual(before, self.project_state())


class ExportLifecycleTests(ExportFixture):
    def setUp(self):
        super().setUp()
        self.exports = exports.Exports(self.store)
        self.popen = self.enterContext(patch('studio.exports.subprocess.Popen', return_value=Mock(pid=1001)))
        self.process = self.enterContext(patch('psutil.Process'))
        self.process.return_value.create_time.return_value = 42.0
        self.thread = self.enterContext(patch('studio.exports.threading.Thread'))

    def create(self, **kw):
        return self.exports.create(self.pid, self.current()['revision'], **kw)

    def test_durable_create_retry_current_status_and_changed_key_payload_conflict_without_project_jobs(self):
        before, sources = self.project_state(), self.source_state()
        row = self.create(client_key='export-retry-key-001')
        folder = self.exports.folder(self.pid, row['id'])
        request_bytes = (folder / 'Request.json').read_bytes()
        self.assertEqual(('running', 2, 1001, 42), tuple(row[k] for k in ['state', 'revision', 'pid', 'process_created']))
        atomic(folder / 'Progress.json', {'stage': 'Frames', 'percent': 50})
        stopped = self.exports.stop(self.pid, row['id'], row['revision'])
        retried = self.create(client_key='export-retry-key-001')
        self.assertEqual('stopping', retried['state']);self.assertEqual(50, retried['progress']['percent'])
        self.assertEqual(stopped['id'], retried['id']);self.popen.assert_called_once()
        with self.assertRaises(StudioError) as caught:
            self.create(client_key='export-retry-key-001', profile='project')
        self.assertEqual(409, caught.exception.status)
        self.assertEqual(request_bytes, (folder / 'Request.json').read_bytes())
        self.assertEqual(before, self.project_state());self.assertEqual(sources, self.source_state());self.assert_no_generation()
        original_revision = row['project_revision']
        self.save({'notes': 'Later edit does not change this export intent'})
        replay = self.exports.create(self.pid, original_revision, client_key='export-retry-key-001')
        self.assertEqual(row['id'], replay['id']);self.popen.assert_called_once()

    def test_single_active_invalid_submission_and_stale_stop_are_atomic(self):
        root = self.store.location(self.pid).parent / 'Exports'
        for kwargs in [{'actor': ''}, {'actor': True}, {'actor': 'x' * 81}, {'client_key': 'short'}, {'client_key': '../bad-key-12345678'}]:
            with self.subTest(kwargs=kwargs), self.assertRaises(StudioError) as caught:
                self.create(**kwargs)
            self.assertEqual(422, caught.exception.status)
            self.assertFalse(root.exists())
        row = self.create();folder = self.exports.folder(self.pid, row['id']);status_bytes = (folder / 'Status.json').read_bytes()
        with self.assertRaises(StudioError) as caught:self.create()
        self.assertEqual(409, caught.exception.status);self.popen.assert_called_once()
        for revision in [row['revision'] - 1, True]:
            with self.assertRaises(StudioError) as caught:self.exports.stop(self.pid, row['id'], revision)
            self.assertEqual(409, caught.exception.status)
        self.assertEqual(status_bytes, (folder / 'Status.json').read_bytes());self.assertFalse(self.exports.stop_event.is_set())

    def test_launch_failure_persists_failure_and_releases_slot_without_replay(self):
        self.popen.side_effect = OSError('CPU runner unavailable')
        row = self.create(client_key='export-failure-key-001')
        self.assertEqual(('failed', 'CPU runner unavailable'), (row['state'], row['error']))
        self.assertIsNone(self.exports.active)
        retry = self.create(client_key='export-failure-key-001')
        self.assertEqual(row, retry);self.popen.assert_called_once();self.thread.assert_not_called()

    def test_durable_retry_finds_original_key_beyond_fifty_row_display_limit(self):
        original = self.create(client_key='export-old-durable-key')
        self.exports.save(self.exports.folder(self.pid, original['id']), original, state='failed')
        self.exports.active = None
        for index in range(50):
            newer = {**original, 'id': f'export-{index:016x}', 'state': 'failed', 'client_key': f'newer-key-{index:016x}',
                     'created_at': f'2099-01-01T00:00:{index:02}Z'}
            folder = self.exports.folder(self.pid, newer['id']);folder.mkdir()
            atomic(folder / 'Status.json', newer)
        self.assertEqual(50, len(self.exports.list(self.pid)))
        self.assertNotIn(original['id'], [row['id'] for row in self.exports.list(self.pid)])
        replay = self.create(client_key='export-old-durable-key')
        self.assertEqual(original['id'], replay['id'])
        self.popen.assert_called_once();self.assertIsNone(self.exports.active)
        original_folder = self.exports.folder(self.pid, original['id'])
        atomic(original_folder / 'Status.json', {**original, 'state': 'running'})
        restarted = exports.Exports(self.store)
        with patch('studio.exports.terminate_owned', return_value=False) as terminate:
            restarted.recover()
        terminate.assert_called_once()
        self.assertEqual(original_folder, terminate.call_args.args[1])
        self.assertEqual('interrupted', restarted.get(self.pid, original['id'])['state'])
        self.assertIsNone(restarted.active);self.popen.assert_called_once()

    def test_monitor_timeout_terminates_owned_worker_and_cannot_report_completed_without_video(self):
        row = self.create();folder = self.exports.folder(self.pid, row['id'])
        process = Mock();process.poll.return_value = None
        clock = SimpleNamespace(monotonic=Mock(side_effect=[0, exports.TIMEOUT + 6]))
        with patch('studio.exports.time', clock), patch('studio.exports.terminate_owned', return_value=True) as terminate:
            self.exports.monitor(folder, row, process)
        terminate.assert_called_once_with(row, folder);process.wait.assert_called_once_with(timeout=5)
        self.assertEqual('failed', self.exports.get(self.pid, row['id'])['state']);self.assertIsNone(self.exports.active)
        row = self.create();folder = self.exports.folder(self.pid, row['id'])
        self.exports.stop(self.pid, row['id'], row['revision'])
        with patch('studio.exports.terminate_owned', return_value=True) as terminate:
            self.exports.monitor(folder, row, process)
        terminate.assert_called_once_with(row, folder)
        self.assertEqual('cancelled', self.exports.get(self.pid, row['id'])['state']);self.assertIsNone(self.exports.active)

    def test_recovery_accepts_verified_finished_result_or_interrupts_never_resubmits(self):
        before = self.project_state();row = self.create();folder = self.exports.folder(self.pid, row['id'])
        fresh = exports.Exports(self.store)
        with patch('studio.exports.terminate_owned', return_value=False) as terminate:
            fresh.recover()
        terminate.assert_called_once();self.assertEqual('interrupted', fresh.get(self.pid, row['id'])['state']);self.popen.assert_called_once()
        self.assertIsNone(fresh.active)
        # Simulate a worker that finalized its MP4 before Studio could persist status.
        atomic(folder / 'Status.json', row);(folder / 'Draft.mp4').write_bytes(b'synthetic mp4 body')
        result = {'state': 'completed', 'sha256': digest(folder / 'Draft.mp4')};atomic(folder / 'Result.json', result)
        with patch('studio.exports.terminate_owned', return_value=False):fresh.recover()
        complete = fresh.get(self.pid, row['id']);self.assertEqual('completed', complete['state'])
        self.assertTrue(complete['video_url'].endswith('/video'));self.assertEqual(folder / 'Draft.mp4', fresh.video(self.pid, row['id']))
        atomic(folder / 'Status.json', row);(folder / 'Draft.mp4').write_bytes(b'changed result')
        with patch('studio.exports.terminate_owned', return_value=False):fresh.recover()
        self.assertEqual('interrupted', fresh.get(self.pid, row['id'])['state']);self.popen.assert_called_once()
        self.assertEqual(before, self.project_state());self.assert_no_generation()

    def test_process_stop_ownership_requires_pid_creation_script_and_exact_job_folder(self):
        folder = self.book / 'Export';folder.mkdir()
        worker = str(Path(exports.__file__).with_name('export_worker.py').resolve())
        row = {'pid': 1001, 'process_created': 42}
        process = self.process.return_value;process.children.return_value = [Mock()]
        for ctime, command in [(41, ['python', worker, str(folder)]), (42, ['python', 'unrelated.py', str(folder)]),
                               (42, ['python', worker, str(self.book / 'Other')])]:
            process.create_time.return_value = ctime;process.cmdline.return_value = command
            self.assertFalse(exports.terminate_owned(row, folder));process.kill.assert_not_called()
        process.create_time.return_value = 42;process.cmdline.return_value = ['python', '-s', '-B', worker, str(folder)]
        with patch('psutil.wait_procs') as wait:
            self.assertTrue(exports.terminate_owned(row, folder))
        self.process.assert_called_with(1001)
        process.kill.assert_called_once();process.children.return_value[0].kill.assert_called_once();wait.assert_called_once()


class ExportEncoderTests(ExportFixture):
    def test_native_audio_tempo_chains_and_sample_boundaries_and_source_mutation_rejection(self):
        folder = self.book / 'WorkerFilters';folder.mkdir()
        request = {'book': str(self.book), 'fps': 24, 'frames': 120, 'size': [32, 32], 'timeout_seconds': 15,
                   'clips': [{'asset_id': 'v', 'start_frame': 0, 'duration_frames': 96,
                              'source_in_frame': 12, 'source_out_frame': 36, 'speed': .25},
                             {'asset_id': 'v', 'start_frame': 96, 'duration_frames': 24,
                              'source_in_frame': 0, 'source_out_frame': 96, 'speed': 4}],
                   'assets': {'v': {'path': str(self.book / 'fake-video.mp4')}}, 'clip_audio': True, 'music': None}
        atomic(folder / 'Request.json', request);renderer = DraftRenderer(folder)
        audio_source = Mock();audio_source.__enter__ = Mock(return_value=SimpleNamespace(streams=SimpleNamespace(audio=[object()])))
        audio_source.__exit__ = Mock(return_value=False)
        with patch('av.open', return_value=audio_source), patch.object(renderer, 'command') as command:
            renderer.audio_parts()
        filters = [args[0][args[0].index('-af') + 1] for args, _ in command.call_args_list]
        self.assertIn('atrim=start=0.500000000000:end=1.500000000000', filters[0])
        self.assertIn('atempo=0.5,atempo=0.5', filters[0]);self.assertIn('atrim=end_sample=192000', filters[0])
        self.assertIn('atempo=2,atempo=2', filters[1]);self.assertIn('atrim=end_sample=48000', filters[1])
        renderer.request['clip_audio'] = False
        music_path = str(self.book / 'muted-score.wav');renderer.request['assets']['m'] = {'path': music_path}
        for muted, gain in [(True, .25), (False, 0)]:
            renderer.request['music'] = {'asset_id': 'm', 'start_frame': 24, 'duration_frames': 12,
                                         'source_in_frame': 12, 'gain': gain, 'muted': muted}
            with patch.object(renderer, 'command') as command:renderer.mux()
            args = command.call_args.args[0];filters = args[args.index('-filter_complex') + 1]
            self.assertEqual('192k', args[args.index('-b:a') + 1])
            self.assertNotIn(music_path, args);self.assertNotIn('concat', args)
            self.assertIn('anullsrc=r=48000:cl=stereo,atrim=end_sample=240000', filters)
        renderer.request['music'].update(muted=False, gain=.25)
        with patch.object(renderer, 'command') as command:renderer.mux()
        args = command.call_args.args[0];filters = args[args.index('-filter_complex') + 1]
        self.assertIn(music_path, args);self.assertIn('volume=0.25,adelay=48000S:all=1', filters)
        self.assertIn('atrim=start=0.500000000000:duration=0.500000000000', filters)
        outside = self.base / 'outside.mp4';outside.write_bytes(b'not readable source outside Book')
        renderer.request['assets']['v']['path'] = str(outside)
        with self.assertRaises(ValueError):renderer.inputs()
        # A changed registered source after encoding must prevent approval even with an output present.
        with patch.object(renderer, 'inputs', side_effect=[{'v': 'before'}, {'v': 'after'}]), \
                patch.object(renderer, 'video'), patch.object(renderer, 'audio_parts') as native, patch.object(renderer, 'mux'):
            with self.assertRaisesRegex(ValueError, 'Материалы изменились'):renderer.run()
        native.assert_not_called()

    def test_tiny_real_mp4_has_exact_frames_floor_retime_letterbox_gaps_music_trim_gain_and_silent_native(self):
        import av
        import numpy as np
        folder = self.book / 'CpuExport';folder.mkdir()
        source = self.book / 'colors.mkv'
        palette = [(0, 0, 0), (255, 255, 255), (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0),
                   (255, 0, 255), (0, 255, 255), (220, 100, 20), (80, 180, 30), (180, 20, 80), (40, 80, 180)]
        with av.open(str(source), 'w') as out:
            stream = out.add_stream('ffv1', rate=8);stream.width = 16;stream.height = 8;stream.pix_fmt = 'bgr0'
            for n, color in enumerate(palette):
                image = np.zeros((8, 16, 3), np.uint8);image[:] = color
                frame = av.VideoFrame.from_ndarray(image, format='rgb24');frame.pts = n;frame.time_base = fractions.Fraction(1, 8)
                for packet in stream.encode(frame):out.mux(packet)
            for packet in stream.encode(None):out.mux(packet)
        decoder = SourceFrames(str(source), .25)
        try:
            for time, expected in [(.26, 2), (.49, 3), (.5, 4), (.76, 6)]:
                self.assertLess(abs(float(decoder.at(time).time) - expected / 8), .001)
        finally:decoder.close()
        music = self.book / 'clicks.wav';rate = 48000;samples = np.zeros(rate * 2, np.float64)
        for start, length, amp in [(int(.1 * rate), int(.1 * rate), .8), (int(.5 * rate), int(.1 * rate), .4)]:
            samples[start:start + length] = amp * np.sin(2 * np.pi * 1000 * np.arange(length) / rate)
        stereo = np.repeat((samples * 32767).astype('<i2')[:, None], 2, axis=1)
        with wave.open(str(music), 'wb') as out:
            out.setnchannels(2);out.setsampwidth(2);out.setframerate(rate);out.writeframes(stereo.tobytes())
        request = {'book': str(self.book), 'fps': 8, 'frames': 16, 'seconds': 2, 'size': [32, 32], 'timeout_seconds': 15,
                   'clip_audio': True, 'clips': [{'id': 'c1', 'asset_id': 'v', 'start_frame': 2, 'duration_frames': 3,
                   'source_in_frame': 2, 'source_out_frame': 7, 'speed': 2}],
                   'assets': {'v': {'path': str(source)}, 'a': {'path': str(music)}},
                   'music': {'asset_id': 'a', 'start_frame': 8, 'duration_frames': 4, 'source_in_frame': 4, 'gain': .25, 'muted': False}}
        atomic(folder / 'Request.json', request);hashes = {p: digest(p) for p in [source, music]}
        result = DraftRenderer(folder).run()
        self.assertEqual({'frames': 16, 'fps': 8, 'seconds': 2, 'width': 32, 'height': 32, 'audio': True}, result['video_info'])
        with av.open(str(folder / 'Draft.mp4')) as output:frames = [f.to_ndarray(format='rgb24') for f in output.decode(video=0)]
        self.assertEqual(16, len(frames))
        for index in range(16):
            color = palette[[2, 4, 6][index - 2]] if 2 <= index <= 4 else (0, 0, 0)
            self.assertLess(float(np.max(np.abs(frames[index][16, 16].astype(float) - color))), 18, index)
            self.assertLess(float(frames[index][1].max()), 12, 'Letterbox top')
        with av.open(str(folder / 'Draft.mp4')) as output:
            audio = np.concatenate([f.to_ndarray() for f in output.decode(audio=0)], axis=1)[0]
        rms = lambda start, end: float(np.sqrt(np.mean(audio[round(start * rate):round(end * rate)] ** 2)))
        self.assertLess(rms(.1, .9), .002);self.assertLess(rms(1.25, 1.9), .002)
        self.assertGreater(rms(1.02, 1.08), .055);self.assertLess(rms(1.02, 1.08), .085)
        self.assertEqual(hashes, {p: digest(p) for p in hashes})
        self.assertEqual({'v': hashes[source], 'a': hashes[music]}, read(folder / 'InputHashes.json'))
        self.assertEqual(100, read(folder / 'Progress.json')['percent']);self.assert_no_generation()

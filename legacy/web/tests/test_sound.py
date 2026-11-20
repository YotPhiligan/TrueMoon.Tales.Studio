import copy
import fractions
import hashlib
import json
import math
import subprocess
from pathlib import Path
from unittest.mock import Mock, patch

from audio_fixtures import wave_data, write_wav
from scenario_fixtures import ScenarioFixture
from studio import agent, audio, export_plan, scenario, sound, timeline
from studio.export_worker import DraftRenderer
from studio.model import StudioError, atomic, digest, read


def multistream(path):
    import av
    import numpy as np
    with av.open(str(path), 'w') as out:
        streams = [out.add_stream('pcm_s16le', rate=16000) for _ in range(2)]
        for n, stream in enumerate(streams):
            stream.layout = 'mono';stream.metadata.update(language=['rus', 'eng'][n], title=['Lead', 'Room'][n])
        for index, (stream, amplitude) in enumerate(zip(streams, [4096, 16384])):
            values = np.full((1, 32000), amplitude, dtype=np.int16)
            if index == 1:values[:, :4000] = 0
            frame = av.AudioFrame.from_ndarray(values, format='s16', layout='mono')
            frame.sample_rate = 16000;frame.pts = 0;frame.time_base = fractions.Fraction(1, 16000)
            for packet in stream.encode(frame):out.mux(packet)
            for packet in stream.encode(None):out.mux(packet)


class SoundFixture(ScenarioFixture):
    def setUp(self):
        super().setUp()
        self.audio = self.book / 'sound.wav';write_wav(self.audio, [(8192,)] * 32000)
        p = self.current();self.aid = self.store.asset(p, self.audio)
        self.store.commit(p, p['revision'], 'fixture', 'Sound fixture')
        self.analyse = self.enterContext(patch('studio.audio.analyse_bounded', return_value=wave_data()))

    def payload(self, **changes):
        return {'segment_id': 's1', 'name': 'Footstep', 'lane': 'sfx', 'asset_id': self.aid, 'stream': 0,
                'offset_frames': 24, 'source_in_frame': 12, 'duration_frames': 24, 'gain': .5, 'muted': False, **changes}

    def op(self, action, **payload):
        return self.store.timeline_operation(self.pid, self.current()['revision'], action, payload)

    def add(self, **changes):
        p = self.op('add_sound', **self.payload(**changes))
        return p['timeline']['sound_events'][-1]

    def refused(self, action, payload, revision=None, status=422):
        before, sources = self.project_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'] if revision is None else revision, action, payload)
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.project_state());self.assertEqual(sources, self.source_state());self.assert_no_generation()


class SoundEventTests(SoundFixture):
    def test_legacy_read_has_no_sound_migration_and_add_update_remove_undo_preserve_sources(self):
        before, sources = self.project_state(), self.source_state()
        self.assertEqual([], sound.events(timeline.get(self.current())))
        self.assertEqual([], sound.placed(timeline.get(self.current())))
        self.assertNotIn('sound_events', self.current()['timeline']);self.assertEqual(before, self.project_state())
        e = self.add();p = self.current()
        self.assertEqual((2, 0, 24, .5), tuple(e[k] for k in ['source_seconds', 'stream', 'duration_frames', 'gain']))
        self.assertEqual(24, sound.placed(p['timeline'])[0]['start_frame'])
        self.op('update_sound', id=e['id'], name='Whisper', lane='voice', offset_frames=36, gain=0, muted=True)
        changed = self.current()['timeline']['sound_events'][0]
        self.assertEqual(('Whisper', 'voice', 36, 0, True), tuple(changed[k] for k in ['name', 'lane', 'offset_frames', 'gain', 'muted']))
        self.op('remove_sound', id=e['id']);self.assertEqual([], sound.events(self.current()['timeline']))
        self.op('undo');self.assertEqual([changed], sound.events(self.current()['timeline']))
        self.op('undo');self.op('undo')
        self.assertEqual([], sound.events(self.current()['timeline']))
        self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_segment_move_follows_offset_while_clip_ripple_does_not_move_sound_and_removal_undo_restores(self):
        e = self.add(offset_frames=60)
        self.op('remove_segment', id='s1');self.assertEqual([], sound.events(self.current()['timeline']))
        self.op('undo');self.assertEqual([e], sound.events(self.current()['timeline']))
        self.activate()
        self.op('update_clip', id='c1', duration_frames=30)
        self.assertEqual(60, sound.placed(self.current()['timeline'])[0]['start_frame'])
        self.assertEqual(54, next(c for c in self.current()['timeline']['clips'] if c['id'] == 'c2')['start_frame'])
        self.op('move_segment', id='s1', start_frame=10)
        self.assertEqual((60, 70), (sound.events(self.current()['timeline'])[0]['offset_frames'], sound.placed(self.current()['timeline'])[0]['start_frame']))
        self.op('remove_segment', id='s1');self.assertEqual([], sound.events(self.current()['timeline']))
        self.op('undo');self.assertEqual([e], sound.events(self.current()['timeline']))
        self.assertEqual(70, sound.placed(self.current()['timeline'])[0]['start_frame']);self.assert_no_generation()

    def test_block_sound_moves_splits_and_restores_with_its_placement(self):
        self.activate()
        event = self.add(clip_id='c2', segment_id='s2', offset_frames=4, source_in_frame=8, duration_frames=24)
        self.assertEqual('s1', event['segment_id'])
        self.op('update_clip', id='c1', duration_frames=30)
        p = self.current()
        self.assertEqual([(58, 20)], [(e['start_frame'], e['duration_frames']) for e in sound.placed(p['timeline'])])
        self.op('update_clip', id='c2', mute_native_audio=True)
        p = self.op('split_clip', id='c2', frame=66)
        first, second = sound.placed(p['timeline'])
        self.assertEqual([(58, 8, 8), (66, 12, 16)],
                         [(e['start_frame'], e['duration_frames'], e['source_in_frame']) for e in [first, second]])
        right = next(c for c in p['timeline']['clips'] if c['id'] == second['clip_id'])
        self.assertTrue(right['mute_native_audio'])
        self.op('remove_clip', id=right['id'])
        self.assertEqual([first['id']], [e['id'] for e in sound.events(self.current()['timeline'])])
        self.op('undo')
        self.assertEqual([first, second], sound.placed(self.current()['timeline']))
        self.assert_no_generation()

    def test_source_boundaries_validation_stream_types_and_revision_failures_are_atomic(self):
        e = self.add(source_in_frame=24, duration_frames=24)
        for changes in [{'source_in_frame': 25}, {'duration_frames': 0}, {'offset_frames': -1}, {'offset_frames': .5},
                        {'stream': -1}, {'stream': 64}, {'stream': True}, {'gain': -.001}, {'gain': 1.001},
                        {'gain': math.nan}, {'gain': True}, {'muted': 1}, {'lane': 'music'}, {'lane': []}, {'name': ''},
                        {'offset_frames': timeline.MAX_FRAME}, {'asset_id': []}, {'asset_id': None},
                        {'asset_id': self.block()['references'][0]['asset_id']}]:
            with self.subTest(changes=changes):self.refused('update_sound', {'id': e['id'], **changes})
        self.refused('update_sound', {'id': e['id'], 'gain': .25}, self.current()['revision'] - 1, 409)
        self.refused('remove_sound', {'id': e['id']}, True, 409)
        self.assertEqual(1, len(sound.events(self.current()['timeline'])))
        p = self.op('update_sound', id=e['id'], gain=1, source_in_frame=0, duration_frames=48)
        self.assertEqual((0, 48, 1), tuple(p['timeline']['sound_events'][0][k] for k in ['source_in_frame', 'duration_frames', 'gain']))

    def test_sound_limit_duplicate_ids_and_invalid_source_metadata_are_directly_rejected(self):
        p = self.current();base = {**self.payload(), 'id': 'sound-one', 'source_seconds': 2}
        for changes in [{'sound_events': [base, base]}, {'sound_events': [{**base, 'id': f'sound-{i}'} for i in range(65)]},
                        {'sound_events': [{**base, 'source_seconds': 0}]}, {'sound_events': [{**base, 'source_seconds': math.inf}]},
                        {'sound_events': [{**base, 'source_seconds': True}]}]:
            with self.subTest(changes=str(changes)[:70]), self.assertRaises(timeline.TimelineError):
                sound.validate(p, {**p['timeline'], **changes})
        sound.validate(p, {**p['timeline'], 'sound_events': [{**base, 'id': f'sound-{i}'} for i in range(64)]})

    def test_export_plan_includes_relative_sound_tail_and_immutable_selected_stream(self):
        legacy_state = self.project_state()
        self.assertNotIn('sound_sources', agent.context(self.store, self.pid))
        self.assertEqual(legacy_state, self.project_state())
        e = self.add(segment_id='s2', stream=1, offset_frames=60, duration_frames=48, source_in_frame=0)
        self.add(segment_id='s1', name='Same donor second use', duration_frames=12)
        p = self.current();p['timeline']['clips'] = [c for c in p['timeline']['clips'] if c['take_id']]
        fingerprint = digest(self.audio);p['assets'][self.aid]['sha256'] = fingerprint
        self.store.commit(p, p['revision'], 'fixture', 'No placeholders')
        before = self.project_state();request = export_plan.plan(self.store, self.pid, self.current()['revision'])
        self.assertEqual((228, 9.5), (request['frames'], request['seconds']))
        self.assertEqual((e['id'], 180, 1, 48), tuple(request['sounds'][0][k] for k in ['id', 'start_frame', 'stream', 'duration_frames']))
        self.assertEqual(str(self.audio), request['assets'][self.aid]['path'])
        request['sounds'][0]['gain'] = .99
        self.assertEqual(before, self.project_state());self.assertEqual(.5, sound.events(self.current()['timeline'])[0]['gain'])
        context = agent.context(self.store, self.pid, sid='s2')
        self.assertEqual([self.aid], list(context['sound_sources']))
        donor = context['sound_sources'][self.aid]
        self.assertEqual((str(self.audio), fingerprint, True), tuple(donor[k] for k in ['absolute_path', 'sha256', 'available']))
        self.assertIn('asset=' + self.aid, donor['media_url'])
        snapshot = copy.deepcopy(context);expected = snapshot.pop('snapshot_sha256')
        self.assertEqual(expected, hashlib.sha256(json.dumps(snapshot, ensure_ascii=False, sort_keys=True).encode()).hexdigest())
        alternate = self.current();alternate['assets'][self.aid]['sha256'] = 'f' * 64
        with patch.object(self.store, 'get', return_value=alternate):different = agent.context(self.store, self.pid, sid='s2')
        self.assertEqual(context['project']['revision'], different['project']['revision'])
        self.assertNotEqual(context['snapshot_sha256'], different['snapshot_sha256'])
        self.assertEqual(before, self.project_state());self.assertEqual(fingerprint, digest(self.audio))

    def test_preview_cache_ignores_placement_gain_but_keys_cut_and_stream_without_project_writes(self):
        e = self.add();before = self.project_state();calls = []
        def preview(path, target, stream, start, duration):
            calls.append((path, stream, start, duration));target.write_bytes(b'cached-cut')
        with patch('studio.sound.preview', side_effect=preview):
            first = self.store.sound_preview(self.pid, e['id']);self.assertEqual(first, self.store.sound_preview(self.pid, e['id']))
            self.assertEqual([(self.audio, 0, .5, 1)], calls);self.assertEqual(before, self.project_state())
            self.op('update_sound', id=e['id'], gain=.2, offset_frames=48, muted=True)
            self.assertEqual(first, self.store.sound_preview(self.pid, e['id']));self.assertEqual(1, len(calls))
            self.op('update_sound', id=e['id'], stream=1)
            second = self.store.sound_preview(self.pid, e['id'])
            self.assertNotEqual(first, second);self.assertEqual((1, .5, 1), calls[-1][1:])
            self.op('update_sound', id=e['id'], source_in_frame=0)
            third = self.store.sound_preview(self.pid, e['id']);self.assertNotEqual(second, third)
            self.assertEqual((1, 0, 1), calls[-1][1:])
            self.op('update_sound', id=e['id'], duration_frames=12)
            fourth = self.store.sound_preview(self.pid, e['id']);self.assertNotEqual(third, fourth)
            p = self.current();p['fps'] = 48;p['timeline']['fps'] = 48
            self.store.commit(p, p['revision'], 'fixture', 'Different grid')
            fifth = self.store.sound_preview(self.pid, e['id']);self.assertNotEqual(fourth, fifth)
            self.assertEqual((1, 0, .25), calls[-1][1:])
            before = self.project_state();self.audio.write_bytes(self.audio.read_bytes() + b'fixture new source bytes')
            sixth = self.store.sound_preview(self.pid, e['id']);self.assertNotEqual(fifth, sixth)
            self.assertEqual(6, len(calls));self.assertEqual(before, self.project_state())
        self.assert_no_generation()

    def test_preview_timeout_and_changed_source_never_commit_project_or_approve_cache(self):
        e = self.add();before = self.project_state()
        with patch('studio.sound.subprocess.run', side_effect=subprocess.TimeoutExpired('preview', 45)), self.assertRaises(timeline.TimelineError):
            sound.preview(self.audio, self.book / 'no-result.wav', 0, 0, 1)
        def changed(path, target, *_):
            target.write_bytes(b'partial');path.write_bytes(path.read_bytes() + b'changed')
        with patch('studio.sound.preview', side_effect=changed), self.assertRaises(StudioError) as caught:
            self.store.sound_preview(self.pid, e['id'])
        self.assertEqual(409, caught.exception.status);self.assertEqual(before, self.project_state())
        files = list((self.store.location(self.pid).parent / 'Preview/Sound').glob('*.wav'))
        self.assertFalse(any(len(p.stem) == 64 for p in files));self.assert_no_generation()


class SoundMediaTests(ScenarioFixture):
    def test_real_multistream_analysis_preview_and_hash_cache_select_requested_audio_only(self):
        import av
        import numpy as np
        source = self.book / 'two streams.mov';multistream(source)
        p = self.current();aid = self.store.asset(p, source);self.store.commit(p, p['revision'], 'fixture', 'Two streams')
        before, fingerprint = self.project_state(), digest(source)
        streams = self.store.sound_streams(self.pid, aid)
        self.assertEqual([0, 1], [s['index'] for s in streams]);self.assertEqual(['rus', 'eng'], [s['language'] for s in streams])
        with patch('studio.audio.analyse_bounded', wraps=audio.analyse_bounded) as analyse:
            first = self.store.waveform(self.pid, aid, 0);second = self.store.waveform(self.pid, aid, 1)
            self.assertEqual(second, self.store.waveform(self.pid, aid, 1));self.assertEqual(2, analyse.call_count)
        self.assertEqual([.125] * 200, first['peaks']);self.assertEqual([0] * 25 + [.5] * 175, second['peaks'])
        self.assertEqual(before, self.project_state());self.assertEqual(fingerprint, digest(source))
        path = self.book / 'selected.wav';sound.preview(source, path, 1, .25, .5)
        with av.open(str(path)) as cut:frames = list(cut.decode(audio=0))
        values = np.concatenate([f.to_ndarray().reshape(-1) for f in frames])
        self.assertEqual(48000, sum(f.samples for f in frames) * 2)
        # ffmpeg mono->stereo uses equal-power -3dB channel scaling.
        self.assertGreater(float(np.mean(values)), 11000);self.assertLess(float(np.mean(values)), 12000)
        with self.assertRaises(audio.AudioError):audio.analyse(source, 2)
        with self.assertRaises(StudioError) as missing:self.store.waveform(self.pid, aid, 2)
        self.assertEqual(422, missing.exception.status);self.assertEqual(before, self.project_state())
        with patch('studio.audio.subprocess.run', side_effect=subprocess.TimeoutExpired('wave', 45)), self.assertRaises(audio.AudioError):
            audio.analyse_bounded(source, 1)
        self.assertEqual(fingerprint, digest(source));self.assert_no_generation()

    def test_no_audio_source_refuses_binding_without_project_migration_or_event(self):
        import av
        import numpy as np
        source = self.book / 'silent.mp4'
        with av.open(str(source), 'w') as out:
            stream = out.add_stream('libx264', rate=24);stream.width = 16;stream.height = 16;stream.pix_fmt = 'yuv420p'
            for packet in stream.encode(av.VideoFrame.from_ndarray(np.zeros((16, 16, 3), np.uint8), format='rgb24')):out.mux(packet)
            for packet in stream.encode(None):out.mux(packet)
        p = self.current();aid = self.store.asset(p, source);self.store.commit(p, p['revision'], 'fixture', 'Silent source')
        before = self.project_state();self.assertEqual([], self.store.sound_streams(self.pid, aid))
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'], 'add_sound',
                                          {'segment_id': 's1', 'asset_id': aid, 'stream': 0, 'name': 'Impossible', 'duration_frames': 1})
        self.assertEqual(422, caught.exception.status);self.assertEqual(before, self.project_state())
        self.assertNotIn('sound_events', self.current()['timeline']);self.assert_no_generation()
        with patch('studio.audio.subprocess.run', return_value=Mock(returncode=0, stdout=json.dumps(wave_data()))) as run:
            audio.analyse_bounded(source, 1)
        self.assertEqual('1', run.call_args.args[0][-1]);self.assertEqual(45, run.call_args.kwargs['timeout'])
        self.assertEqual('utf-8', run.call_args.kwargs['env']['PYTHONIOENCODING'])
        self.assertEqual('replace', run.call_args.kwargs['errors'])

    def test_real_mp4_mixes_music_and_selected_stream_at_relative_start_gain_and_ignores_muted_sound(self):
        import av
        import numpy as np
        folder = self.book / 'SoundExport';folder.mkdir();source = self.book / 'two.mkv';multistream(source)
        music = self.book / 'music.wav';write_wav(music, [(3277,)] * 32000)
        event = {'asset_id': 'video', 'stream': 1, 'start_frame': 8, 'source_in_frame': 4, 'duration_frames': 4, 'gain': .5, 'muted': False}
        request = {'book': str(self.book), 'fps': 8, 'frames': 16, 'size': [32, 32], 'timeout_seconds': 15,
                   'clips': [], 'clip_audio': False, 'assets': {'video': {'path': str(source)}, 'music': {'path': str(music)}},
                   'music': {'asset_id': 'music', 'start_frame': 0, 'source_in_frame': 0, 'duration_frames': 16, 'gain': .25, 'muted': False},
                   'sounds': [event, {**event, 'stream': 0, 'start_frame': 0, 'muted': True}, {**event, 'start_frame': 0, 'gain': 0}]}
        atomic(folder / 'Request.json', request);hashes = {p: digest(p) for p in [source, music]}
        result = DraftRenderer(folder).run();self.assertEqual((16, 8, 2), tuple(result['video_info'][k] for k in ['frames', 'fps', 'seconds']))
        with av.open(str(folder / 'Draft.mp4')) as out:
            self.assertEqual(2, out.streams.audio[0].duration * out.streams.audio[0].time_base)
            frames = list(out.decode(audio=0))
        self.assertEqual(0, frames[0].pts)
        values = np.concatenate([f.to_ndarray() for f in frames], axis=1)[0]
        self.assertGreaterEqual(values.size, 96000)
        mean = lambda start, end: float(np.mean(values[round(start * 48000):round(end * 48000)]))
        self.assertGreater(mean(.2, .8), .014);self.assertLess(mean(.2, .8), .022)
        self.assertGreater(mean(1.1, 1.4), .175);self.assertLess(mean(1.1, 1.4), .21)
        self.assertGreater(mean(1.7, 1.9), .014);self.assertLess(mean(1.7, 1.9), .022)
        self.assertEqual(hashes, {p: digest(p) for p in hashes});self.assert_no_generation()

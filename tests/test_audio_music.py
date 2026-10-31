import copy
import math
from unittest.mock import patch

from audio_fixtures import AudioFixture, wave_data
from studio.model import StudioError, digest
from studio.timeline import TimelineError, apply, empty, get


class MusicTimelineTests(AudioFixture):
    def set_music(self, **patches):
        with patch('studio.audio.analyse_bounded', side_effect=lambda path: wave_data()):
            return self.op('set_music', **self.music_payload(**patches))

    def rejected(self, action, payload, revision=None, status=422):
        before = self.state_bytes()
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'] if revision is None else revision, action, payload)
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.state_bytes())

    def test_legacy_defaults_add_music_and_clip_audio_without_rewriting_project(self):
        before = self.state_bytes()
        timeline = get(self.current())
        self.assertIsNone(timeline['music'])
        self.assertIs(timeline['clip_audio'], False)
        self.assertEqual(before, self.state_bytes())

    def test_set_music_outside_video_segments_persists_metadata_only_on_success(self):
        self.op('add_segment', name='Video', start_frame=0, duration_frames=24)
        before = self.current()
        result = self.set_music(start_frame=240)
        music = result['timeline']['music']
        self.assertEqual({**self.music_payload(start_frame=240), 'id': music['id']}, music)
        self.assertEqual(288, music['start_frame'] + music['duration_frames'])
        self.assertEqual(before['blocks'], result['blocks'])
        self.assertEqual(before['segments'], result['segments'])
        info = {'seconds': 2.0, 'sample_rate': 16000, 'channels': 1, 'source_sha256': digest(self.audio)}
        self.assertEqual(info, result['assets'][self.aid]['audio_info'])
        self.assertEqual(info, self.current()['assets'][self.aid]['audio_info'])
        self.assertEqual(before['revision'] + 1, result['revision'])

    def test_failed_set_music_trim_does_not_commit_analysed_metadata(self):
        before = self.state_bytes()
        with patch('studio.audio.analyse_bounded', side_effect=lambda path: wave_data()) as analyse:
            self.rejected('set_music', self.music_payload(duration_frames=49))
        analyse.assert_called_once_with(self.audio.resolve())
        self.assertEqual(before, self.state_bytes())
        self.assertNotIn('audio_info', self.current()['assets'][self.aid])
        self.assertNotIn('timeline', self.current())

    def test_stale_or_non_audio_set_music_does_not_even_analyse(self):
        revision = self.current()['revision']
        self.op('add_marker', name='Newer', frame=0)
        image_aid = self.block()['references'][0]['asset_id']
        with patch('studio.audio.analyse_bounded') as analyse:
            self.rejected('set_music', self.music_payload(), revision=revision, status=409)
            self.rejected('set_music', self.music_payload(asset_id=image_aid))
            self.rejected('set_music', self.music_payload(asset_id='missing'))
        analyse.assert_not_called()

    def test_update_music_exact_source_end_allowed_and_one_frame_past_is_atomic(self):
        initial = self.set_music()
        music_id = initial['timeline']['music']['id']
        p = self.op('update_music', start_frame=96, source_in_frame=24, duration_frames=24)
        self.assertEqual({**self.music_payload(start_frame=96, source_in_frame=24, duration_frames=24), 'id': music_id}, p['timeline']['music'])
        self.rejected('update_music', {'duration_frames': 25})
        self.rejected('update_music', {'source_in_frame': 25})
        removed = self.op('remove_music')
        self.assertIsNone(removed['timeline']['music'])
        self.assertEqual(initial['assets'], removed['assets'])
        self.rejected('update_music', {'gain': .5})

    def test_gain_inclusive_bounds_and_strict_mute_and_clip_audio_booleans(self):
        self.set_music(gain=0, muted=False)
        p = self.op('update_music', gain=1, muted=True)
        self.assertEqual((1, True), (p['timeline']['music']['gain'], p['timeline']['music']['muted']))
        for gain in [-.001, 1.001, math.nan, math.inf, None, '0.5', True]:
            with self.subTest(gain=gain):
                self.rejected('update_music', {'gain': gain})
        for invalid in [0, 1, 'false', None]:
            with self.subTest(muted=invalid):
                self.rejected('update_music', {'muted': invalid})
            with self.subTest(clip_audio=invalid):
                self.rejected('set_clip_audio', {'enabled': invalid})
        self.assertIs(self.op('set_clip_audio', enabled=True)['timeline']['clip_audio'], True)
        self.assertIs(self.op('set_clip_audio', enabled=False)['timeline']['clip_audio'], False)

    def test_music_frame_fields_refuse_negative_fractional_bool_and_zero_duration(self):
        self.set_music()
        for key, values in [('start_frame', [-1, True, .5, '0']), ('source_in_frame', [-1, False, .5]),
                            ('duration_frames', [0, -1, True, 1.5])]:
            for value in values:
                with self.subTest(key=key, value=value):
                    self.rejected('update_music', {key: value})
        limit = 24 * 60 * 60 * 6
        p = self.op('update_music', start_frame=limit - 48)
        self.assertEqual(limit, p['timeline']['music']['start_frame'] + 48)
        self.rejected('update_music', {'start_frame': limit - 47})

    def test_music_and_clip_audio_undo_redo_preserve_later_notes_takes_and_assets(self):
        self.set_music()
        self.op('set_clip_audio', enabled=True)
        self.op('update_music', gain=.5)
        self.add_take()
        p = self.current()
        self.block(p).update(notes='Later author edit', status='generation-ready')
        self.block(p)['settings']['seed'] = '42'
        p = self.store.commit(p, p['revision'], 'author', 'Later changes')
        unrelated = {key: copy.deepcopy(p[key]) for key in ['blocks', 'assets', 'segments']}
        first = self.op('undo')
        self.assertEqual(.8, first['timeline']['music']['gain'])
        self.assertIs(first['timeline']['clip_audio'], True)
        second = self.op('undo')
        self.assertIs(second['timeline']['clip_audio'], False)
        self.assertEqual(.8, second['timeline']['music']['gain'])
        self.op('redo')
        redone = self.op('redo')
        self.assertEqual(.5, redone['timeline']['music']['gain'])
        self.assertIs(redone['timeline']['clip_audio'], True)
        for key in unrelated:
            self.assertEqual(unrelated[key], first[key])
            self.assertEqual(unrelated[key], second[key])
            self.assertEqual(unrelated[key], redone[key])

    def test_old_undo_entry_without_audio_fields_restores_none_and_false_then_redoes_music(self):
        p = self.set_music()
        p['timeline']['clip_audio'] = True
        p['timeline']['undo'] = [{'action': 'legacy', 'state': {'segments': [], 'clips': [], 'markers': []}}]
        self.store.commit(p, p['revision'], 'fixture', 'Legacy undo')
        before = copy.deepcopy(p['timeline']['music'])
        undone = self.op('undo')
        self.assertIsNone(undone['timeline']['music'])
        self.assertIs(undone['timeline']['clip_audio'], False)
        restored = self.op('redo')
        self.assertEqual(before, restored['timeline']['music'])
        self.assertIs(restored['timeline']['clip_audio'], True)

    def test_apply_requires_audio_metadata_and_does_not_enrich_or_modify_input(self):
        p = self.current()
        original = copy.deepcopy(p)
        with self.assertRaises(TimelineError):
            apply(p, 'set_music', self.music_payload())
        self.assertEqual(original, p)
        p['assets'][self.aid]['audio_info'] = {'seconds': 2.0}
        t = apply(p, 'set_music', self.music_payload(source_in_frame=24, duration_frames=24))
        self.assertEqual((24, 24), (t['music']['source_in_frame'], t['music']['duration_frames']))
        self.assertNotIn('timeline', p)

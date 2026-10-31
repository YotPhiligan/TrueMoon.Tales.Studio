import copy
import fractions
import math
import unittest

from audio_fixtures import write_wav
from scenario_fixtures import ScenarioFixture
from studio import export_plan, scenario, timeline, transitions as tr
from studio.export_worker import DraftRenderer
from studio.model import StudioError, atomic, digest


def row(left='a', right='b', out=2, pause=4, incoming=2):
    return {'left_clip_id': left, 'right_clip_id': right, 'fade_out_frames': out,
            'pause_frames': pause, 'fade_in_frames': incoming}


def montage():
    return {'clips': [{'id': 'c', 'segment_id': 's2', 'start_frame': 20, 'duration_frames': 8},
                      {'id': 'a', 'segment_id': 's1', 'start_frame': 0, 'duration_frames': 8},
                      {'id': 'b', 'segment_id': 's2', 'start_frame': 10, 'duration_frames': 8}], 'transitions': [row()]}


class TransitionMathTests(unittest.TestCase):
    def test_global_joins_windows_and_midpoint_levels_include_cross_segment_and_preserve_inputs(self):
        t = montage();original = copy.deepcopy(t)
        self.assertEqual([('a', 'b', 2), ('b', 'c', 2)], [(j['left']['id'], j['right']['id'], j['gap_frames']) for j in tr.joins(t)])
        compiled = tr.windows(t)
        self.assertEqual([{**row(), 'gap_frames': 2, 'out_start_frame': 4, 'black_start_frame': 6,
                           'right_start_frame': 10, 'in_end_frame': 12}], compiled)
        self.assertEqual([1, 1, .75, .25, 0, 0, 0, 0, .25, .75, 1, 1],
                         [tr.brightness(frame, compiled) for frame in [-1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 18]])
        self.assertEqual(original, t);self.assertEqual([], tr.joins({'clips': []}))
        self.assertEqual([], tr.joins({'clips': t['clips'][:1]}));self.assertEqual(1, tr.brightness(0, []))

    def test_capacity_gap_and_incoming_outgoing_touch_boundaries_are_exact(self):
        t = montage()
        for good in [row(out=8, pause=2, incoming=8), row(out=0, pause=10, incoming=0), row(out=0, pause=2, incoming=0)]:
            tr.validate({**t, 'transitions': [good]})
        for bad in [row(pause=1), row(out=9, pause=2), row(out=0, pause=11), row(incoming=9)]:
            with self.subTest(bad=bad), self.assertRaises(timeline.TimelineError):tr.validate({**t, 'transitions': [bad]})
        # Incoming b ends at12; next outgoing can begin exactly12 but never11.
        tr.validate({**t, 'transitions': [row(), row('b', 'c', out=0, pause=8, incoming=0)]})
        with self.assertRaises(timeline.TimelineError):tr.validate({**t, 'transitions': [row(), row('b', 'c', out=0, pause=9, incoming=0)]})

    def test_invalid_rows_types_duplicate_right_and_non_neighbor_are_rejected(self):
        t = montage()
        invalid = [None, {}, 'bad', [None], [{}], [row('a', 'c')], [row(), row()], [row(left=[])],
                   [row(out=-1)], [row(out=True)], [row(pause=4.0)], [row(incoming=math.nan)],
                   [row(incoming=518401)]]
        for rows in invalid:
            with self.subTest(rows=rows), self.assertRaises(timeline.TimelineError):tr.validate({**t, 'transitions': rows})

    def test_set_replace_remove_and_zero_adjacent_noop_keep_optional_legacy_field(self):
        t = montage();tr.set_transition(t, row(out=1));self.assertEqual([row(out=1)], t['transitions'])
        tr.remove_transition(t, {'right_clip_id': 'b'});self.assertEqual([], t['transitions'])
        with self.assertRaises(timeline.TimelineError):tr.remove_transition(t, {'right_clip_id': 'b'})
        legacy = {'clips': [{'id': 'a', 'start_frame': 0, 'duration_frames': 8}, {'id': 'b', 'start_frame': 8, 'duration_frames': 8}]}
        tr.prune(legacy);self.assertNotIn('transitions', legacy)
        tr.set_transition(legacy, row(out=0, pause=0, incoming=0));self.assertNotIn('transitions', legacy)

    def test_prune_only_broken_adjacency_retains_short_invalid_envelopes_for_validation(self):
        t = montage();t['transitions'].append(row('b', 'c', out=1, pause=2, incoming=1))
        t['clips'] = [c for c in t['clips'] if c['id'] != 'a'];tr.prune(t)
        self.assertEqual([row('b', 'c', out=1, pause=2, incoming=1)], t['transitions'])
        t['clips'][1]['duration_frames'] = 1;tr.prune(t)
        self.assertEqual(1, len(t['transitions']))
        with self.assertRaises(timeline.TimelineError):tr.validate(t)


class TransitionStoreTests(ScenarioFixture):
    def op(self, action, **payload):
        return self.store.timeline_operation(self.pid, self.current()['revision'], action, payload)

    def refused(self, action, payload, revision=None, status=422):
        before, sources = self.project_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'] if revision is None else revision, action, payload)
        self.assertEqual(status, caught.exception.status);self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def transition(self, left='c1', right='c2', out=4, pause=16, incoming=4):
        return row(left, right, out, pause, incoming)

    def test_set_and_remove_are_visual_only_and_legacy_reads_do_not_migrate(self):
        p = self.current();p['timeline']['clips'][2]['anchor_frame'] = 12
        aid = p['timeline']['music']['asset_id']
        p['timeline']['sound_events'] = [{'id': 'sound', 'name': 'SFX', 'segment_id': 's1', 'lane': 'sfx', 'asset_id': aid,
            'stream': 0, 'offset_frames': 24, 'source_in_frame': 0, 'duration_frames': 24, 'source_seconds': 4, 'gain': .4, 'muted': False}]
        self.store.commit(p, p['revision'], 'fixture', 'Preserved independent state')
        before, files, sources = self.current(), self.project_state(), self.source_state()
        tr.windows(timeline.get(before));tr.joins(timeline.get(before));scenario.get(before)
        self.assertNotIn('transitions', self.current()['timeline']);self.assertEqual(files, self.project_state())
        p = self.op('set_transition', **self.transition())
        for key in ['segments', 'clips', 'music', 'markers', 'sound_events', 'clip_audio']:
            self.assertEqual(before['timeline'][key], p['timeline'][key], key)
        self.assertEqual(before['blocks'], p['blocks']);self.assertEqual(before['assets'], p['assets'])
        self.assertEqual([self.transition()], p['timeline']['transitions'])
        p = self.op('remove_transition', right_clip_id='c2');self.assertEqual([], p['timeline']['transitions'])
        self.op('undo');self.assertEqual([self.transition()], self.current()['timeline']['transitions'])
        self.assertEqual(sources, self.source_state());self.assert_no_generation()

    def test_invalid_transition_and_conflict_are_atomic_and_cross_segment_touch_is_valid(self):
        self.op('set_transition', **self.transition())
        for invalid in [self.transition(pause=11), self.transition(incoming=25), self.transition(out=25), self.transition(left='c1', right='c3')]:
            self.refused('set_transition', invalid)
        self.refused('set_transition', self.transition(), self.current()['revision'] - 1, 409)
        self.refused('remove_transition', {'right_clip_id': 'c2'}, True, 409)
        self.op('set_transition', **self.transition('c2', 'c3', out=0, pause=80, incoming=4))
        self.refused('set_transition', self.transition('c2', 'c3', out=0, pause=81, incoming=4))
        self.assertEqual(2, len(self.current()['timeline']['transitions']))
        self.activate();self.edit('update_item', segment_id='s1', id='c1', duration_frames=None)
        self.refused('set_transition', self.transition())

    def test_remove_split_reorder_prune_only_broken_pairs_and_undo_restores(self):
        first, cross = self.transition(), self.transition('c2', 'c3', out=4, pause=64, incoming=4)
        self.op('set_transition', **first);self.op('set_transition', **cross)
        p = self.op('remove_clip', id='c1');self.assertEqual([cross], p['timeline']['transitions'])
        self.op('undo');self.assertEqual([first, cross], self.current()['timeline']['transitions'])
        p = self.op('split_clip', id='c1', frame=24);self.assertEqual([cross], p['timeline']['transitions'])
        self.op('undo');self.assertEqual([first, cross], self.current()['timeline']['transitions'])
        p = self.edit('reorder_items', segment_id='s1', order=['c2', 'c1']);self.assertEqual([], p['timeline']['transitions'])
        self.op('undo');self.assertEqual([first, cross], self.current()['timeline']['transitions']);self.assert_no_generation()

    def test_short_duration_rejects_atomically_but_valid_compaction_keeps_pair_and_undo(self):
        self.activate();self.op('set_transition', **self.transition())
        self.refused('edit_clip_source', {'id': 'c1', 'source_in_frame': 0, 'source_out_frame': 7, 'speed': 1})
        self.refused('update_clip', {'id': 'c2', 'duration_frames': 3})
        before = self.current()
        p = self.op('compact_segment', segment_id='s1')
        self.assertEqual(36, next(c for c in p['timeline']['clips'] if c['id'] == 'c2')['start_frame'])
        self.assertEqual([self.transition()], p['timeline']['transitions'])
        p = self.op('undo');self.assertEqual(before['timeline']['clips'], p['timeline']['clips'])
        self.assertEqual(before['timeline']['transitions'], p['timeline']['transitions']);self.assert_no_generation()

    def test_export_freezes_global_cross_segment_windows_and_request_is_detached(self):
        cross = self.transition('c2', 'c3', out=4, pause=64, incoming=4)
        self.op('set_transition', **cross)
        p = self.current();c3 = next(c for c in p['timeline']['clips'] if c['id'] == 'c3')
        c3.update(block_id=self.bid, take_id=self.take['id'])
        self.store.commit(p, p['revision'], 'fixture', 'Assigned third source')
        before = self.project_state();request = export_plan.plan(self.store, self.pid, self.current()['revision'])
        w = request['transition_windows'][0]
        self.assertEqual((64, 68, 132, 136), tuple(w[k] for k in ['out_start_frame', 'black_start_frame', 'right_start_frame', 'in_end_frame']))
        request['transitions'][0]['pause_frames'] = 99;w['black_start_frame'] = 999
        self.assertEqual(before, self.project_state());self.assertEqual([cross], self.current()['timeline']['transitions'])

    def test_real_cpu_export_midpoint_rgb_cached_slow_frames_black_pause_and_unmodified_audio(self):
        import av
        import numpy as np
        source = self.book / 'gray.mkv'
        with av.open(str(source), 'w') as output:
            stream = output.add_stream('ffv1', rate=8);stream.width = 32;stream.height = 32;stream.pix_fmt = 'bgr0'
            for index in range(4):
                frame = av.VideoFrame.from_ndarray(np.full((32, 32, 3), 200, np.uint8), format='rgb24')
                frame.pts = index;frame.time_base = fractions.Fraction(1, 8)
                for packet in stream.encode(frame):output.mux(packet)
            for packet in stream.encode(None):output.mux(packet)
        music = self.book / 'continuous.wav';rate = 48000
        values = (.2 * np.sin(2 * np.pi * 1000 * np.arange(108000) / rate) * 32767).astype(np.int16)
        write_wav(music, [(int(n), int(n)) for n in values], rate=rate)
        clips = [{'id': cid, 'asset_id': 'v', 'start_frame': start, 'duration_frames': 8, 'source_in_frame': 0,
                  'source_out_frame': 2, 'speed': .25} for cid, start in [('a', 0), ('b', 10)]]
        request = {'book': str(self.book), 'fps': 8, 'frames': 18, 'size': [32, 32], 'timeout_seconds': 15,
                   'clips': clips, 'clip_audio': False, 'assets': {'v': {'path': str(source)}, 'm': {'path': str(music)}},
                   'music': {'asset_id': 'm', 'start_frame': 0, 'source_in_frame': 0, 'duration_frames': 18, 'gain': 1, 'muted': False},
                   'transition_windows': tr.windows({'clips': clips, 'transitions': [row()]})}
        folder = self.book / 'FadeExport';folder.mkdir();atomic(folder / 'Request.json', request)
        hashes = {p: digest(p) for p in [source, music]};result = DraftRenderer(folder).run()
        self.assertEqual((18, 8, 2.25), tuple(result['video_info'][k] for k in ['frames', 'fps', 'seconds']))
        with av.open(str(folder / 'Draft.mp4')) as output:frames = [f.to_ndarray(format='rgb24') for f in output.decode(video=0)]
        expected = [200] * 4 + [150, 50] + [0] * 4 + [50, 150] + [200] * 6
        self.assertEqual(18, len(frames))
        for index, level in enumerate(expected):self.assertAlmostEqual(level, float(frames[index][16, 16].mean()), delta=12, msg=f'frame{index}')
        with av.open(str(folder / 'Draft.mp4')) as output:audio = np.concatenate([f.to_ndarray() for f in output.decode(audio=0)], axis=1)[0]
        rms = lambda lo, hi: float(np.sqrt(np.mean(audio[round(lo * rate):round(hi * rate)] ** 2)))
        baseline, blackout, incoming = rms(.15, .35), rms(.85, 1.15), rms(1.3, 1.45)
        self.assertAlmostEqual(.2 / math.sqrt(2), baseline, delta=.008)
        self.assertAlmostEqual(baseline, blackout, delta=.004);self.assertAlmostEqual(baseline, incoming, delta=.004)
        self.assertEqual(hashes, {p: digest(p) for p in hashes});self.assert_no_generation()

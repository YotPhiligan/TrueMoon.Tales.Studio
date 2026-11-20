import copy
import math
import unittest
from unittest.mock import patch

from scenario_fixtures import ScenarioFixture
from studio import clip_edit as ce, scenario, timeline
from studio.model import StudioError


class ClipEditMathTests(unittest.TestCase):
    def test_speed_and_source_frame_accept_exact_boundaries_and_reject_nonfinite_or_bool(self):
        self.assertEqual(1, ce.speed({}))
        for rate in [.25, 1, 1.25, 4]:
            with self.subTest(rate=rate):
                self.assertEqual(rate, ce.speed({'speed': rate}))
        for value in [0, .25, ce.MAX_FRAME]:
            self.assertEqual(value, ce.source_frame(value))
        for value in [True, False, None, '1', math.nan, math.inf, -math.inf, .249999, 4.000001]:
            with self.subTest(invalid_rate=value), self.assertRaises(ce.ClipEditError):
                ce.speed({'speed': value})
        for value in [True, None, '0', math.nan, math.inf, -0.0001, ce.MAX_FRAME + .001]:
            with self.subTest(invalid_source=value), self.assertRaises(ce.ClipEditError):
                ce.source_frame(value)

    def test_source_patch_exclusive_out_and_rounding_are_exact_and_do_not_mutate_input(self):
        item = {'take_id': 'take', 'source_in_frame': 0, 'duration_frames': 24}
        original = copy.deepcopy(item)
        self.assertEqual(24, ce.source_out(item))
        for span, rate, expected in [(24, .25, 96), (24, 4, 6), (8.125, 1.25, 7),
                                     (1.0000000001, 1, 1), (1.000001, 1, 2)]:
            with self.subTest(span=span, rate=rate):
                self.assertEqual(expected, ce.duration(span, rate))
        result = ce.source_patch(item, 1.25, 6, 1.5, 6)
        self.assertEqual({'source_in_frame': 1.25, 'source_out_frame': 6,
                          'speed': 1.5, 'duration_frames': 4}, result)
        ce.validate({**item, **result}, 6)
        self.assertEqual(original, item)
        for start, end, rate, cap in [(0, 6.001, 1, 6), (2, 2, 1, 6), (3, 2, 1, 6),
                                      (-.1, 3, 1, 6), (0, 3, .2, 6), (0, math.inf, 1, 6)]:
            with self.subTest(start=start, end=end, rate=rate), self.assertRaises(ce.ClipEditError):
                ce.source_patch(item, start, end, rate, cap)
        with self.assertRaises(ce.ClipEditError):
            ce.source_patch({**item, 'take_id': None}, 0, 12, 1, 24)

    def test_validate_detects_inconsistent_span_and_adjacent_capacity_overrun(self):
        legacy = {'source_in_frame': 72, 'duration_frames': 24}
        ce.validate(legacy, 96)
        for changed in [{'source_in_frame': 73}, {'source_out_frame': 95},
                        {'speed': 2}, {'source_out_frame': 72}]:
            with self.subTest(changed=changed), self.assertRaises(ce.ClipEditError):
                ce.validate({**legacy, **changed}, 96)

    def test_split_fractional_retime_conserves_span_and_grid_duration_without_mutating(self):
        item = {'take_id': 'take', 'start_frame': 12, 'duration_frames': 7,
                'source_in_frame': 2.25, 'source_out_frame': 10.375, 'speed': 1.25}
        original = copy.deepcopy(item)
        left, right = ce.split(item, 15)
        self.assertEqual({'duration_frames': 3, 'source_out_frame': 6, 'speed': 1.25}, left)
        self.assertEqual({'duration_frames': 4, 'source_in_frame': 6,
                          'source_out_frame': 10.375, 'speed': 1.25}, right)
        self.assertEqual(item['duration_frames'], left['duration_frames'] + right['duration_frames'])
        self.assertEqual(item['source_out_frame'] - item['source_in_frame'],
                         left['source_out_frame'] - item['source_in_frame'] +
                         right['source_out_frame'] - right['source_in_frame'])
        for part in [left, right]:
            ce.validate({**item, **part}, 11)
        self.assertEqual(original, item)
        for cut in [11, 12, 19, 20, 15.0, True, None]:
            with self.subTest(cut=cut), self.assertRaises(ce.ClipEditError):
                ce.split(item, cut)

    def test_split_legacy_and_placeholder_keep_legacy_shape_and_source_offsets(self):
        item = {'take_id': 'take', 'start_frame': 10, 'duration_frames': 8, 'source_in_frame': 7}
        self.assertEqual(({'duration_frames': 1}, {'duration_frames': 7, 'source_in_frame': 8}), ce.split(item, 11))
        self.assertEqual(({'duration_frames': 7}, {'duration_frames': 1, 'source_in_frame': 0}),
                         ce.split({**item, 'take_id': None, 'source_in_frame': 0}, 17))

    def test_sync_interval_recalculates_only_implicit_end_and_clears_placeholder_retime(self):
        item = {'take_id': 'take', 'source_in_frame': 1.5, 'source_out_frame': 9,
                'duration_frames': 6, 'speed': 1.25}
        previous = copy.deepcopy(item)
        ce.sync_interval(item, {'description': 'Text only'}, previous)
        self.assertEqual(previous, item)
        item['duration_frames'] = 4
        ce.sync_interval(item, {'duration_frames': 4}, previous)
        self.assertEqual(6.5, item['source_out_frame'])
        item['source_out_frame'] = 6.25
        ce.sync_interval(item, {'source_in_frame': 1.5, 'source_out_frame': 6.25}, previous)
        self.assertEqual(6.25, item['source_out_frame'])
        previous = copy.deepcopy(item)
        item['source_in_frame'] += .5
        ce.sync_interval(item, {'source_in_frame': item['source_in_frame']}, previous)
        self.assertEqual(6.75, item['source_out_frame'])
        previous = copy.deepcopy(item)
        item['speed'] = 2
        ce.sync_interval(item, {'speed': 2}, previous)
        self.assertEqual((6.75, 3), (item['source_out_frame'], item['duration_frames']))
        item['take_id'] = None
        ce.sync_interval(item, {})
        self.assertNotIn('speed', item)
        self.assertNotIn('source_out_frame', item)


class MontageEditTests(ScenarioFixture):
    def op(self, action, **payload):
        p = self.current()
        return self.store.timeline_operation(self.pid, p['revision'], action, payload)

    def item(self, ident, p=None):
        return next(i for s in scenario.get(p or self.current())['segments'] for i in s['items'] if i['id'] == ident)

    def refuse(self, action, payload, status=422, revision=None):
        before, sources = self.project_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'] if revision is None else revision, action, payload)
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()

    def assert_sources_preserved(self, before, p):
        for key in ['blocks', 'assets', 'segments']:
            self.assertEqual(before[key], p[key], key)
        for key in ['music', 'markers', 'clip_audio']:
            self.assertEqual(before['timeline'][key], p['timeline'][key], key)
        for original in [c for c in before['timeline']['clips'] if c['segment_id'] == 's2']:
            actual = next(c for c in p['timeline']['clips'] if c['id'] == original['id'])
            self.assertEqual(original, {k: actual[k] for k in original})
        self.assert_no_generation()

    def test_edit_source_adopts_legacy_ripples_one_fixed_window_and_preserves_sources(self):
        original, files = self.current(), self.source_state()
        p = self.op('edit_clip_source', id='c1', source_in_frame=1.25, source_out_frame=61.25, speed=2)
        self.assertTrue(scenario.get(p)['active'])
        first, second = self.item('c1', p), self.item('c2', p)
        self.assertEqual((12, 30, 1.25, 61.25, 2), tuple(first[k] for k in
                         ['start_frame', 'duration_frames', 'source_in_frame', 'source_out_frame', 'speed']))
        self.assertEqual(54, second['start_frame'])
        self.assertEqual((0, 96), tuple(p['timeline']['segments'][0][k] for k in ['start_frame', 'duration_frames']))
        self.assertEqual(len(original['timeline']['undo']) + 1, len(p['timeline']['undo']))
        self.assert_sources_preserved(original, p)
        self.assertEqual(files, self.source_state())

    def test_slip_keeps_duration_rate_and_positions_and_rejects_source_overrun_atomically(self):
        self.op('edit_clip_source', id='c1', source_in_frame=1.25, source_out_frame=13.25, speed=.5)
        before = self.current()
        p = self.op('slip_clip', id='c1', delta_frames=2.5)
        self.assertEqual((3.75, 15.75, .5, 24, 12), tuple(self.item('c1', p)[k] for k in
                         ['source_in_frame', 'source_out_frame', 'speed', 'duration_frames', 'start_frame']))
        self.assertEqual(self.item('c2', before)['start_frame'], self.item('c2', p)['start_frame'])
        for delta in [-4, 81, math.nan, math.inf, True, '2']:
            self.refuse('slip_clip', {'id': 'c1', 'delta_frames': delta})
        self.assert_sources_preserved(before, self.current())

    def test_source_edit_failures_stale_revision_and_wrong_source_are_atomic(self):
        original = self.current()
        for payload in [{'id': 'c1', 'source_out_frame': 97}, {'id': 'c1', 'speed': .2499},
                        {'id': 'c1', 'source_in_frame': 24}, {'id': 'c3', 'source_out_frame': 12},
                        {'id': 'c1', 'speed': True}]:
            self.refuse('edit_clip_source', payload)
        self.refuse('edit_clip_source', {'id': 'missing'}, 404)
        self.refuse('set_gap_protected', {'id': 'c1', 'enabled': True}, 409, original['revision'] - 1)
        self.activate()
        self.refuse('update_clip', {'id': 'c3', 'take_id': self.take['id']}, 404)
        # Original legacy API still rejects a fractional source in without explicit retime fields.
        p = self.current()
        t = timeline.get(p)
        t['clips'][0]['source_in_frame'] = .5
        with self.assertRaises(timeline.TimelineError):
            timeline.validate(p, t)

    def test_explicit_source_probe_is_cpu_only_and_invalid_trim_does_not_persist_metadata(self):
        p = self.current()
        self.block(p)['takes'][0]['video_info'] = {}
        self.store.commit(p, p['revision'], 'fixture', 'Missing metadata')
        before = self.project_state()
        with patch.object(self.store, 'probe_video', return_value={'seconds': 4, 'frames': 96, 'fps': 24}) as probe:
            scenario.get(self.current())
            probe.assert_not_called()
            self.refuse('edit_clip_source', {'id': 'c1', 'source_out_frame': 97})
            self.assertEqual(before, self.project_state())
            p = self.op('edit_clip_source', id='c1', source_in_frame=.5, source_out_frame=24.5, speed=1)
        self.assertEqual(2, probe.call_count)
        self.assertEqual(self.store.resolve_asset(p, self.take['video_asset_id']), probe.call_args.args[0])
        self.assertEqual(4, self.block(p)['takes'][0]['video_info']['seconds'])
        self.assert_no_generation()

    def test_retimed_split_preserves_exact_span_ids_source_and_one_undo(self):
        self.op('edit_clip_source', id='c1', source_in_frame=2.25, source_out_frame=10.375, speed=1.25)
        self.op('set_clip_anchor', id='c1', enabled=True)
        self.op('set_gap_protected', id='c1', enabled=True)
        before, files = self.current(), self.source_state()
        p = self.op('split_clip', id='c1', frame=15)
        items = scenario.get(p)['segments'][0]['items']
        left, right = items[:2]
        self.assertEqual('c1', left['id'])
        self.assertNotIn(right['id'], [c['id'] for c in before['timeline']['clips']])
        self.assertEqual((3, 4, 12, 15, 6, 6, 10.375),
                         (left['duration_frames'], right['duration_frames'], left['start_frame'], right['start_frame'],
                          left['source_out_frame'], right['source_in_frame'], right['source_out_frame']))
        self.assertEqual([self.bid] * 2, [left['block_id'], right['block_id']])
        self.assertEqual([self.take['id']] * 2, [left['take_id'], right['take_id']])
        self.assertEqual([1.25, 1.25], [left['speed'], right['speed']])
        self.assertEqual(12, left['anchor_frame'])
        self.assertTrue(left['gap_protected'])
        self.assertEqual(0, right['gap_before_frames'])
        self.assertNotIn('anchor_frame', right)
        self.assertNotIn('gap_protected', right)
        self.assertEqual(left['title'] + ' · 2', right['title'])
        self.assertEqual(self.item('c2', before)['start_frame'], items[2]['start_frame'])
        self.assertEqual(len(before['timeline']['undo']) + 1, len(p['timeline']['undo']))
        self.assert_sources_preserved(before, p)
        self.assertEqual(files, self.source_state())

    def test_placeholder_split_and_illegal_cut_boundaries_never_change_source_library(self):
        before = self.current()
        for frame in [132, 156, 131, 157, 140.0, True, None]:
            self.refuse('split_clip', {'id': 'c3', 'frame': frame})
        p = self.op('split_clip', id='c3', frame=140)
        items = scenario.get(p)['segments'][1]['items']
        self.assertEqual([8, 16], [i['duration_frames'] for i in items])
        self.assertEqual([132, 140], [i['start_frame'] for i in items])
        self.assertEqual([self.second_bid] * 2, [i['block_id'] for i in items])
        self.assertEqual([None, None], [i['take_id'] for i in items])
        self.assertEqual([0, 0], [i['source_in_frame'] for i in items])
        self.assertEqual(before['blocks'], p['blocks'])
        self.assertEqual(before['assets'], p['assets'])
        self.assert_no_generation()

    def test_anchor_enable_absorbs_unprotected_wait_disable_materializes_without_jump(self):
        p = self.op('set_clip_anchor', id='c2', enabled=True)
        self.assertEqual((48, 48, 0), tuple(self.item('c2', p)[k] for k in ['start_frame', 'anchor_frame', 'gap_before_frames']))
        p = self.op('edit_clip_source', id='c1', source_out_frame=30)
        self.assertEqual(48, self.item('c2', p)['start_frame'])
        p = self.op('set_clip_anchor', id='c2', enabled=False)
        self.assertNotIn('anchor_frame', self.item('c2', p))
        self.assertEqual((48, 6), tuple(self.item('c2', p)[k] for k in ['start_frame', 'gap_before_frames']))
        p = self.op('compact_segment', segment_id='s1')
        self.assertEqual(42, self.item('c2', p)['start_frame'])
        for action in ['set_clip_anchor', 'set_gap_protected']:
            for invalid in [1, 0, None, 'true']:
                self.refuse(action, {'id': 'c2', 'enabled': invalid})

    def test_anchor_conflict_preserves_last_valid_projection_and_group_move_shifts_anchor(self):
        self.op('set_clip_anchor', id='c2', enabled=True)
        before = self.current()
        p = self.op('edit_clip_source', id='c1', source_out_frame=37)
        self.assertEqual([('anchor_overlap', 'c2')], [(i['code'], i['item_id']) for i in scenario.get(p)['issues']])
        self.assertEqual(self.projection(before), self.projection(p))
        self.assertEqual(37, self.item('c1', p)['duration_frames'])
        self.assert_sources_preserved(before, p)
        self.op('undo')
        p = self.op('move_segment', id='s1', start_frame=10)
        self.assertEqual((58, 58), tuple(self.item('c2', p)[k] for k in ['anchor_frame', 'start_frame']))
        self.assertEqual(22, self.item('c1', p)['start_frame'])
        self.refuse('update_clip', {'id': 'c2', 'start_frame': 60})

    def test_compact_respects_first_gap_protection_anchor_and_tail_without_moving_neighbors(self):
        self.op('set_gap_protected', id='c2', enabled=True)
        p = self.op('compact_segment', segment_id='s1')
        self.assertEqual([(12, 12), (48, 12)], [(self.item(i, p)['start_frame'], self.item(i, p)['gap_before_frames']) for i in ['c1', 'c2']])
        self.op('set_clip_anchor', id='c2', enabled=True)
        self.op('set_gap_protected', id='c2', enabled=False)
        before = self.current()
        p = self.op('compact_segment', segment_id='s1')
        self.assertEqual((48, 0, 48), tuple(self.item('c2', p)[k] for k in ['start_frame', 'gap_before_frames', 'anchor_frame']))
        self.assertEqual(24, scenario.get(p)['segments'][0]['remaining_frames'])
        self.assert_sources_preserved(before, p)
        self.op('set_clip_anchor', id='c2', enabled=False)
        before = self.current()
        p = self.op('compact_segment', segment_id='s1')
        self.assertEqual([12, 36], [self.item(i, p)['start_frame'] for i in ['c1', 'c2']])
        self.assertEqual(36, scenario.get(p)['segments'][0]['remaining_frames'])
        self.assertEqual(len(before['timeline']['undo']) + 1, len(p['timeline']['undo']))
        self.assert_sources_preserved(before, p)

    def test_unknown_window_or_duration_rejects_compaction_without_committing(self):
        for field in ['start_frame', 'duration_frames']:
            self.edit('update_segment', id='s1', **{field: None})
            self.refuse('compact_segment', {'segment_id': 's1'})
            self.op('undo')
        self.edit('update_item', segment_id='s1', id='c1', duration_frames=None)
        self.refuse('compact_segment', {'segment_id': 's1'})
        self.refuse('split_clip', {'id': 'c1', 'frame': 14})

    def test_atomic_undo_redo_restores_legacy_and_keeps_later_generation_notes(self):
        before = self.current()
        edited = self.op('split_clip', id='c1', frame=24)
        expected = copy.deepcopy(edited['timeline']['scenario'])
        self.save({'prompt': self.initial_prompt + '\nAuthor later action', 'notes': 'Later source note'})
        changed = self.current()
        p = self.op('undo')
        self.assertIsNone(p['timeline'].get('scenario'))
        self.assertEqual(self.projection(before), self.projection(p))
        self.assertEqual(changed['blocks'], p['blocks'])
        self.assertEqual(changed['assets'], p['assets'])
        p = self.op('redo')
        self.assertEqual(expected, p['timeline']['scenario'])
        self.assertEqual(changed['blocks'], p['blocks'])
        self.assert_no_generation()

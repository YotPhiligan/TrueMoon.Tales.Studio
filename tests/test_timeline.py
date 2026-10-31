"""Frame-accurate montage behavior, with isolated projects and no runner."""
import copy
import math
from fractions import Fraction
from unittest.mock import patch

from fixtures import Fixture
from studio.model import Store, StudioError, digest
from studio.timeline import TimelineError, apply, capacity, get, validate


class TimelineTests(Fixture):
    def setUp(self):
        super().setUp()
        self.take, _ = self.add_take()
        p = self.current()
        self.block(p)['takes'][0]['video_info'] = {'seconds': 2.5, 'frames': 120, 'fps': 24}
        self.store.commit(p, p['revision'], 'fixture', 'Video metadata')

    def op(self, action, **payload):
        return self.store.timeline_operation(self.pid, self.current()['revision'], action, payload)

    def segment(self, start=0, duration=96, name='Монтажный сегмент'):
        p = self.op('add_segment', name=name, start_frame=start, duration_frames=duration)
        return p['timeline']['segments'][-1]['id']

    def clip(self, segment, start=0, duration=24, source=0, take='fixture'):
        p = self.op('add_clip', segment_id=segment, block_id=self.bid,
                    take_id=self.take['id'] if take == 'fixture' else take,
                    start_frame=start, duration_frames=duration, source_in_frame=source)
        return p['timeline']['clips'][-1]['id']

    def montage(self):
        return get(self.current())

    def rejected(self, action, payload, revision=None, status=422):
        before = self.store.location(self.pid).read_bytes()
        history = {f.name: digest(f) for f in (self.store.location(self.pid).parent / 'History').glob('*.json')}
        with self.assertRaises(StudioError) as caught:
            self.store.timeline_operation(self.pid, self.current()['revision'] if revision is None else revision, action, payload)
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.store.location(self.pid).read_bytes(), 'invalid operation wrote the project')
        self.assertEqual(history, {f.name: digest(f) for f in (self.store.location(self.pid).parent / 'History').glob('*.json')})
        self.assertEqual(self.plan_digest, digest(self.plan))

    def test_legacy_default_is_detached_and_does_not_write_or_reuse_production_segments(self):
        p = self.current()
        before = self.store.location(self.pid).read_bytes()
        history = self.store.history(self.pid)
        actual = get(p)
        self.assertEqual({'version': 1, 'fps': 24, 'segments': [], 'clips': [], 'markers': [], 'music': None, 'clip_audio': False, 'undo': [], 'redo': []}, actual)
        actual['markers'].append({'id': 'local', 'name': 'Detached', 'frame': 0})
        self.assertNotIn('timeline', self.current())
        self.assertEqual(before, self.store.location(self.pid).read_bytes())
        self.assertEqual(history, self.store.history(self.pid))
        self.assertEqual([], get(p)['markers'])

    def test_add_update_and_persist_segment_returns_full_project_and_preserves_generation(self):
        before = self.current()
        sid = self.segment(24, 72)
        p = self.op('update_segment', id=sid, name='Бой', color='rose')
        self.assertEqual(before['revision'] + 2, p['revision'])
        self.assertEqual(before['segments'], p['segments'])
        self.assertEqual(before['blocks'], p['blocks'])
        self.assertEqual({'id': sid, 'name': 'Бой', 'color': 'rose', 'start_frame': 24, 'duration_frames': 72}, p['timeline']['segments'][0])
        self.assertEqual(p, Store(self.book, self.amv, self.base / 'state').get(self.pid))

    def test_segment_touch_is_allowed_but_one_frame_overlap_is_atomic(self):
        self.segment(0, 60)
        second = self.segment(60, 24)
        self.assertEqual([0, 60], [s['start_frame'] for s in self.montage()['segments']])
        self.rejected('add_segment', {'name': 'Overlap', 'start_frame': 59, 'duration_frames': 24})
        self.rejected('move_segment', {'id': second, 'start_frame': 59})

    def test_segment_frame_types_and_nonpositive_values_are_refused_before_commit(self):
        for field, values in [('start_frame', [-1, True, 0.5, '24', None]),
                              ('duration_frames', [0, -1, False, 24.0, '24', None])]:
            for value in values:
                payload = {'name': 'Invalid frames', 'start_frame': 0, 'duration_frames': 24, field: value}
                with self.subTest(field=field, value=value):
                    self.rejected('add_segment', payload)

    def test_six_hour_limit_accepts_last_frame_and_refuses_one_frame_past(self):
        limit = 24 * 60 * 60 * 6
        self.segment(limit - 1, 1)
        marker = self.op('add_marker', name='End', frame=limit)['timeline']['markers'][0]
        self.assertEqual(limit, marker['frame'])
        self.rejected('add_segment', {'name': 'Past end', 'start_frame': limit, 'duration_frames': 1})
        self.rejected('add_marker', {'name': 'Past end', 'frame': limit + 1})

    def test_segment_metadata_and_unknown_actions_are_atomic_errors(self):
        sid = self.segment()
        self.op('update_segment', id=sid, name='x' * 160)
        self.assertEqual('x' * 160, self.montage()['segments'][0]['name'])
        for payload in [{'id': sid, 'name': ''}, {'id': sid, 'name': 'x' * 161}, {'id': sid, 'color': 'invalid'}]:
            with self.subTest(payload=payload):
                self.rejected('update_segment', payload)
        self.rejected('unknown_action', {})
        self.rejected('remove_segment', {'id': 'missing'})
        self.rejected('add_marker', None)

    def test_clip_touch_and_exact_segment_edges_are_allowed_one_frame_outside_is_not(self):
        sid = self.segment(24, 60)
        first = self.clip(sid, 24, 30)
        second = self.clip(sid, 54, 30)
        self.assertEqual([24, 54], [c['start_frame'] for c in self.montage()['clips']])
        self.rejected('update_clip', {'id': second, 'start_frame': 53})
        self.rejected('update_clip', {'id': first, 'start_frame': 23})
        self.rejected('update_clip', {'id': second, 'duration_frames': 31})

    def test_move_segment_shifts_its_clip_group_and_preserves_offsets_and_other_segment(self):
        sid = self.segment(24, 96)
        self.clip(sid, 24, 20)
        self.clip(sid, 80, 20, source=20)
        other = self.segment(200, 48)
        self.clip(other, 210, 15)
        before = self.montage()
        p = self.op('move_segment', id=sid, start_frame=72)
        clips = p['timeline']['clips']
        self.assertEqual([72, 128, 210], [c['start_frame'] for c in clips])
        self.assertEqual([0, 56], [c['start_frame'] - 72 for c in clips if c['segment_id'] == sid])
        self.assertEqual([0, 20, 0], [c['source_in_frame'] for c in clips])
        self.assertEqual([{**c, 'start_frame': c['start_frame'] + 48} if c['segment_id'] == sid else c for c in before['clips']], clips)
        self.assertEqual(before['segments'][1], p['timeline']['segments'][1])
        self.assertEqual(before['clips'][2], clips[2])
        self.rejected('move_segment', {'id': sid, 'start_frame': 150})
        self.op('move_segment', id=sid, start_frame=0)
        self.assertEqual([0, 56, 210], [c['start_frame'] for c in self.montage()['clips']])

    def test_resize_changes_window_without_moving_clips_and_requires_containment(self):
        sid = self.segment(24, 120)
        self.clip(sid, 30, 24)
        self.clip(sid, 100, 20)
        clips = self.montage()['clips']
        self.op('resize_segment', id=sid, start_frame=20, duration_frames=110)
        self.assertEqual(clips, self.montage()['clips'])
        self.rejected('resize_segment', {'id': sid, 'start_frame': 31, 'duration_frames': 99})
        self.rejected('resize_segment', {'id': sid, 'duration_frames': 99})
        self.op('resize_segment', id=sid, start_frame=30, duration_frames=90)
        self.assertEqual(clips, self.montage()['clips'])

    def test_edit_segment_combines_group_move_resize_and_metadata_atomically(self):
        sid = self.segment(24, 96)
        self.clip(sid, 30, 24)
        self.clip(sid, 80, 20)
        before = self.montage()
        p = self.op('edit_segment', id=sid, name='Прорыв', color='violet', start_frame=48, duration_frames=100)
        self.assertEqual({'id': sid, 'name': 'Прорыв', 'color': 'violet', 'start_frame': 48, 'duration_frames': 100}, p['timeline']['segments'][0])
        self.assertEqual([54, 104], [c['start_frame'] for c in p['timeline']['clips']])
        self.assertEqual([{**c, 'start_frame': c['start_frame'] + 24} for c in before['clips']], p['timeline']['clips'])
        self.rejected('edit_segment', {'id': sid, 'name': 'Too short', 'color': 'rose', 'start_frame': 72, 'duration_frames': 24})
        self.assertEqual('Прорыв', self.montage()['segments'][0]['name'])
        undone = self.op('undo')['timeline']
        for key in ['segments', 'clips', 'markers']:
            self.assertEqual(before[key], undone[key])
        redone = self.op('redo')['timeline']
        for key in ['segments', 'clips', 'markers']:
            self.assertEqual(p['timeline'][key], redone[key])

    def test_trim_uses_seconds_capacity_exact_source_end_and_does_not_change_review(self):
        sid = self.segment(0, 96)
        before = copy.deepcopy(self.block())
        cid = self.clip(sid, 0, 40, source=20)
        original = self.montage()['clips'][0]
        self.assertEqual(60, capacity(self.take_with_metadata(), 24))
        self.rejected('update_clip', {'id': cid, 'duration_frames': 41})
        self.rejected('update_clip', {'id': cid, 'source_in_frame': -1})
        self.op('update_clip', id=cid, source_in_frame=30, duration_frames=30)
        self.assertEqual({**original, 'source_in_frame': 30, 'duration_frames': 30}, self.montage()['clips'][0])
        self.assertEqual(before, self.block())
        self.assertEqual('unreviewed', self.block()['takes'][0]['review_state'])
        self.assertIsNone(self.block()['selected_take_id'])

    def take_with_metadata(self):
        return self.block()['takes'][0]

    def test_take_must_belong_to_existing_block(self):
        sid = self.segment()
        production = self.current()['segments'][0]['id']
        p = self.store.operation(self.pid, self.current()['revision'], 'add_block', {'segment_id': production, 'label': 'CL-OTHER'})
        other_bid = p['blocks'][-1]['id']
        base = {'segment_id': sid, 'block_id': self.bid, 'take_id': self.take['id'], 'start_frame': 0, 'duration_frames': 24}
        for change in [{'block_id': 'missing'}, {'block_id': other_bid}, {'take_id': 'missing'}, {'segment_id': 'missing'}]:
            with self.subTest(change=change):
                self.rejected('add_clip', {**base, **change})

    def test_placeholder_has_no_source_in_and_can_later_bind_a_take(self):
        sid = self.segment(0, 120)
        cid = self.clip(sid, 0, 96, take=None)
        self.assertIsNone(self.montage()['clips'][0]['take_id'])
        self.rejected('update_clip', {'id': cid, 'source_in_frame': 1})
        self.rejected('update_clip', {'id': cid, 'take_id': self.take['id']})
        self.op('update_clip', id=cid, take_id=self.take['id'], source_in_frame=0, duration_frames=60)
        self.assertEqual(self.take['id'], self.montage()['clips'][0]['take_id'])

    def test_update_clip_transfer_validates_new_segment_and_source_then_preserves_identity(self):
        first = self.segment(0, 60)
        second = self.segment(72, 60)
        cid = self.clip(first, 12, 24, source=6)
        self.rejected('update_clip', {'id': cid, 'segment_id': second})
        self.op('update_clip', id=cid, segment_id=second, start_frame=84, source_in_frame=10, duration_frames=30)
        clip = self.montage()['clips'][0]
        self.assertEqual({'id': cid, 'segment_id': second, 'block_id': self.bid, 'take_id': self.take['id'],
                          'start_frame': 84, 'duration_frames': 30, 'source_in_frame': 10}, clip)

    def test_remove_segment_cascades_only_its_clips_without_deleting_sources_or_markers(self):
        first = self.segment(0, 60)
        second = self.segment(72, 60)
        self.clip(first, 0, 24)
        remaining = self.clip(second, 72, 24)
        marker = self.op('add_marker', name='Cut', frame=24)['timeline']['markers'][0]
        before = self.block()
        assets = self.current()['assets']
        p = self.op('remove_segment', id=first)
        self.assertEqual([second], [s['id'] for s in p['timeline']['segments']])
        self.assertEqual([remaining], [c['id'] for c in p['timeline']['clips']])
        self.assertEqual([marker], p['timeline']['markers'])
        self.assertEqual(before, self.block(p))
        self.assertEqual(assets, p['assets'])
        self.assertTrue(all(self.store.resolve_asset(p, aid).is_file() for aid in assets))

    def test_remove_clip_and_marker_preserve_other_items(self):
        sid = self.segment()
        first = self.clip(sid, 0, 24)
        second = self.clip(sid, 24, 24)
        marker = self.op('add_marker', name='Shot', frame=0)['timeline']['markers'][0]['id']
        self.op('update_marker', id=marker, name='Impact', frame=23)
        self.assertEqual({'id': marker, 'name': 'Impact', 'frame': 23}, self.montage()['markers'][0])
        self.op('remove_clip', id=first)
        self.op('remove_marker', id=marker)
        self.assertEqual([second], [c['id'] for c in self.montage()['clips']])
        self.assertEqual([], self.montage()['markers'])
        self.assertEqual([sid], [s['id'] for s in self.montage()['segments']])

    def test_invalid_clip_and_marker_frames_are_atomic(self):
        sid = self.segment()
        cid = self.clip(sid)
        marker = self.op('add_marker', name='Beat', frame=0)['timeline']['markers'][0]['id']
        for field, value in [('start_frame', True), ('duration_frames', 0), ('duration_frames', 1.5), ('source_in_frame', '0')]:
            with self.subTest(field=field, value=value):
                self.rejected('update_clip', {'id': cid, field: value})
        for change in [{'frame': -1}, {'frame': False}, {'frame': 24.0}, {'name': ''}]:
            with self.subTest(change=change):
                self.rejected('update_marker', {'id': marker, **change})
        self.rejected('remove_clip', {'id': 'missing'})
        self.rejected('update_marker', {'id': 'missing', 'name': 'Unknown'})

    def test_undo_redo_only_timeline_after_unrelated_notes_take_and_generation_edits(self):
        sid = self.segment()
        cid = self.clip(sid, 0, 24)
        before = self.montage()['clips'][0]
        self.op('update_clip', id=cid, start_frame=24, source_in_frame=10)
        p = self.current()
        block = self.block(p)
        block['notes'] = 'Сохранить авторскую правку'
        block['status'] = 'generation-ready'
        block['settings']['seed'] = '42'
        block['takes'].append({**block['takes'][0], 'id': 'later-take', 'notes': 'Later generation'})
        p = self.store.commit(p, p['revision'], 'author', 'Unrelated edit')
        unrelated = copy.deepcopy(p['blocks'])
        undone = self.op('undo')
        self.assertEqual(before, undone['timeline']['clips'][0])
        self.assertEqual(unrelated, undone['blocks'])
        redone = self.op('redo')
        self.assertEqual((24, 10), tuple(redone['timeline']['clips'][0][k] for k in ['start_frame', 'source_in_frame']))
        self.assertEqual(unrelated, redone['blocks'])
        self.assertEqual(p['revision'] + 2, redone['revision'])

    def test_empty_undo_redo_and_branch_after_undo_have_atomic_boundaries(self):
        self.rejected('undo', {})
        self.rejected('redo', {})
        self.segment()
        self.op('add_marker', name='Old future', frame=24)
        self.op('undo')
        self.op('add_marker', name='New branch', frame=48)
        self.assertEqual([], self.montage()['redo'])
        self.rejected('redo', {})
        self.assertEqual(['New branch'], [m['name'] for m in self.montage()['markers']])

    def test_stale_revision_does_not_undo_or_overwrite_newer_timeline(self):
        revision = self.current()['revision']
        sid = self.segment()
        self.rejected('move_segment', {'id': sid, 'start_frame': 100}, revision=revision, status=409)
        self.rejected('undo', {}, revision=revision, status=409)
        self.rejected('add_marker', {'name': 'Bad revision type', 'frame': 0}, revision=True, status=409)

    def test_timeline_edits_do_not_prepare_launch_or_create_job_folders(self):
        before = self.block()
        with patch.object(self.jobs, 'command') as command, patch.object(self.jobs, 'launch') as launch:
            sid = self.segment()
            self.clip(sid)
            self.op('move_segment', id=sid, start_frame=24)
            self.op('undo')
            self.op('redo')
        command.assert_not_called()
        launch.assert_not_called()
        self.assertEqual(before, self.block())
        self.assertFalse((self.store.location(self.pid).parent / 'Jobs').exists())

    def test_legacy_add_and_update_bindings_probe_once_preserve_review_and_reuse_metadata(self):
        sid = self.segment(0, 120)
        placeholder = self.clip(sid, 0, 60, take=None)
        info = {'frames': 60, 'fps': 24.0, 'seconds': 2.5, 'width': 32, 'height': 24, 'silent': True, 'metadata_source': 'media_header'}
        operations = [('add_clip', {'segment_id': sid, 'block_id': self.bid, 'take_id': self.take['id'], 'start_frame': 60, 'duration_frames': 60}),
                      ('update_clip', {'id': placeholder, 'take_id': self.take['id']})]
        for action, payload in operations:
            with self.subTest(action=action):
                p = self.current()
                self.block(p)['takes'][0]['video_info'] = {}
                self.store.commit(p, p['revision'], 'fixture', 'Legacy metadata')
                before = copy.deepcopy(self.block())
                video = self.store.resolve_asset(self.current(), self.take['video_asset_id'])
                with patch.object(self.store, 'probe_video', return_value=info) as probe:
                    result = self.op(action, **payload)
                probe.assert_called_once_with(video)
                expected = copy.deepcopy(before)
                expected['takes'][0]['video_info'] = info
                self.assertEqual(expected, self.block(result))
                self.assertEqual(info, self.block()['takes'][0]['video_info'])
                self.assertEqual('unreviewed', self.block()['takes'][0]['review_state'])
                self.assertIsNone(self.block()['selected_take_id'])
        self.assertEqual([self.take['id'], self.take['id']], [c['take_id'] for c in self.montage()['clips']])
        with patch.object(self.store, 'probe_video') as probe:
            self.op('update_clip', id=placeholder, take_id=self.take['id'], start_frame=1, duration_frames=59)
        probe.assert_not_called()

    def test_failed_legacy_add_and_update_trim_never_persist_probed_metadata(self):
        sid = self.segment(0, 120)
        placeholder = self.clip(sid, 0, 24, take=None)
        p = self.current()
        self.block(p)['takes'][0]['video_info'] = {}
        self.store.commit(p, p['revision'], 'fixture', 'Legacy metadata')
        info = {'seconds': 2.5, 'frames': 60, 'fps': 24}
        operations = [('add_clip', {'segment_id': sid, 'block_id': self.bid, 'take_id': self.take['id'],
                                    'start_frame': 24, 'duration_frames': 61}),
                      ('update_clip', {'id': placeholder, 'take_id': self.take['id'], 'duration_frames': 61})]
        for action, payload in operations:
            with self.subTest(action=action), patch.object(self.store, 'probe_video', return_value=info) as probe:
                self.rejected(action, payload)
                probe.assert_called_once()
                self.assertEqual({}, self.block()['takes'][0]['video_info'])
                self.assertEqual([placeholder], [c['id'] for c in self.montage()['clips']])
                self.assertIsNone(self.montage()['clips'][0]['take_id'])


class TimelinePureTests(Fixture):
    def test_probe_video_reads_tiny_cpu_mp4_and_rejects_damaged_file_without_changes(self):
        import av

        path = self.book / 'tiny.mp4'
        with av.open(str(path), mode='w') as container:
            stream = container.add_stream('mpeg4', rate=24)
            stream.width = 32
            stream.height = 24
            stream.pix_fmt = 'yuv420p'
            for index in range(6):
                frame = av.VideoFrame(32, 24, 'yuv420p')
                for plane in frame.planes:
                    plane.update(bytes(plane.buffer_size))
                frame.pts = index
                frame.time_base = Fraction(1, 24)
                for packet in stream.encode(frame):
                    container.mux(packet)
            for packet in stream.encode():
                container.mux(packet)
        original = digest(path)
        result = Store.probe_video(path)
        self.assertEqual({'frames': 6, 'fps': 24.0, 'seconds': .25, 'width': 32, 'height': 24,
                          'silent': True, 'metadata_source': 'media_header'}, result)
        self.assertEqual(original, digest(path))
        damaged = self.book / 'damaged.mp4'
        damaged.write_bytes(b'not an MP4 video')
        with self.assertRaises(StudioError) as caught:
            Store.probe_video(damaged)
        self.assertEqual(422, caught.exception.status)
        self.assertEqual(b'not an MP4 video', damaged.read_bytes())

    def test_capacity_converts_source_rate_at_normal_speed_and_prefers_seconds(self):
        cases = [({'seconds': 1, 'frames': 120, 'fps': 24}, 24),
                 ({'frames': 122, 'fps': 60}, 48),
                 ({'seconds': 2.9999999999999996}, 72),
                 ({'seconds': 1.001}, 24)]
        for info, expected in cases:
            with self.subTest(info=info):
                self.assertEqual(expected, capacity({'video_info': info}, 24))

    def test_unknown_or_invalid_source_duration_is_a_timeline_error(self):
        for info in [{}, {'seconds': 0}, {'seconds': -1}, {'seconds': True}, {'seconds': '2'},
                     {'seconds': math.nan}, {'seconds': math.inf}, {'frames': 60, 'fps': 0},
                     {'frames': '60', 'fps': 24}, {'frames': 60, 'fps': '24'}, {'frames': True, 'fps': 24},
                     {'frames': 60, 'fps': True}]:
            with self.subTest(info=info), self.assertRaises(TimelineError):
                capacity({'video_info': info}, 24)

    def test_apply_keeps_input_immutable_and_history_is_bounded(self):
        p = self.current()
        original = copy.deepcopy(p)
        t = apply(p, 'add_marker', {'name': 'M0', 'frame': 0})
        self.assertEqual(original, p)
        for index in range(1, 32):
            t = apply({**p, 'timeline': t}, 'add_marker', {'name': f'M{index}', 'frame': index})
        self.assertEqual(32, len(t['markers']))
        self.assertEqual(30, len(t['undo']))
        for _ in range(30):
            t = apply({**p, 'timeline': t}, 'undo', {})
        self.assertEqual(['M0', 'M1'], [m['name'] for m in t['markers']])
        self.assertEqual(30, len(t['redo']))
        with self.assertRaises(TimelineError):
            apply({**p, 'timeline': t}, 'undo', {})

    def test_validate_rejects_fps_mismatch_duplicate_ids_and_unknown_source_links(self):
        p = self.current()
        t = apply(p, 'add_segment', {'name': 'Only', 'start_frame': 0, 'duration_frames': 24})
        mismatched = copy.deepcopy(t)
        mismatched['fps'] = 30
        duplicate = copy.deepcopy(t)
        duplicate['segments'].append(copy.deepcopy(t['segments'][0]))
        missing_source = copy.deepcopy(t)
        missing_source['clips'].append({'id': 'c', 'segment_id': t['segments'][0]['id'], 'block_id': 'missing',
                                       'take_id': None, 'start_frame': 0, 'duration_frames': 1, 'source_in_frame': 0})
        for invalid in [mismatched, duplicate, missing_source]:
            with self.subTest(invalid=invalid), self.assertRaises(TimelineError):
                validate(p, invalid)

    def test_noop_edit_preserves_undo_and_redo_content(self):
        p = self.current()
        t = apply(p, 'add_segment', {'name': 'Only', 'start_frame': 0, 'duration_frames': 24})
        t = apply({**p, 'timeline': t}, 'add_marker', {'name': 'Future', 'frame': 0})
        t = apply({**p, 'timeline': t}, 'undo', {})
        before = copy.deepcopy(t)
        result = apply({**p, 'timeline': t}, 'update_segment', {'id': t['segments'][0]['id'], 'name': 'Only'})
        self.assertEqual(before, result)
        self.assertEqual(before, t)

    def test_collection_capacity_accepts_limit_and_refuses_the_next_element(self):
        p = self.current()
        for kind, limit in [('segments', 200), ('clips', 2000), ('markers', 1000)]:
            with self.subTest(kind=kind):
                t = get(p)
                if kind == 'segments':
                    row = lambda index: {'id': f's{index}', 'name': f'S{index}', 'start_frame': index * 2, 'duration_frames': 1, 'color': 'mint'}
                elif kind == 'clips':
                    t['segments'] = [{'id': 'owner', 'name': 'Owner', 'start_frame': 0, 'duration_frames': limit + 1, 'color': 'mint'}]
                    row = lambda index: {'id': f'c{index}', 'segment_id': 'owner', 'block_id': self.bid, 'take_id': None,
                                         'start_frame': index, 'duration_frames': 1, 'source_in_frame': 0}
                else:
                    row = lambda index: {'id': f'm{index}', 'name': f'M{index}', 'frame': index}
                t[kind] = [row(index) for index in range(limit)]
                validate(p, t)
                t[kind].append(row(limit))
                with self.assertRaises(TimelineError):
                    validate(p, t)

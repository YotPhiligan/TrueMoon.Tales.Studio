import copy
from unittest.mock import patch
from scenario_fixtures import ScenarioFixture
from studio import scenario
from studio.model import StudioError


class ScenarioTests(ScenarioFixture):
    def test_legacy_get_is_read_only_adopts_only_placed_items_and_preserves_ids_gaps(self):
        before, sources = self.project_state(), self.source_state()
        p = self.current()
        original = copy.deepcopy(p)
        view = scenario.get(p)
        self.assertIs(view['active'], False)
        self.assertEqual([], view['issues'])
        self.assertEqual(['s1', 's2'], [s['id'] for s in view['segments']])
        first = view['segments'][0]
        self.assertEqual(('Воспоминание', 0, 96, 72, 24), tuple(first[k] for k in ['name', 'start_frame', 'duration_frames', 'used_frames', 'remaining_frames']))
        self.assertEqual(['c1', 'c2'], [i['id'] for i in first['items']])
        self.assertEqual([12, 12], [i['gap_before_frames'] for i in first['items']])
        self.assertEqual([12, 48], [i['start_frame'] for i in first['items']])
        self.assertNotIn(self.unplaced_bid, [i['block_id'] for s in view['segments'] for i in s['items']])
        self.assertEqual(original, p)
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()
        empty_project = copy.deepcopy(original)
        empty_project['timeline'].update(segments=[], clips=[])
        empty = scenario.get(empty_project)
        self.assertEqual(([], [], False), (empty['segments'], empty['issues'], empty['active']))
        self.assertEqual(original['blocks'], empty_project['blocks'])
        self.assertEqual(before, self.project_state())

    def test_first_edit_is_pure_copy_then_canonical_projection_keeps_sources_and_media(self):
        p = self.current()
        original, state, sources = copy.deepcopy(p), self.project_state(), self.source_state()
        result = scenario.apply(self.store, p, 'update_segment', {'id': 's1', 'name': 'Новая глава'})
        self.assertEqual(original, p)
        self.assertEqual(state, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assertEqual('Новая глава', result['timeline']['scenario']['segments'][0]['name'])
        self.assertEqual(['c1', 'c2', 'c3'], [c['id'] for c in result['timeline']['clips']])
        for before in original['timeline']['clips']:
            after = next(c for c in result['timeline']['clips'] if c['id'] == before['id'])
            self.assertEqual(before, {k: after[k] for k in before})
        self.assertEqual(original['blocks'], result['blocks'])
        self.assertEqual(original['assets'], result['assets'])
        for key in ['music', 'markers', 'clip_audio']:
            self.assertEqual(original['timeline'][key], result['timeline'][key])

    def test_segment_type_projects_clears_and_undoes_without_moving_clips(self):
        original = self.activate()
        typed = self.edit('update_segment', id='s1', segment_type='action')
        view = scenario.get(typed)
        self.assertEqual('action', view['segments'][0]['segment_type'])
        self.assertEqual('action', typed['timeline']['segments'][0]['segment_type'])
        self.assertEqual(original['timeline']['clips'], typed['timeline']['clips'])
        self.assertEqual(original['timeline']['music'], typed['timeline']['music'])
        self.rejected('update_segment', {'id':'s1','segment_type':[]})
        cleared = self.edit('update_segment', id='s1', segment_type=None)
        self.assertNotIn('segment_type', cleared['timeline']['segments'][0])
        restored = self.store.timeline_operation(self.pid,cleared['revision'],'undo',{})
        self.assertEqual('action',scenario.get(restored)['segments'][0]['segment_type'])
        redone = self.store.timeline_operation(self.pid,restored['revision'],'redo',{})
        self.assertNotIn('segment_type',scenario.get(redone)['segments'][0])
        self.assertEqual(original['timeline']['clips'], redone['timeline']['clips'])

    def test_animatic_frame_is_per_placement_and_undoable_without_editing_source(self):
        from studio import library

        original = self.activate()
        frame_dir = self.store.project_dir(self.pid) / 'Frames'
        frame_dir.mkdir(exist_ok=True)
        (frame_dir / 'Still.png').write_bytes(self.images[0].read_bytes())
        invalid = {'segment_id': 's1', 'id': 'c1'}
        self.rejected('set_animatic_frame', invalid)
        self.rejected('set_animatic_frame', {**invalid, 'asset_selection': {'asset_id': self.take['video_asset_id']}})
        changed = self.edit('set_animatic_frame', **invalid, asset_selection={'file': 'Frames/Still.png'})
        first, other_use = scenario.get(changed)['segments'][0]['items']
        aid = first['animatic_asset_id']
        self.assertEqual('image', changed['assets'][aid]['kind'])
        self.assertNotIn('animatic_asset_id', other_use)
        self.assertEqual([('animatic_frame', 's1', 'c1')],
                         [(u['role'], u['segment_id'], u['item_id']) for u in library.usage_index(changed)[aid]])
        self.assertEqual(original['timeline']['clips'], changed['timeline']['clips'])
        self.assertEqual(original['blocks'], changed['blocks'])
        self.assertEqual(original['timeline']['music'], changed['timeline']['music'])
        reverted = self.store.timeline_operation(self.pid, changed['revision'], 'undo', {})
        self.assertNotIn('animatic_asset_id', scenario.get(reverted)['segments'][0]['items'][0])
        restored = self.store.timeline_operation(self.pid, reverted['revision'], 'redo', {})
        self.assertEqual(aid, scenario.get(restored)['segments'][0]['items'][0]['animatic_asset_id'])
        cleared = self.edit('set_animatic_frame', **invalid, asset_selection=None)
        self.assertNotIn('animatic_asset_id', scenario.get(cleared)['segments'][0]['items'][0])
        self.assert_no_generation()

    def test_item_notes_survive_context_partial_edits_undo_and_split(self):
        from studio import agent

        original = self.activate()
        notes = {'sound_notes': '«Принято».\nДождь стихает.', 'continuity_notes': 'Карабин остаётся в руках.'}
        self.rejected('update_item', {'segment_id': 's1', 'id': 'c1', 'sound_notes': None})
        self.rejected('update_item', {'segment_id': 's1', 'id': 'c1', 'continuity_notes': 'а' * 12001})
        with patch.object(self.store, 'probe_video', side_effect=AssertionError('Text edit must not probe video')):
            edited = self.edit('update_item', segment_id='s1', id='c1', **notes)
        self.assertEqual(original['timeline']['clips'], edited['timeline']['clips'])
        self.assertEqual(original['blocks'], edited['blocks'])
        context = agent.context(self.store, self.pid, sid='s1', iid='c1')
        self.assertEqual(notes, {key: context['scenario_item'][key] for key in notes})
        other_use = context['scenario_segment']['items'][1]
        self.assertEqual('c2', other_use['id'])
        self.assertTrue(set(notes).isdisjoint(other_use))

        self.edit('update_item', segment_id='s1', id='c1', description='Новое действие')
        cleared = self.edit('update_item', segment_id='s1', id='c1', sound_notes='')
        item = scenario.get(cleared)['segments'][0]['items'][0]
        self.assertNotIn('sound_notes', item)
        self.assertEqual(notes['continuity_notes'], item['continuity_notes'])
        restored = self.store.timeline_operation(self.pid, cleared['revision'], 'undo', {})
        item = scenario.get(restored)['segments'][0]['items'][0]
        self.assertEqual(notes, {key: item[key] for key in notes})

        divided = self.store.timeline_operation(self.pid, restored['revision'], 'split_clip', {'id': 'c1', 'frame': 24})
        left, right, _ = scenario.get(divided)['segments'][0]['items']
        self.assertEqual(notes['sound_notes'], left['sound_notes'])
        self.assertEqual(notes['continuity_notes'], right['continuity_notes'])
        self.assertNotIn('continuity_notes', left)
        self.assertNotIn('sound_notes', right)
        self.assert_no_generation()

    def test_duration_and_explicit_gap_edits_ripple_items_inside_fixed_window(self):
        self.activate()
        p = self.edit('update_item', segment_id='s1', id='c1', duration_frames=36, gap_before_frames=8)
        items = scenario.get(p)['segments'][0]['items']
        self.assertEqual([(8, 44), (56, 80)], [(i['start_frame'], i['end_frame']) for i in items])
        self.assertEqual((80, 16), tuple(scenario.get(p)['segments'][0][k] for k in ['used_frames', 'remaining_frames']))
        self.assertEqual((0, 96), (p['timeline']['segments'][0]['start_frame'], p['timeline']['segments'][0]['duration_frames']))
        self.assertEqual([0, 12], [c['source_in_frame'] for c in p['timeline']['clips'][:2]])

    def test_overflow_one_frame_persists_intent_without_truncating_last_valid_projection(self):
        self.activate()
        before = self.projection()
        p = self.edit('update_item', segment_id='s1', id='c2', duration_frames=49)
        view = scenario.get(p)
        self.assertEqual(['overflow'], [i['code'] for i in view['issues']])
        self.assertEqual(-1, view['segments'][0]['remaining_frames'])
        self.assertEqual(49, view['segments'][0]['items'][1]['duration_frames'])
        self.assertEqual(before, self.projection(p))
        p = self.edit('update_item', segment_id='s1', id='c2', duration_frames=48)
        self.assertEqual([], scenario.get(p)['issues'])
        self.assertEqual((48, 48), tuple(next(c for c in p['timeline']['clips'] if c['id'] == 'c2')[k] for k in ['start_frame', 'duration_frames']))

    def test_short_source_and_metadata_issues_preserve_projection_and_do_not_probe_on_get(self):
        self.activate()
        before = self.projection()
        p = self.edit('update_item', segment_id='s1', id='c2', source_in_frame=73)
        issues = scenario.get(p)['issues']
        self.assertEqual([('source_short', 'c2')], [(i['code'], i['item_id']) for i in issues])
        self.assertEqual(before, self.projection(p))
        p = self.edit('update_item', segment_id='s1', id='c2', source_in_frame=72)
        self.assertEqual([], scenario.get(p)['issues'])
        self.block(p)['takes'][0]['video_info'] = {}
        self.store.commit(p, p['revision'], 'fixture', 'Unknown source metadata')
        with patch.object(self.store, 'probe_video') as probe:
            view = self.view()
        self.assertEqual({'source_metadata'}, {i['code'] for i in view['issues']})
        probe.assert_not_called()

    def test_overlap_and_range_are_visible_and_keep_last_projection_until_repaired(self):
        self.activate()
        before = self.projection()
        p = self.edit('update_segment', id='s2', start_frame=95)
        self.assertEqual(['segment_overlap'], [i['code'] for i in scenario.get(p)['issues']])
        self.assertEqual(before, self.projection(p))
        p = self.edit('update_segment', id='s2', start_frame=96)
        self.assertEqual([], scenario.get(p)['issues'])
        self.assertEqual(108, next(c for c in p['timeline']['clips'] if c['id'] == 'c3')['start_frame'])
        before = self.projection()
        limit = 24 * 60 * 60 * 6
        p = self.edit('update_segment', id='s2', start_frame=limit - 71)
        self.assertEqual(['range'], [i['code'] for i in scenario.get(p)['issues']])
        self.assertEqual(before, self.projection(p))

    def test_unknown_item_duration_propagates_unknown_positions_and_null_window_is_unplaced(self):
        self.activate()
        before = self.projection()
        p = self.edit('update_item', segment_id='s1', id='c1', duration_frames=None)
        segment = scenario.get(p)['segments'][0]
        self.assertEqual([(12, None), (None, None)], [(i['start_frame'], i['end_frame']) for i in segment['items']])
        self.assertIsNone(segment['used_frames'])
        self.assertIsNone(segment['remaining_frames'])
        self.assertEqual(['unknown_duration'], [i['code'] for i in scenario.get(p)['issues']])
        self.assertEqual(before, self.projection(p))
        self.edit('update_item', segment_id='s1', id='c1', duration_frames=24)
        p = self.edit('update_segment', id='s1', start_frame=None)
        self.assertEqual([None, None], [i['start_frame'] for i in scenario.get(p)['segments'][0]['items']])
        self.assertEqual(['unplaced_segment'], [i['code'] for i in scenario.get(p)['issues']])
        self.assertEqual(before, self.projection(p))
        p = self.edit('update_segment', id='s1', start_frame=0, duration_frames=None)
        view = scenario.get(p)
        self.assertEqual(['unplaced_segment'], [i['code'] for i in view['issues']])
        self.assertIsNone(view['segments'][0]['remaining_frames'])
        self.assertEqual(before, self.projection(p))

    def test_reorder_and_remove_actions_ripple_items_but_preserve_library_sources(self):
        self.activate()
        sources = {k: copy.deepcopy(self.current()[k]) for k in ['blocks', 'assets']}
        p = self.edit('reorder_items', segment_id='s1', order=['c2', 'c1'])
        self.assertEqual(['c2', 'c1'], [i['id'] for i in scenario.get(p)['segments'][0]['items']])
        self.assertEqual([12, 48], [i['start_frame'] for i in scenario.get(p)['segments'][0]['items']])
        p = self.edit('reorder_segments', order=['s2', 's1'])
        self.assertEqual(['s2', 's1'], [s['id'] for s in p['timeline']['scenario']['segments']])
        self.assertEqual({'s1': 0, 's2': 120}, {s['id']: s['start_frame'] for s in p['timeline']['segments']})
        self.edit('remove_item', segment_id='s1', id='c2')
        self.assertEqual(12, next(c for c in self.current()['timeline']['clips'] if c['id'] == 'c1')['start_frame'])
        p = self.edit('remove_segment', id='s1')
        self.assertEqual(['c3'], [c['id'] for c in p['timeline']['clips']])
        for key in sources:
            self.assertEqual(sources[key], p[key])
        p = self.edit('remove_segment', id='s2')
        self.assertEqual([], p['timeline']['scenario']['segments'])
        self.assertEqual({'segments': [], 'clips': []}, self.projection(p))
        self.assertIs(scenario.get(p)['active'], True)
        self.assertEqual(sources['blocks'], p['blocks'])

    def test_blank_item_creates_draft_source_and_remove_and_undo_never_delete_it(self):
        self.activate()
        p = self.edit('add_item', segment_id='s1', title='Новая вставка', duration_frames=None)
        item = p['timeline']['scenario']['segments'][0]['items'][-1]
        draft = next(b for b in p['blocks'] if b['id'] == item['block_id'])
        group = next(g for g in p['segments'] if g['id'] == draft['segment_id'])
        self.assertEqual(('Сценарные заготовки', True), (group['name'], group['scenario_drafts']))
        self.assertIn(draft['id'], group['block_ids'])
        self.assertEqual(('Новая вставка', 'draft', [], [], []), tuple(draft[k] for k in ['title', 'status', 'takes', 'references', 'guides']))
        self.assertIsNone(draft['selected_take_id'])
        undone = self.store.timeline_operation(self.pid, p['revision'], 'undo', {})
        self.assertEqual(draft, self.store.find_block(undone, draft['id']))
        self.assertNotIn(item['id'], [i['id'] for s in undone['timeline']['scenario']['segments'] for i in s['items']])
        self.store.timeline_operation(self.pid, undone['revision'], 'redo', {})
        p = self.edit('remove_item', segment_id='s1', id=item['id'])
        self.assertEqual(draft, self.store.find_block(p, draft['id']))
        p = self.edit('add_item', segment_id='s1', title='Вторая заготовка', duration_frames=24)
        self.assertEqual(1, len([g for g in p['segments'] if g.get('scenario_drafts')]))
        self.assertEqual(draft, self.store.find_block(p, draft['id']))
        self.assert_no_generation()

    def test_library_add_is_new_usage_and_explicit_take_binding_preserves_source_selection(self):
        self.activate()
        sources = copy.deepcopy(self.current()['blocks'])
        p = self.edit('add_item', segment_id='s2', block_id=self.bid, title='Ещё одно использование', duration_frames=24, index=0)
        item = p['timeline']['scenario']['segments'][1]['items'][0]
        self.assertEqual((self.bid, None, 0), (item['block_id'], item['take_id'], item['source_in_frame']))
        self.assertNotIn(item['id'], ['c1', 'c2', 'c3'])
        self.assertEqual(sources, p['blocks'])
        p = self.edit('update_item', segment_id='s2', id=item['id'], take_id=self.take['id'], source_in_frame=72)
        placed = next(c for c in p['timeline']['clips'] if c['id'] == item['id'])
        self.assertEqual((120, 24, self.take['id'], 72), tuple(placed[k] for k in ['start_frame', 'duration_frames', 'take_id', 'source_in_frame']))
        self.assertEqual(sources, p['blocks'])
        self.assertEqual(self.take['id'], self.block(p)['selected_take_id'])

    def test_undo_redo_preserve_later_prompt_take_assets_and_clear_legacy_scenario(self):
        first = self.activate()
        undone = self.store.timeline_operation(self.pid, first['revision'], 'undo', {})
        self.assertIsNone(undone['timeline'].get('scenario'))
        self.assertIs(scenario.get(undone)['active'], False)
        self.assertEqual(self.projection(self.seed), self.projection(undone))
        self.store.timeline_operation(self.pid, undone['revision'], 'redo', {})
        self.edit('update_item', segment_id='s1', id='c1', duration_frames=36)
        self.save({'prompt': self.initial_prompt + '\nAuthor action', 'notes': 'Later notes'})
        p = self.current()
        self.block(p)['takes'].append({**self.take, 'id': 'take-later'})
        extra = self.book / 'extra.png'
        extra.write_bytes(b'fixture image')
        self.store.asset(p, extra)
        p = self.store.commit(p, p['revision'], 'fixture', 'Later take and asset')
        preserved = {k: copy.deepcopy(p[k]) for k in ['blocks', 'assets', 'segments']}
        u = self.store.timeline_operation(self.pid, p['revision'], 'undo', {})
        self.assertEqual(24, u['timeline']['scenario']['segments'][0]['items'][0]['duration_frames'])
        r = self.store.timeline_operation(self.pid, u['revision'], 'redo', {})
        self.assertEqual(36, r['timeline']['scenario']['segments'][0]['items'][0]['duration_frames'])
        for key, expected in preserved.items():
            self.assertEqual(expected, u[key]); self.assertEqual(expected, r[key])

    def test_invalid_schema_permutations_and_draft_creation_are_atomic(self):
        self.activate()
        p = self.edit('update_item', segment_id='s1', id='c1', title='x' * 160, description='Ж' * 12000)
        item = p['timeline']['scenario']['segments'][0]['items'][0]
        self.assertEqual(('x' * 160, 'Ж' * 12000), (item['title'], item['description']))
        self.assertEqual('Исходный блок', self.block(p)['title'])
        cases = [('update_segment', {'id': 's1', 'duration_frames': 0}),
                 ('update_segment', {'id': 's1', 'start_frame': True}),
                 ('update_item', {'segment_id': 's1', 'id': 'c1', 'duration_frames': 1.5}),
                 ('update_item', {'segment_id': 's1', 'id': 'c1', 'gap_before_frames': -1}),
                 ('update_item', {'segment_id': 's1', 'id': 'c1', 'source_in_frame': '0'}),
                 ('update_item', {'segment_id': 's1', 'id': 'c1', 'title': ''}),
                 ('update_item', {'segment_id': 's1', 'id': 'c1', 'title': 'x' * 161}),
                 ('update_item', {'segment_id': 's1', 'id': 'c1', 'description': 'x' * 12001}),
                 ('reorder_items', {'segment_id': 's1', 'order': ['c1', 'c1']}),
                 ('reorder_segments', {'order': ['s1']}),
                 ('add_item', {'segment_id': 's1', 'title': 'Discarded draft', 'duration_frames': 24, 'index': True}),
                 ('unknown', {}), ('add_segment', None)]
        for action, payload in cases:
            with self.subTest(action=action):
                self.rejected(action, payload)
        self.rejected('add_item', {'segment_id': 's1', 'block_id': 'missing'}, 404)
        self.rejected('update_item', {'segment_id': 's2', 'id': 'c3', 'take_id': self.take['id']}, 404)


class ScenarioSpatialTests(ScenarioFixture):
    def spatial(self, action, **payload):
        return self.store.timeline_operation(self.pid, self.current()['revision'], action, payload)

    def test_translate_only_applies_to_active_scenario_and_duration_edits_ripple(self):
        self.assertIsNone(scenario.translate(self.store, self.current(), 'update_clip', {'id': 'c1', 'duration_frames': 36}))
        self.activate()
        before = self.current()
        p = self.spatial('update_clip', id='c1', duration_frames=36)
        self.assertEqual([12, 60], [i['start_frame'] for i in scenario.get(p)['segments'][0]['items']])
        self.assertEqual(36, p['timeline']['scenario']['segments'][0]['items'][0]['duration_frames'])
        self.assertEqual(before['blocks'], p['blocks'])
        self.assertEqual(before['timeline']['music'], p['timeline']['music'])

    def test_spatial_move_reorders_once_and_left_trim_adjusts_gap_without_shifting_next_end(self):
        self.activate()
        before = copy.deepcopy(self.current()['timeline'])
        p = self.spatial('update_clip', id='c2', start_frame=0)
        self.assertEqual(['c2', 'c1'], [i['id'] for i in scenario.get(p)['segments'][0]['items']])
        self.assertEqual(len(before['undo']) + 1, len(p['timeline']['undo']))
        p = self.spatial('undo')
        self.assertEqual(before['scenario'], p['timeline']['scenario'])
        p = self.spatial('update_clip', id='c1', start_frame=16, source_in_frame=4, duration_frames=20)
        first, second = scenario.get(p)['segments'][0]['items']
        self.assertEqual((16, 36, 16, 4), tuple(first[k] for k in ['start_frame', 'end_frame', 'gap_before_frames', 'source_in_frame']))
        self.assertEqual(48, second['start_frame'])

    def test_spatial_add_clip_preserves_requested_start_and_existing_following_gaps(self):
        self.activate()
        p = self.spatial('add_clip', segment_id='s1', block_id=self.bid, take_id=self.take['id'], start_frame=40, duration_frames=8, source_in_frame=0)
        items = scenario.get(p)['segments'][0]['items']
        self.assertEqual([12, 40, 60], [i['start_frame'] for i in items])
        self.assertEqual([12, 4, 12], [i['gap_before_frames'] for i in items])
        p = self.edit('add_segment', name='Empty gap', start_frame=240, duration_frames=120)
        sid = p['timeline']['scenario']['segments'][-1]['id']
        p = self.spatial('add_clip', segment_id=sid, block_id=self.bid, take_id=self.take['id'], start_frame=288, duration_frames=24, source_in_frame=0)
        item = scenario.get(p)['segments'][-1]['items'][0]
        self.assertEqual((288, 48), (item['start_frame'], item['gap_before_frames']))

    def test_segment_move_remove_and_cross_segment_transfer_do_not_silently_drop_story(self):
        self.activate()
        p = self.spatial('move_segment', id='s1', start_frame=24)
        self.assertEqual([36, 72], [i['start_frame'] for i in scenario.get(p)['segments'][0]['items']])
        self.assertEqual(24, p['timeline']['scenario']['segments'][0]['start_frame'])
        before = self.project_state()
        with self.assertRaises(StudioError) as caught:
            self.spatial('update_clip', id='c1', segment_id='s2', start_frame=132)
        self.assertEqual(422, caught.exception.status)
        self.assertEqual(before, self.project_state())
        p = self.spatial('add_marker', name='New marker', frame=80)
        self.assertEqual(24, p['timeline']['scenario']['segments'][0]['start_frame'])
        p = self.spatial('remove_clip', id='c1')
        self.assertEqual(['c2'], [i['id'] for i in scenario.get(p)['segments'][0]['items']])
        p = self.spatial('remove_segment', id='s1')
        self.assertEqual(['s2'], [s['id'] for s in p['timeline']['scenario']['segments']])

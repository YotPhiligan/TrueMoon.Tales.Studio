import copy
import hashlib
import json
import threading
from concurrent.futures import ThreadPoolExecutor

from scenario_fixtures import ScenarioFixture
from studio import agent
from studio.model import StudioError


class ScenarioAgentTests(ScenarioFixture):
    def prepare_scope(self, **patch):
        return {'segment_id': 's1', 'intent': 'prepare_block', 'components': ['description', 'prompt'],
                'draft': {'title': 'Новая вставка', 'description': 'Снижение', 'duration_frames': 12, 'after_item_id': 'c1'}, **patch}

    def improve_scope(self, **patch):
        return {'segment_id': 's1', 'item_id': 'c1', 'intent': 'improve_block', 'components': ['prompt'], **patch}

    def create(self, scope=None, mode='apply', client_key=None, **patch):
        return agent.create_request(self.store, self.pid, self.current()['revision'], 'Подготовить движение', mode=mode,
                                    scope=self.prepare_scope() if scope is None else scope, client_key=client_key, **patch)

    def refused(self, callback, status=422):
        project, requests, sources = self.project_state(), self.request_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            callback()
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(project, self.project_state())
        self.assertEqual(requests, self.request_state())
        self.assertEqual(sources, self.source_state())

    def completed_breakdown(self, *rows):
        task = self.create({'segment_id': 's1', 'intent': agent.BREAKDOWN_INTENT}, mode='propose')
        running = agent.update_request(self.store, self.pid, task['id'], task['revision'], 'claim', 'agent:qa')
        self.refused(lambda: agent.update_request(self.store, self.pid, task['id'], running['revision'],
                                                 'finish', 'agent:qa', 'Пустой результат'))
        self.refused(lambda: agent.update_request(self.store, self.pid, task['id'], running['revision'],
                                                 'finish', 'agent:qa', 'Недопустимый результат',
                                                 proposal=[{'title': 'Блок', 'description': 'Действие', 'duration_frames': True}]))
        return agent.update_request(self.store, self.pid, task['id'], running['revision'],
                                    'finish', 'agent:qa', 'Предложение готово', proposal=list(rows))

    def test_breakdown_preview_is_read_only_and_apply_creates_one_undoable_change(self):
        original = self.activate()
        state = self.project_state()
        scope = {'segment_id': 's1', 'intent': agent.BREAKDOWN_INTENT}
        self.refused(lambda: self.create(scope, mode='apply'))
        task = self.completed_breakdown(
            {'title': 'Снижение', 'description': 'Клэр уходит за парапет.', 'duration_frames': 8,
             'sound_notes': 'Выстрел затихает'},
            {'title': 'Смещение', 'description': 'Клэр идёт вдоль парапета.', 'gap_before_frames': 4,
             'duration_frames': 12, 'continuity_notes': 'Карабин в руках'})
        rid = task['id']
        requests = self.request_state()
        plan = agent.preview_breakdown(self.store, self.pid, rid)
        self.assertEqual(state, self.project_state())
        self.assertEqual(requests, self.request_state())
        self.assertEqual('ready', plan['status'])
        self.assertEqual((96, 72, 24, 0), tuple(plan[k] for k in
                         ('window_frames', 'used_frames', 'proposed_frames', 'remaining_frames')))
        self.assertEqual([(72, 80), (84, 96)], [(r['start_frame'], r['end_frame']) for r in plan['items']])
        self.refused(lambda: agent.apply_breakdown(self.store, self.pid, rid, plan['project_revision'] - 1, 'author'), 409)
        updated = agent.apply_breakdown(self.store, self.pid, rid, plan['project_revision'], 'author')
        new_items = updated['timeline']['scenario']['segments'][0]['items'][2:]
        self.assertEqual(['Снижение', 'Смещение'], [i['title'] for i in new_items])
        self.assertEqual(('Выстрел затихает', 'Карабин в руках'),
                         (new_items[0]['sound_notes'], new_items[1]['continuity_notes']))
        self.assertTrue(all(i['take_id'] is None for i in new_items))
        self.assertEqual(original['blocks'], updated['blocks'][:len(original['blocks'])])
        self.assertEqual(original['timeline']['music'], updated['timeline']['music'])
        self.assertEqual(original['timeline']['markers'], updated['timeline']['markers'])
        self.assertEqual(len(original['timeline']['undo']) + 1, len(updated['timeline']['undo']))
        self.assertEqual('applied', agent.preview_breakdown(self.store, self.pid, rid)['status'])
        self.refused(lambda: agent.apply_breakdown(self.store, self.pid, rid, updated['revision'], 'author'), 409)
        undone = self.store.timeline_operation(self.pid, updated['revision'], 'undo', {})
        self.assertEqual(original['timeline']['scenario'], undone['timeline']['scenario'])
        redone = self.store.timeline_operation(self.pid, undone['revision'], 'redo', {})
        self.assertEqual(updated['timeline']['scenario'], redone['timeline']['scenario'])
        self.assertEqual(requests, self.request_state())
        self.assert_no_generation()

    def test_breakdown_rechecks_current_budget_and_protects_transitions(self):
        self.activate()
        task = self.completed_breakdown({'title': 'Заглушка', 'description': 'Перебежка.', 'duration_frames': 16})
        rid = task['id']
        before = agent.preview_breakdown(self.store, self.pid, rid)
        self.assertEqual(8, before['remaining_frames'])
        changed = self.edit('update_segment', id='s1', duration_frames=84)
        after = agent.preview_breakdown(self.store, self.pid, rid)
        self.assertEqual(('blocked', -4), (after['status'], after['remaining_frames']))
        self.refused(lambda: agent.apply_breakdown(self.store, self.pid, rid, before['project_revision'], 'author'), 409)
        self.refused(lambda: agent.apply_breakdown(self.store, self.pid, rid, changed['revision'], 'author'))
        restored = self.edit('update_segment', id='s1', duration_frames=96)
        transition = self.store.timeline_operation(self.pid, restored['revision'], 'set_transition', {
            'left_clip_id': 'c2', 'right_clip_id': 'c3', 'pause_frames': 60, 'fade_out_frames': 4})
        blocked = agent.preview_breakdown(self.store, self.pid, rid)
        self.assertEqual('blocked', blocked['status'])
        self.assertIn('разорвёт настроенный переход', blocked['issues'][0])
        self.refused(lambda: agent.apply_breakdown(self.store, self.pid, rid, transition['revision'], 'author'))

    def test_scope_and_target_use_stable_item_ids_and_component_subset_without_mutation(self):
        p = self.current()
        original = copy.deepcopy(p)
        segment, item = agent.scenario_target(p, 's1', 'c1')
        self.assertEqual(('s1', 'c1', self.bid, 12, 36), (segment['id'], item['id'], item['block_id'], item['start_frame'], item['end_frame']))
        scope = self.improve_scope(components=['settings', 'prompt'])
        normalized = agent.request_scope(p, scope)
        self.assertEqual({'segment_id': 's1', 'segment_name': 'Воспоминание', 'item_id': 'c1', 'intent': 'improve_block',
                          'components': ['settings', 'prompt'], 'block_id': self.bid}, normalized)
        normalized['components'].append('description')
        self.assertEqual(['settings', 'prompt'], scope['components'])
        self.assertEqual(original, p)

    def test_prepare_draft_limits_null_duration_and_default_components_are_exact(self):
        scope = self.prepare_scope(draft={'title': 'Ж' * 160, 'description': 'Р' * 12000, 'duration_frames': 518400, 'after_item_id': None})
        scope.pop('components')
        result = agent.request_scope(self.current(), scope)
        self.assertEqual(['description', 'references', 'guides', 'prompt', 'settings'], result['components'])
        self.assertEqual(scope['draft'], result['draft'])
        self.assertIsNone(result['item_id'])
        self.assertNotIn('block_id', result)
        result = agent.request_scope(self.current(), self.prepare_scope(draft={'title': 'Later'}))
        self.assertEqual({'title': 'Later', 'description': '', 'duration_frames': None, 'after_item_id': None}, result['draft'])

    def test_invalid_scope_shapes_components_ids_and_draft_boundaries_are_atomic(self):
        cases = [(None, 422), ([], 422), ({**self.prepare_scope(), 'extra': 1}, 422),
                 (self.prepare_scope(intent='generate'), 422), (self.prepare_scope(segment_id='missing'), 404),
                 (self.improve_scope(segment_id='s2'), 404), (self.improve_scope(item_id=None), 422),
                 (self.improve_scope(draft={}), 422), (self.prepare_scope(item_id='c1'), 422)]
        for components in [[], ['prompt', 'prompt'], ['unknown'], 'prompt', [{}]]:
            cases.append((self.prepare_scope(components=components), 422))
        for patch in [{'title': ''}, {'title': 'x' * 161}, {'description': None}, {'description': 'x' * 12001},
                      {'duration_frames': 0}, {'duration_frames': 1.0}, {'duration_frames': True}, {'duration_frames': 518401},
                      {'after_item_id': 'missing'}, {'after_item_id': 'c3'}, {'block_id': self.bid}]:
            cases.append((self.prepare_scope(draft={**self.prepare_scope()['draft'], **patch}),
                          409 if 'after_item_id' in patch else 422))
        for scope, status in cases:
            with self.subTest(scope=str(scope)[:90]):
                self.refused(lambda: agent.request_scope(self.current(), scope), status)

    def test_scoped_context_contains_current_window_music_neighbors_and_shared_source_count_read_only(self):
        before, sources = self.project_state(), self.source_state()
        context = agent.context(self.store, self.pid, sid='s1', iid='c1')
        self.assertEqual((self.bid, 'c1'), (context['block']['id'], context['scenario_item']['id']))
        self.assertEqual((0, 96, 24), tuple(context['scenario_segment'][k] for k in ['start_frame', 'duration_frames', 'remaining_frames']))
        self.assertEqual(['c1', 'c2'], [i['id'] for i in context['scenario_segment']['items']])
        self.assertEqual(2, context['source_usage_count'])
        self.assertEqual(self.seed['timeline']['music']['asset_id'], context['music_source']['id'])
        self.assertIn('Do not run GPU', context['contract']['scenario'])
        self.refused(lambda: agent.context(self.store, self.pid, bid=self.second_bid, sid='s1', iid='c1'))
        self.refused(lambda: agent.context(self.store, self.pid, iid='c1'))
        self.assertEqual(before, self.project_state()); self.assertEqual(sources, self.source_state()); self.assert_no_generation()

    def test_prepare_and_improve_intents_only_create_tasks_and_capture_real_neighbor_snapshot(self):
        before = self.project_state()
        prepared = self.create()
        self.assertEqual(('prepare_block', None, None), (prepared['scope']['intent'], prepared['scope']['item_id'], prepared['block_id']))
        self.assertEqual(['c1', 'c2'], [n['item']['id'] for n in prepared['context']['scenario_neighbors']])
        neighbor_assets = prepared['context']['scenario_neighbors'][0]['assets']
        self.assertEqual([1, 2, 3], [a['picture'] for a in neighbor_assets])
        self.assertEqual(['ref-1', 'ref-2', 'ref-3'], [a['reference_id'] for a in neighbor_assets])
        self.assertEqual([str(path.resolve()) for path in self.images], [a['absolute_path'] for a in neighbor_assets])
        self.assertEqual(self.prepare_scope()['draft'], prepared['scope']['draft'])
        improved = self.create(scope=self.improve_scope(), mode='propose')
        self.assertEqual((self.bid, 'c1', ['prompt']), (improved['block_id'], improved['scope']['item_id'], improved['scope']['components']))
        self.assertEqual(['c2'], [n['item']['id'] for n in improved['context']['scenario_neighbors']])
        snapshot = prepared['context']; unsigned = {k: v for k, v in snapshot.items() if k != 'snapshot_sha256'}
        self.assertEqual(hashlib.sha256(json.dumps(unsigned, ensure_ascii=False, sort_keys=True).encode()).hexdigest(), snapshot['snapshot_sha256'])
        self.assertEqual(before, self.project_state())
        self.refused(lambda: self.create(scope=self.improve_scope(), bid=self.second_bid))
        self.assert_no_generation()

    def test_client_key_retry_returns_same_current_task_after_project_change_and_changed_payload_conflicts(self):
        key = 'scenario_retry_001'
        original_revision = self.current()['revision']
        task = self.create(client_key=key)
        running = agent.update_request(self.store, self.pid, task['id'], 1, 'claim', 'agent:worker')
        self.save({'notes': 'New project revision'})
        before, requests = self.project_state(), self.request_state()
        retry = agent.create_request(self.store, self.pid, original_revision, 'Подготовить движение', mode='apply', scope=self.prepare_scope(), client_key=key)
        self.assertEqual(running, retry); self.assertEqual(1, len(requests))
        self.assertEqual(requests, self.request_state()); self.assertEqual(before, self.project_state())
        for patch in [{'goal': 'Changed goal'}, {'mode': 'propose'}, {'actor': 'other'},
                      {'scope': self.prepare_scope(components=['references'])}]:
            values = {'goal': 'Подготовить движение', 'mode': 'apply', 'scope': self.prepare_scope(), 'client_key': key, **patch}
            self.refused(lambda: agent.create_request(self.store, self.pid, self.current()['revision'], **values), 409)

    def test_client_key_boundaries_and_stale_new_submission_are_atomic(self):
        for key in ['a' * 15, 'a' * 81, 'bad/key_______________', True]:
            self.refused(lambda: self.create(client_key=key))
        first = self.create(client_key='a' * 16)
        second = self.create(client_key='b' * 80)
        self.assertNotEqual(first['id'], second['id'])
        self.refused(lambda: agent.create_request(self.store, self.pid, self.current()['revision'] - 1, 'Stale', scope=self.prepare_scope(), client_key='stale_submission_1'), 409)

    def test_concurrent_retry_key_creates_exactly_one_durable_intent(self):
        before = self.project_state(); barrier = threading.Barrier(2)
        def create(_):
            barrier.wait(timeout=5)
            return self.create(client_key='concurrent_submit_1')
        with ThreadPoolExecutor(max_workers=2) as pool:
            left, right = list(pool.map(create, range(2)))
        self.assertEqual(left, right); self.assertEqual(1, len(self.request_state()))
        self.assertEqual(before, self.project_state()); self.assert_no_generation()

    def test_applied_preparation_finishes_only_after_external_new_item_and_links_resolved_block(self):
        task = self.create()
        running = agent.update_request(self.store, self.pid, task['id'], 1, 'claim', 'agent:worker')
        self.refused(lambda: agent.update_request(self.store, self.pid, task['id'], 2, 'finish', 'agent:worker', 'Done'))
        self.refused(lambda: agent.update_request(self.store, self.pid, task['id'], 2, 'finish', 'agent:worker', 'Old item', {'segment_id': 's1', 'item_id': 'c1'}))
        p = self.edit('add_item', segment_id='s1', title='Новая вставка', description='Снижение', duration_frames=12, index=1)
        item = p['timeline']['scenario']['segments'][0]['items'][1]
        before = self.project_state()
        result = agent.update_request(self.store, self.pid, task['id'], running['revision'], 'finish', 'agent:worker', 'Материалы подготовлены',
                                      {'segment_id': 's1', 'item_id': item['id']})
        self.assertEqual({'segment_id': 's1', 'item_id': item['id'], 'block_id': item['block_id']}, result['result_target'])
        self.assertEqual(('completed', 3, p['revision']), (result['state'], result['revision'], result['completed_project_revision']))
        self.assertEqual(task['context'], result['context'])
        self.assertEqual(before, self.project_state()); self.assert_no_generation()

    def test_result_target_respects_selected_use_not_just_shared_block_and_modes(self):
        task = self.create(scope=self.improve_scope())
        running = agent.update_request(self.store, self.pid, task['id'], 1, 'claim', 'agent:worker')
        for target in [{'segment_id': 's1', 'item_id': 'c2'}, {'segment_id': 's2', 'item_id': 'c3'},
                       {'segment_id': 's1', 'item_id': 'c1', 'block_id': self.bid}, []]:
            self.refused(lambda: agent.update_request(self.store, self.pid, task['id'], 2, 'finish', 'agent:worker', 'Result', target))
        self.refused(lambda: agent.update_request(self.store, self.pid, task['id'], 2, 'finish', 'other', 'Result', {'segment_id': 's1', 'item_id': 'c1'}), 409)
        result = agent.update_request(self.store, self.pid, task['id'], running['revision'], 'finish', 'agent:worker', 'Prompt improved', {'segment_id': 's1', 'item_id': 'c1'})
        self.assertEqual(self.bid, result['result_target']['block_id'])
        proposed = self.create(mode='propose'); running = agent.update_request(self.store, self.pid, proposed['id'], 1, 'claim', 'agent:worker')
        self.refused(lambda: agent.update_request(self.store, self.pid, proposed['id'], 2, 'finish', 'agent:worker', 'Proposal', {'segment_id': 's1', 'item_id': 'c1'}))
        before = self.project_state(); result = agent.update_request(self.store, self.pid, proposed['id'], running['revision'], 'finish', 'agent:worker', 'Proposal only')
        self.assertEqual('completed', result['state']); self.assertNotIn('result_target', result); self.assertEqual(before, self.project_state())

    def test_old_unscoped_requests_and_context_remain_compatible(self):
        before = self.project_state()
        task = agent.create_request(self.store, self.pid, self.current()['revision'], 'Legacy request', bid='CL-T01')
        self.assertEqual(self.bid, task['block_id']); self.assertNotIn('scope', task)
        context = agent.context(self.store, self.pid, self.bid)
        self.assertEqual(self.bid, context['block']['id']); self.assertNotIn('scenario_segment', context)
        self.assertEqual(before, self.project_state()); self.assert_no_generation()

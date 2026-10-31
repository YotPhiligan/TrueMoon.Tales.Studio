import copy
import hashlib
import json
import re
import threading
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone

from agent_fixtures import AgentFixture
from studio import agent
from studio.model import StudioError, atomic, read


class AgentBriefTests(AgentFixture):
    def test_legacy_brief_read_returns_detached_defaults_without_source_or_project_writes(self):
        before, sources = self.project_state(), self.source_state()
        p = self.current()
        brief = agent.get_brief(p)
        self.assertEqual({'synopsis': '', 'visual_style': '', 'continuity_rules': '', 'agent_notes': ''}, brief)
        brief['synopsis'] = 'Local only'
        self.assertNotIn('brief', p)
        self.assertEqual('', agent.get_brief(self.current())['synopsis'])
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())

    def test_brief_patch_accepts_12000_chars_merges_fields_and_commits_once(self):
        p = self.current()
        before, sources = copy.deepcopy(p['blocks']), self.source_state()
        result = agent.save_brief(self.store, self.pid, {'synopsis': 'Ж' * 12000, 'visual_style': 'Anime'}, p['revision'], ' agent:writer ')
        self.assertEqual(p['revision'] + 1, result['revision'])
        self.assertEqual('agent:writer', result['activity'][-1]['actor'])
        self.assertEqual('Ж' * 12000, result['brief']['synopsis'])
        result = agent.save_brief(self.store, self.pid, {'agent_notes': 'Keep direction'}, result['revision'])
        self.assertEqual('Anime', result['brief']['visual_style'])
        self.assertEqual('Keep direction', result['brief']['agent_notes'])
        self.assertEqual(result['brief'], agent.get_brief(result))
        self.assertEqual(before, result['blocks'])
        self.assertEqual(sources, self.source_state())

    def test_brief_invalid_shapes_fields_types_actor_and_stale_revision_are_atomic(self):
        for patch in [{}, [], None, {'unknown': 'text'}, {'synopsis': 1}, {'synopsis': None}, {'synopsis': 'x' * 12001}]:
            with self.subTest(patch=type(patch).__name__):
                before = self.project_state()
                with self.assertRaises(StudioError) as caught:
                    agent.save_brief(self.store, self.pid, patch, self.current()['revision'])
                self.assertEqual(422, caught.exception.status)
                self.assertEqual(before, self.project_state())
        for actor in ['', ' ', None, True, 'x' * 81]:
            before = self.project_state()
            with self.subTest(actor=actor), self.assertRaises(StudioError) as caught:
                agent.save_brief(self.store, self.pid, {'synopsis': 'Denied'}, self.current()['revision'], actor)
            self.assertEqual(422, caught.exception.status)
            self.assertEqual(before, self.project_state())
        stale = self.current()['revision']
        agent.save_brief(self.store, self.pid, {'synopsis': 'Winner'}, stale)
        before = self.project_state()
        with self.assertRaises(StudioError) as caught:
            agent.save_brief(self.store, self.pid, {'synopsis': 'Lost'}, stale)
        self.assertEqual(409, caught.exception.status)
        self.assertEqual('Winner', self.current()['brief']['synopsis'])
        self.assertEqual(before, self.project_state())

    def test_malformed_stored_brief_is_reported_without_mutating_input(self):
        for value in [None, [], {'synopsis': 3}]:
            p = {**self.current(), 'brief': value}
            before = copy.deepcopy(p)
            with self.subTest(value=value), self.assertRaises(StudioError) as caught:
                agent.get_brief(p)
            self.assertEqual(422, caught.exception.status)
            self.assertEqual(before, p)


class AgentRequestTests(AgentFixture):
    def create(self, **changes):
        values = {'goal': 'Improve motion', 'mode': 'propose', 'bid': self.bid, 'actor': 'author', **changes}
        return agent.create_request(self.store, self.pid, self.current()['revision'], **values)

    def update(self, row, action, actor='worker', result=''):
        return agent.update_request(self.store, self.pid, row['id'], row['revision'], action, actor, result)

    def refused(self, call, status):
        project, requests = self.project_state(), self.request_state()
        with self.assertRaises(StudioError) as caught:
            call()
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(project, self.project_state())
        self.assertEqual(requests, self.request_state())

    def test_create_list_get_use_separate_files_and_frozen_context_without_project_changes(self):
        before, sources = self.project_state(), self.source_state()
        first = self.create(actor=' author ')
        second = self.create(mode='apply', bid=None)
        self.assertRegex(first['id'], r'^req-[0-9a-f]{16}$')
        self.assertNotEqual(first['id'], second['id'])
        self.assertEqual(('pending', 1, None, 'author'), tuple(first[k] for k in ['state', 'revision', 'owner', 'actor']))
        path = self.store.location(self.pid).parent / 'AgentRequests' / (first['id'] + '.json')
        self.assertEqual(first, read(path))
        self.assertEqual(first, agent.get_request(self.store, self.pid, first['id']))
        summaries = agent.list_requests(self.store, self.pid)
        self.assertCountEqual([first['id'], second['id']], [r['id'] for r in summaries])
        self.assertTrue(all('context' not in row for row in summaries))
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.save({'notes': 'Later notes'})
        frozen = agent.get_request(self.store, self.pid, first['id'])
        self.assertEqual(first['context'], frozen['context'])
        self.assertEqual('', frozen['context']['block']['notes'])
        self.assert_no_generation()

    def test_owner_claim_finish_records_latest_project_revision_and_is_terminal(self):
        row = self.create()
        running = self.update(row, 'claim', actor=' worker ')
        self.assertEqual(('running', 2, 'worker'), tuple(running[k] for k in ['state', 'revision', 'owner']))
        for action in ['finish', 'fail']:
            self.refused(lambda: self.update(running, action, 'another', 'Wrong owner'), 409)
        self.save({'notes': 'Independent author edit'})
        before = self.project_state()
        completed = self.update(running, 'finish', 'worker', 'Finished exactly')
        self.assertEqual(('completed', 3, 'Finished exactly', self.current()['revision']), tuple(completed[k] for k in ['state', 'revision', 'result', 'completed_project_revision']))
        self.assertEqual(before, self.project_state())
        self.assertEqual(row['context'], completed['context'])
        for action in ['claim', 'cancel', 'finish', 'fail']:
            with self.subTest(action=action):
                self.refused(lambda: self.update(completed, action, 'worker', 'No replacement'), 409)

    def test_owner_can_fail_and_pending_or_running_can_be_cancelled_by_other_actor(self):
        pending = self.create()
        cancelled = self.update(pending, 'cancel', 'reviewer', 'Withdrawn')
        self.assertEqual(('cancelled', 2, 'Withdrawn'), tuple(cancelled[k] for k in ['state', 'revision', 'result']))
        running = self.update(self.create(), 'claim')
        cancelled_running = self.update(running, 'cancel', 'author')
        self.assertEqual('cancelled', cancelled_running['state'])
        running = self.update(self.create(), 'claim')
        failed = self.update(running, 'fail', result='Cannot inspect source')
        self.assertEqual(('failed', 3, 'Cannot inspect source'), tuple(failed[k] for k in ['state', 'revision', 'result']))
        for row in [cancelled, cancelled_running, failed]:
            self.refused(lambda: self.update(row, 'cancel', 'author'), 409)

    def test_stale_request_and_project_revisions_reject_without_lost_writes(self):
        row = self.create()
        running = self.update(row, 'claim')
        self.refused(lambda: self.update(row, 'cancel', 'author'), 409)
        self.refused(lambda: agent.update_request(self.store, self.pid, row['id'], True, 'cancel', 'author'), 409)
        revision = self.current()['revision']
        self.save({'notes': 'New project revision'})
        self.refused(lambda: agent.create_request(self.store, self.pid, revision, 'Stale'), 409)
        self.refused(lambda: agent.create_request(self.store, self.pid, float(self.current()['revision']), 'Noninteger revision'), 409)
        self.assertEqual(running, agent.get_request(self.store, self.pid, row['id']))

    def test_invalid_transitions_and_shapes_do_not_create_or_update_requests(self):
        row = self.create()
        for action in ['finish', 'fail', 'unknown', None, []]:
            with self.subTest(action=action):
                self.refused(lambda: self.update(row, action, result='Result'), 409)
        running = self.update(row, 'claim')
        self.refused(lambda: self.update(running, 'claim'), 409)
        for result in ['', ' ', None, {}, 'x' * 100001]:
            with self.subTest(result=type(result).__name__):
                self.refused(lambda: self.update(running, 'finish', result=result), 422)
        for actor in ['', ' ', None, True, 'a' * 81]:
            with self.subTest(actor=actor):
                self.refused(lambda: self.update(running, 'cancel', actor), 422)

    def test_goal_actor_and_result_exact_limits_and_invalid_modes_are_enforced(self):
        maximum = self.create(goal='Ж' * 12000, actor='a' * 80)
        self.assertEqual(('Ж' * 12000, 'a' * 80), (maximum['goal'], maximum['actor']))
        running = self.update(maximum, 'claim')
        completed = self.update(running, 'finish', result='Р' * 100000)
        self.assertEqual('Р' * 100000, completed['result'])
        for goal in ['', ' ', None, False, {}, 'x' * 12001]:
            with self.subTest(goal=type(goal).__name__):
                self.refused(lambda: self.create(goal=goal), 422)
        for mode in ['execute', '', None, ['propose']]:
            with self.subTest(mode=mode):
                self.refused(lambda: self.create(mode=mode), 422)
        for actor in ['', None, 1, 'x' * 81]:
            self.refused(lambda: self.create(actor=actor), 422)

    def test_concurrent_claim_at_same_revision_has_exactly_one_owner(self):
        row = self.create()
        barrier = threading.Barrier(2)
        def claim(actor):
            barrier.wait(timeout=5)
            try:
                return self.update(row, 'claim', actor)['owner']
            except StudioError as error:
                return error.status
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(claim, ['worker-a', 'worker-b']))
        self.assertEqual(1, results.count(409))
        current = agent.get_request(self.store, self.pid, row['id'])
        self.assertEqual(2, current['revision'])
        self.assertIn(current['owner'], ['worker-a', 'worker-b'])
        self.assertIn(current['owner'], results)

    def test_request_ids_and_embedded_project_scope_are_checked(self):
        row = self.create()
        for rid in ['../Studio.project', 'req-' + 'g' * 16, None]:
            self.refused(lambda: agent.get_request(self.store, self.pid, rid), 422)
        self.refused(lambda: agent.get_request(self.store, self.pid, 'req-' + '0' * 16), 404)
        path = agent.request_path(self.store, self.pid, row['id'])
        corrupted = {**row, 'project_id': 'foreign'}
        atomic(path, corrupted)
        self.refused(lambda: agent.get_request(self.store, self.pid, row['id']), 422)

    def test_list_is_bounded_to_200_newest_summaries_without_context(self):
        row = self.create()
        original_path = agent.request_path(self.store, self.pid, row['id'])
        original_path.unlink()
        base = datetime(2026, 10, 31, tzinfo=timezone.utc)
        for index in range(201):
            rid = f'req-{index:016x}'
            value = {**row, 'id': rid, 'updated_at': (base + timedelta(seconds=index)).isoformat(), 'context': {'private_snapshot': index}}
            atomic(agent.request_path(self.store, self.pid, rid), value)
        summaries = agent.list_requests(self.store, self.pid)
        self.assertEqual(200, len(summaries))
        self.assertEqual('req-00000000000000c8', summaries[0]['id'])
        self.assertEqual('req-0000000000000001', summaries[-1]['id'])
        self.assertTrue(all('context' not in item for item in summaries))


class AgentContextTests(AgentFixture):
    def test_context_preserves_legacy_block_payload_and_project_overview_without_writes(self):
        agent.save_brief(self.store, self.pid, {'synopsis': 'Тизер Клэр', 'visual_style': 'Anime'}, self.current()['revision'])
        before, sources = self.project_state(), self.source_state()
        legacy = self.store.context(self.pid, self.bid)
        actual = agent.context(self.store, self.pid, 'CL-T01')
        for key in ['block', 'segment', 'validation', 'neighbor_blocks']:
            self.assertEqual(legacy[key], actual[key])
        self.assertEqual(len(legacy['assets']), len(actual['assets']))
        for old, new in zip(legacy['assets'], actual['assets']):
            for key in old:
                self.assertEqual(old[key], new[key])
        overview = agent.context(self.store, self.pid)
        self.assertIsNone(overview['block'])
        self.assertIsNone(overview['validation'])
        self.assertIsNone(overview['music_source'])
        self.assertEqual([], overview['timeline_media'])
        self.assertEqual([self.bid], [b['id'] for b in overview['project_blocks']])
        self.assertEqual(('Тизер Клэр', 'Anime'), (overview['brief']['synopsis'], overview['brief']['visual_style']))
        self.assertEqual({'id': self.bid, 'label': 'CL-T01', 'title': 'Исходный блок', 'segment_id': self.block()['segment_id'],
                          'selected_take_id': None, 'takes': 0}, overview['project_blocks'][0])
        self.assertIs(overview['integration']['automatic_dispatch'], False)
        self.assertEqual(str(self.amv / 'UI/studio_agent.py'), overview['integration']['cli_path'])
        encoded = {k: v for k, v in actual.items() if k != 'snapshot_sha256'}
        expected = hashlib.sha256(json.dumps(encoded, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
        self.assertEqual(expected, actual['snapshot_sha256'])
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()

    def test_context_orders_repeated_placements_and_actual_neighbors_not_block_array(self):
        p = self.current()
        segment = p['segments'][0]['id']
        for label in ['CL-EARLY', 'CL-LATE']:
            p = self.store.operation(self.pid, p['revision'], 'add_block', {'segment_id': segment, 'label': label})
        early, late = p['blocks'][1]['id'], p['blocks'][2]['id']
        def clip(ident, bid, start):
            return {'id': ident, 'segment_id': 'montage', 'block_id': bid, 'take_id': None, 'start_frame': start, 'duration_frames': 24, 'source_in_frame': 0}
        p['timeline'] = {'version': 1, 'fps': 24, 'segments': [{'id': 'montage', 'name': 'Montage', 'color': 'mint', 'start_frame': 0, 'duration_frames': 240}],
                         'clips': [clip('late', late, 120), clip('repeat', self.bid, 200), clip('early', early, 0), clip('selected', self.bid, 72)],
                         'markers': [], 'music': None, 'clip_audio': False, 'undo': [], 'redo': []}
        self.store.commit(p, p['revision'], 'fixture', 'Shuffled placements')
        actual = agent.context(self.store, self.pid, self.bid)['placements']
        self.assertEqual(['selected', 'repeat'], [item['clip']['id'] for item in actual])
        self.assertEqual(('early', 'late'), (actual[0]['previous']['id'], actual[0]['next']['id']))
        self.assertEqual(3, actual[0]['clip']['start_seconds'])
        self.assertEqual(4, actual[0]['clip']['end_seconds'])
        self.assertEqual('late', actual[1]['previous']['id'])
        self.assertIsNone(actual[1]['next'])

    def test_context_media_includes_take_and_motion_source_paths_with_no_latent_url(self):
        take, _ = self.add_take()
        p = self.current()
        p = self.store.operation(self.pid, p['revision'], 'add_block', {'segment_id': p['segments'][0]['id'], 'label': 'CL-SOURCE'})
        source = p['blocks'][1]
        metadata = {'seconds': 2.0, 'fps': 24, 'frames': 48}
        self.block(p)['takes'][0]['video_info'] = metadata
        source['takes'] = [{**take, 'id': 'take-source', 'video_info': metadata}]
        self.block(p)['context_take_id'] = 'take-source'
        music = self.book / 'score.wav'
        music.write_bytes(b'fixture audio metadata only')
        aid = self.store.asset(p, music)
        p['assets'][aid]['audio_info'] = {'seconds': 2.0}
        p['timeline'] = {'version': 1, 'fps': 24,
            'segments': [{'id': 'montage', 'name': 'Montage', 'color': 'mint', 'start_frame': 0, 'duration_frames': 96}], 'markers': [], 'clips': [
            {'id': 'video-placement', 'segment_id': 'montage', 'block_id': self.bid, 'take_id': take['id'], 'start_frame': 0, 'duration_frames': 24, 'source_in_frame': 0},
            {'id': 'placeholder', 'segment_id': 'montage', 'block_id': source['id'], 'take_id': None, 'start_frame': 48, 'duration_frames': 24, 'source_in_frame': 0}],
            'music': {'id': 'music-fixture', 'asset_id': aid, 'start_frame': 24, 'duration_frames': 24, 'source_in_frame': 0, 'gain': .8, 'muted': False},
            'clip_audio': False, 'undo': [], 'redo': []}
        self.store.commit(p, p['revision'], 'fixture', 'Motion source')
        actual = agent.context(self.store, self.pid, self.bid)
        self.assertEqual([1, 2, 3], [r['picture'] for r in actual['assets']])
        self.assertEqual([str(path.resolve()) for path in self.images], [r['absolute_path'] for r in actual['assets']])
        for key in ['video_asset_id', 'last_frame_asset_id', 'native_av_asset_id']:
            media = actual['takes'][0]['media'][key]
            self.assertEqual(str(self.store.resolve_asset(self.current(), take[key])), media['absolute_path'])
            self.assertIs(media['available'], True)
            self.assertEqual(media, actual['motion_context_source']['media'][key])
            if key == 'native_av_asset_id':
                self.assertIsNone(media['media_url'])
            else:
                self.assertEqual(f'/api/media?project={self.pid}&asset={take[key]}', media['media_url'])
        self.assertEqual(source['id'], actual['motion_context_source']['block_id'])
        self.assertEqual([{'clip_id': 'video-placement', 'take_id': take['id'], 'video': actual['takes'][0]['media']['video_asset_id']}], actual['timeline_media'])
        self.assertEqual(str(music), actual['music_source']['absolute_path'])
        self.assertIs(actual['music_source']['available'], True)
        self.assertEqual(f'/api/media?project={self.pid}&asset={aid}', actual['music_source']['media_url'])

    def test_source_docs_are_allowlisted_hashed_truncated_and_exclude_secrets_and_unlisted_files(self):
        long_doc = self.book / 'AGENTS.md'
        long_doc.write_text('Ж' * 40000, encoding='utf-8')
        card = self.root / 'Shots/CL-T01.md'
        card.parent.mkdir()
        card.write_text('Approved source card', encoding='utf-8')
        secret = self.root / 'Handbook.Secrets.md'
        secret.write_text('FAKE_SECRET_SENTINEL', encoding='utf-8')
        (self.root / 'Private.md').write_text('UNLISTED_SENTINEL', encoding='utf-8')
        p = self.current()
        self.block(p)['card_path'] = card.relative_to(self.book).as_posix()
        self.store.commit(p, p['revision'], 'fixture', 'Source card')
        before = self.project_state()
        source_hash = hashlib.sha256(long_doc.read_bytes()).hexdigest()
        source_state = self.source_state()
        docs = agent.context(self.store, self.pid, self.bid)['documents']
        self.assertEqual({str(path) for path in self.documents + [card]}, {row['absolute_path'] for row in docs})
        roles = dict(zip([str(path) for path in self.documents + [card]],
                         ['instructions', 'instructions', 'instructions', 'pipeline_plan', 'project_plan', 'source_card']))
        self.assertEqual(roles, {row['absolute_path']: row['role'] for row in docs})
        for doc in docs:
            self.assertEqual('repository_instruction' if roles[doc['absolute_path']] == 'instructions' else 'source_material', doc['authority'])
        row = next(row for row in docs if row['absolute_path'] == str(long_doc))
        self.assertEqual(('instructions', 'repository_instruction', True, 'Ж' * 12000), tuple(row[k] for k in ['role', 'authority', 'truncated', 'text']))
        self.assertEqual(source_hash, row['sha256'])
        self.assertEqual('source_card', docs[-1]['role'])
        self.assertEqual('source_material', docs[-1]['authority'])
        self.assertNotIn('FAKE_SECRET_SENTINEL', json.dumps(docs))
        self.assertNotIn('UNLISTED_SENTINEL', json.dumps(docs))
        self.assertEqual(before, self.project_state())
        self.assertEqual(source_state, self.source_state())
        outside = self.book / 'Outside.md'
        outside.write_text('Outside project root', encoding='utf-8')
        wrong_type = self.root / 'Shots/wrong.txt'
        wrong_type.write_text('Wrong extension', encoding='utf-8')
        for forbidden in [secret, outside, wrong_type]:
            p = self.current()
            self.block(p)['card_path'] = forbidden.relative_to(self.book).as_posix()
            self.store.commit(p, p['revision'], 'fixture', 'Forbidden card')
            self.assertNotIn(str(forbidden), [row['absolute_path'] for row in agent.context(self.store, self.pid, self.bid)['documents']])

    def test_block_resolution_prefers_id_rejects_ambiguous_label_and_unknown_block(self):
        p = self.current()
        p = self.store.operation(self.pid, p['revision'], 'add_block', {'segment_id': p['segments'][0]['id'], 'label': self.bid})
        self.assertEqual(self.bid, agent.context(self.store, self.pid, self.bid)['block']['id'])
        p['blocks'][1]['label'] = 'CL-T01'
        self.store.commit(p, p['revision'], 'fixture', 'Duplicate label')
        before = self.project_state()
        for label, status in [('CL-T01', 409), ('missing', 404), ('', 422), ([], 422), (True, 422)]:
            with self.subTest(label=label), self.assertRaises(StudioError) as caught:
                agent.context(self.store, self.pid, label)
            self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.project_state())

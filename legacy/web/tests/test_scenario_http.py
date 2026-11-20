import copy
from scenario_fixtures import ScenarioFixture
from agent_fixtures import AgentHttpFixture


class ScenarioHttpTests(ScenarioFixture, AgentHttpFixture):
    def test_get_scenario_adopts_legacy_read_only_without_library_injection_or_gpu(self):
        before, sources = self.project_state(), self.source_state()
        status, view = self.http('GET', f'/api/projects/{self.pid}/scenario')
        self.assertEqual(200, status)
        self.assertIs(view['active'], False)
        self.assertEqual(['c1', 'c2'], [i['id'] for i in view['segments'][0]['items']])
        self.assertNotIn(self.unplaced_bid, [i['block_id'] for s in view['segments'] for i in s['items']])
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()

    def test_post_returns_full_project_actor_revision_and_issue_persists_last_valid_projection(self):
        path = f'/api/projects/{self.pid}/scenario'
        original = self.current()
        status, updated = self.http('POST', path, {'expected_revision': original['revision'], 'actor': 'agent:scenario',
            'action': 'update_item', 'payload': {'segment_id': 's1', 'id': 'c1', 'duration_frames': 36}})
        self.assertEqual(200, status)
        self.assertEqual((self.pid, original['revision'] + 1, 'agent:scenario'), (updated['id'], updated['revision'], updated['activity'][-1]['actor']))
        self.assertEqual(original['blocks'], updated['blocks'])
        self.assertEqual(original['assets'], updated['assets'])
        self.assertEqual(60, next(c for c in updated['timeline']['clips'] if c['id'] == 'c2')['start_frame'])
        valid_projection = self.projection(updated)
        status, issue_project = self.http('POST', path, {'expected_revision': updated['revision'], 'action': 'update_item',
            'payload': {'segment_id': 's1', 'id': 'c2', 'duration_frames': 49}})
        self.assertEqual(200, status)
        self.assertEqual(updated['revision'] + 1, issue_project['revision'])
        self.assertEqual(valid_projection, self.projection(issue_project))
        self.assertEqual(49, issue_project['timeline']['scenario']['segments'][0]['items'][1]['duration_frames'])
        status, view = self.http('GET', path)
        self.assertEqual(200, status)
        self.assertEqual(['overflow'], [i['code'] for i in view['issues']])
        self.assert_no_generation()

    def test_post_without_token_stale_revision_or_bad_schema_is_atomic(self):
        path = f'/api/projects/{self.pid}/scenario'
        revision = self.current()['revision']
        payload = {'expected_revision': revision, 'action': 'update_segment', 'payload': {'id': 's1', 'name': 'Denied'}}
        before, sources = self.project_state(), self.source_state()
        denied, _ = self.http('POST', path, payload, authorized=False)
        stale, _ = self.http('POST', path, {**payload, 'expected_revision': revision - 1})
        invalid, _ = self.http('POST', path, {**payload, 'payload': []})
        invalid_number, _ = self.http('POST', path, {**payload, 'payload': {'id': 's1', 'duration_frames': 0}})
        self.assertEqual((403, 409, 422, 422), (denied, stale, invalid, invalid_number))
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())

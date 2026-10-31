from agent_fixtures import AgentHttpFixture


class AgentHttpTests(AgentHttpFixture):
    def test_context_by_id_label_and_overview_preserve_legacy_route_and_never_start_jobs(self):
        before, sources = self.project_state(), self.source_state()
        base = f'/api/projects/{self.pid}'
        old_status, old = self.http('GET', base + '/context?block=' + self.bid)
        id_status, by_id = self.http('GET', base + '/agent/context?block=' + self.bid)
        label_status, by_label = self.http('GET', base + '/agent/context?block=CL-T01')
        overview_status, overview = self.http('GET', base + '/agent/context')
        self.assertEqual((200, 200, 200, 200), (old_status, id_status, label_status, overview_status))
        self.assertEqual(old['block'], by_id['block'])
        self.assertEqual(by_id, by_label)
        self.assertIsNone(overview['block'])
        self.assertEqual(2, overview['context_version'])
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())
        self.assert_no_generation()

    def test_brief_get_patch_revision_actor_and_unknown_patch_are_atomic(self):
        path = f'/api/projects/{self.pid}/brief'
        initial = self.current()['revision']
        status, brief = self.http('GET', path)
        self.assertEqual(200, status)
        self.assertEqual({'synopsis': '', 'visual_style': '', 'continuity_rules': '', 'agent_notes': ''}, brief)
        payload = {'expected_revision': initial, 'brief': {'synopsis': 'Короткий тизер'}, 'actor': ' agent:writer '}
        denied, _ = self.http('PATCH', path, payload, authorized=False)
        self.assertEqual(403, denied)
        self.assertEqual(initial, self.current()['revision'])
        status, saved = self.http('PATCH', path, payload)
        self.assertEqual(200, status)
        self.assertEqual(initial + 1, saved['revision'])
        self.assertEqual('agent:writer', saved['activity'][-1]['actor'])
        self.assertEqual('Короткий тизер', saved['brief']['synopsis'])
        before = self.project_state()
        stale, _ = self.http('PATCH', path, payload)
        invalid, _ = self.http('PATCH', path, {'expected_revision': saved['revision'], 'brief': {'unknown': 'x'}})
        self.assertEqual((409, 422), (stale, invalid))
        self.assertEqual(before, self.project_state())

    def test_request_http_full_snapshot_claim_finish_list_and_no_project_or_gpu_changes(self):
        base = f'/api/projects/{self.pid}/agent/requests'
        before = self.project_state()
        payload = {'expected_revision': self.current()['revision'], 'goal': 'Ускорить снижение', 'block_id': 'CL-T01', 'mode': 'apply', 'actor': 'author'}
        status, created = self.http('POST', base, payload)
        self.assertEqual(201, status)
        self.assertEqual((self.bid, 'pending', 1), (created['block_id'], created['state'], created['revision']))
        self.assertEqual(self.bid, created['context']['block']['id'])
        path = base + '/' + created['id']
        status, loaded = self.http('GET', path)
        self.assertEqual(200, status)
        self.assertEqual(created, loaded)
        status, running = self.http('POST', path, {'expected_revision': 1, 'action': 'claim', 'actor': 'agent:worker'})
        self.assertEqual(200, status)
        self.assertEqual(('running', 2, 'agent:worker'), (running['state'], running['revision'], running['owner']))
        stale, _ = self.http('POST', path, {'expected_revision': 1, 'action': 'cancel'})
        foreign, _ = self.http('POST', path, {'expected_revision': 2, 'action': 'finish', 'actor': 'other', 'result': 'Done'})
        self.assertEqual((409, 409), (stale, foreign))
        status, completed = self.http('POST', path, {'expected_revision': 2, 'action': 'finish', 'actor': 'agent:worker', 'result': 'Правка проверена'})
        self.assertEqual(200, status)
        self.assertEqual(('completed', 3, 'Правка проверена'), (completed['state'], completed['revision'], completed['result']))
        list_status, summaries = self.http('GET', base)
        self.assertEqual(200, list_status)
        self.assertEqual([created['id']], [row['id'] for row in summaries])
        self.assertNotIn('context', summaries[0])
        self.assertEqual(before, self.project_state())
        self.assert_no_generation()

    def test_request_write_token_shape_revision_mode_and_result_errors_do_not_write(self):
        base = f'/api/projects/{self.pid}/agent/requests'
        revision = self.current()['revision']
        before, requests = self.project_state(), self.request_state()
        payload = {'expected_revision': revision, 'goal': 'Task'}
        denied, _ = self.http('POST', base, payload, authorized=False)
        invalid_mode, _ = self.http('POST', base, {**payload, 'mode': 'execute'})
        invalid_goal, _ = self.http('POST', base, {**payload, 'goal': []})
        stale, _ = self.http('POST', base, {**payload, 'expected_revision': revision - 1})
        invalid_shape, _ = self.http('POST', base, [])
        self.assertEqual((403, 422, 422, 409, 400), (denied, invalid_mode, invalid_goal, stale, invalid_shape))
        self.assertEqual(before, self.project_state())
        self.assertEqual(requests, self.request_state())

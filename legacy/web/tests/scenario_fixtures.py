import copy
from agent_fixtures import AgentFixture
from studio import scenario
from studio.model import StudioError, digest


class ScenarioFixture(AgentFixture):
    def setUp(self):
        super().setUp()
        self.take, _ = self.add_take()
        p = self.current()
        group = p['segments'][0]['id']
        for label in ['CL-SECOND', 'CL-UNPLACED']:
            p = self.store.operation(self.pid, p['revision'], 'add_block', {'segment_id': group, 'label': label})
        self.second_bid, self.unplaced_bid = [b['id'] for b in p['blocks'][1:]]
        p['blocks'][1]['title'] = 'Второй блок'
        self.block(p)['takes'][0]['video_info'] = {'seconds': 4.0, 'frames': 96, 'fps': 24}
        self.block(p)['takes'][0]['review_state'] = 'accepted'
        self.block(p)['selected_take_id'] = self.take['id']
        audio = self.book / 'scenario-score.wav'
        audio.write_bytes(b'fixture metadata only')
        aid = self.store.asset(p, audio)
        p['assets'][aid]['audio_info'] = {'seconds': 4.0}
        def clip(ident, sid, bid, start, take=None, source=0):
            return {'id': ident, 'segment_id': sid, 'block_id': bid, 'take_id': take,
                    'start_frame': start, 'duration_frames': 24, 'source_in_frame': source}
        p['timeline'] = {'version': 1, 'fps': 24, 'segments': [
            {'id': 's1', 'name': 'Воспоминание', 'color': 'mint', 'start_frame': 0, 'duration_frames': 96},
            {'id': 's2', 'name': 'Операция', 'color': 'blue', 'start_frame': 120, 'duration_frames': 72}],
            'clips': [clip('c2', 's1', self.bid, 48, self.take['id'], 12),
                      clip('c3', 's2', self.second_bid, 132), clip('c1', 's1', self.bid, 12, self.take['id'])],
            'markers': [{'id': 'beat', 'name': 'Музыкальная опора', 'frame': 24}],
            'music': {'id': 'music', 'asset_id': aid, 'start_frame': 0, 'duration_frames': 48, 'source_in_frame': 0, 'gain': .8, 'muted': False},
            'clip_audio': False, 'undo': [], 'redo': []}
        self.store.commit(p, p['revision'], 'fixture', 'Legacy montage')
        self.seed = self.current()

    def edit(self, action, **payload):
        p = self.current()
        updated = scenario.apply(self.store, p, action, payload)
        return self.store.commit(updated, p['revision'], 'author', 'Scenario ' + action)

    def source_state(self):
        state = super().source_state()
        p = self.current()
        for aid in p['assets']:
            path = self.store.resolve_asset(p, aid)
            state[str(path)] = digest(path)
        return state

    def activate(self):
        return self.edit('update_segment', id='s1', description='Scenario intent')

    def view(self):
        return scenario.get(self.current())

    def projection(self, p=None):
        return {k: copy.deepcopy((p or self.current())['timeline'][k]) for k in ['segments', 'clips']}

    def rejected(self, action, payload, status=422):
        before, sources = self.project_state(), self.source_state()
        with self.assertRaises(StudioError) as caught:
            scenario.apply(self.store, self.current(), action, payload)
        self.assertEqual(status, caught.exception.status)
        self.assertEqual(before, self.project_state())
        self.assertEqual(sources, self.source_state())

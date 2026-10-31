import http.client
from unittest.mock import patch

from agent_fixtures import AgentHttpFixture
from scenario_fixtures import ScenarioFixture
from test_titles import title


class TitleHttpTests(ScenarioFixture, AgentHttpFixture):
    def setUp(self):
        super().setUp();p = self.current();p['output_size'] = [320, 180]
        self.store.commit(p, p['revision'], 'fixture', 'HTTP title canvas')

    def test_font_catalog_layers_and_preview_png_are_public_read_only_derived_resources(self):
        base = f'/api/projects/{self.pid}';before, sources = self.project_state(), self.source_state()
        status, fonts = self.http('GET', '/api/title-fonts')
        self.assertEqual(200, status);self.assertGreater(len(fonts), 0)
        for font in fonts:self.assertEqual({'id', 'label'}, set(font))
        status, layers = self.http('GET', base + '/title-layers')
        self.assertEqual((200, [], [320, 180]), (status, layers['layers'], layers['canvas_size']))
        payload = {'expected_revision': self.current()['revision'], 'title': title()}
        status, preview = self.http('POST', base + '/title-preview', payload)
        self.assertEqual(200, status);self.assertNotIn('path', preview['layer']);self.assertEqual('title-preview', preview['layer']['id'])
        key = preview['layer']['key'];url = preview['layer']['url']
        self.assertEqual(base + '/title-images/' + key, url)
        conn = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        try:
            conn.request('GET', url);response = conn.getresponse();body = response.read()
            self.assertEqual(200, response.status);self.assertEqual('image/png', response.getheader('Content-Type'))
            self.assertTrue(body.startswith(b'\x89PNG\r\n\x1a\n'))
        finally:conn.close()
        self.assertEqual(422, self.http('GET', base + '/title-images/not-a-key')[0])
        self.assertEqual(404, self.http('GET', base + '/title-images/' + 'f' * 64)[0])
        self.assertEqual(before, self.project_state());self.assertEqual(sources, self.source_state())
        self.assertNotIn('titles', self.current()['timeline']);self.assert_no_generation()

    def test_preview_token_revision_and_layout_errors_do_not_write_or_render_stale_draft(self):
        base = f'/api/projects/{self.pid}';before = self.project_state()
        body = {'expected_revision': self.current()['revision'], 'title': title()}
        with patch.object(self.store, 'render_title') as render:
            self.assertEqual(403, self.http('POST', base + '/title-preview', body, authorized=False)[0])
            self.assertEqual(409, self.http('POST', base + '/title-preview', {**body, 'expected_revision': body['expected_revision'] - 1})[0])
            render.assert_not_called()
        self.assertEqual(422, self.http('POST', base + '/title-preview', {**body, 'title': []})[0])
        self.assertEqual(422, self.http('POST', base + '/title-preview', {**body, 'title': title(text='Ш', font_size=256)})[0])
        self.assertEqual(before, self.project_state());self.assert_no_generation()

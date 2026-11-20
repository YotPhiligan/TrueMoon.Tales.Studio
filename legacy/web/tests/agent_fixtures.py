"""Agent-only isolated projects, source documents and guarded HTTP service."""
import http.client
import json
import threading
from unittest.mock import patch

from fixtures import Fixture
from studio.model import digest
from studio.server import LocalServer, handler_for


class AgentFixture(Fixture):
    def setUp(self):
        super().setUp()
        self.root = self.store.location(self.pid).parent.parent
        self.documents = [self.book / 'AGENTS.md', self.amv / 'AGENTS.md', self.root / 'AGENTS.md',
                          self.amv / 'Plan.md', self.root / 'Plan.md']
        for index, path in enumerate(self.documents[:-1]):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f'Fixture source {index}\n', encoding='utf-8')
        self.guard_calls = {}
        for name in ['prepare', 'launch', 'worker', 'stop', 'command']:
            guard = patch.object(self.jobs, name, side_effect=AssertionError('Agent operation attempted generation: ' + name))
            self.guard_calls[name] = guard.start()
            self.addCleanup(guard.stop)

    def project_state(self):
        location = self.store.location(self.pid)
        return (location.read_bytes(), {f.name: digest(f) for f in (location.parent / 'History').glob('*.json')})

    def source_state(self):
        return {str(path): digest(path) for path in self.documents + self.images}

    def request_state(self):
        return {f.name: f.read_bytes() for f in (self.store.location(self.pid).parent / 'AgentRequests').glob('*.json')}

    def assert_no_generation(self):
        for guard in self.guard_calls.values():
            guard.assert_not_called()
        self.assertFalse((self.store.location(self.pid).parent / 'Jobs').exists())


class AgentHttpFixture(AgentFixture):
    def setUp(self):
        super().setUp()
        static = self.base / 'static'
        static.mkdir()
        (static / 'index.html').write_text('<!doctype html><title>Agent fixture</title>')
        handler = handler_for(self.store, self.jobs, static)
        handler.log_message = lambda *args: None
        self.http_calls = []
        route = handler.route
        def recorded(handler_self):
            self.http_calls.append((handler_self.command, handler_self.path))
            return route(handler_self)
        handler.route = recorded
        self.server = LocalServer(('127.0.0.1', 0), handler)
        self.port = self.server.server_address[1]
        self.url = f'http://127.0.0.1:{self.port}'
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .02}, daemon=True)
        self.thread.start()
        self.addCleanup(self.close_server)
        _, session = self.http('GET', '/api/bootstrap', authorized=False)
        self.token = session['token']
        self.http_calls.clear()

    def close_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)

    def http(self, method, path, payload=None, authorized=True):
        headers = {'Content-Type': 'application/json'}
        if authorized:
            headers['X-Studio-Token'] = self.token
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        try:
            connection.request(method, path, body=json.dumps(payload) if payload is not None else None, headers=headers)
            response = connection.getresponse()
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
        finally:
            connection.close()

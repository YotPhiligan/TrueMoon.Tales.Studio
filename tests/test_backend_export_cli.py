import hashlib
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import studio_agent as cli


ID = 'export-' + 'a' * 32
TOKEN = 'A' * 64
BASELINE = 'B' * 64
REQUEST_SHA = 'C' * 64
CLIENT_KEY = 'backend7-fixture-key'
MEDIA = b'mp4-fixture-' * 23000
ARTIFACT_SHA = hashlib.sha256(MEDIA).hexdigest()
START_ARGUMENTS = ['backend-export-start', '--client-key', CLIENT_KEY, '--profile', 'final',
                   '--revision', '12', '--sha256', BASELINE]


class Reply(io.BytesIO):
    def __init__(self, body, headers=None):
        super().__init__(body)
        self.headers = {} if headers is None else headers


def job(state='running', **changes):
    completed = state == 'completed'
    result = {'version': 1, 'id': ID, 'project_id': 'fixture', 'project_revision': 12,
              'project_fingerprint': BASELINE, 'profile': 'final', 'revision': 2 if completed else 1,
              'state': state, 'request_sha256': REQUEST_SHA, 'frames': 48, 'fps': 24,
              'artifact_size_bytes': len(MEDIA) if completed else None,
              'artifact_sha256': ARTIFACT_SHA if completed else None, 'error_code': None,
              'progress_percent': 100 if completed else None}
    return {**result, **changes}


def json_reply(value):
    return Reply(json.dumps(value).encode('utf-8'), {'Content-Type': 'application/json'})


def chunk_reply(data, **headers):
    return Reply(data, {'Content-Type': 'application/octet-stream', 'Content-Length': str(len(data)), **headers})


class BackendExportCliTests(unittest.TestCase):
    def invoke(self, opener, args):
        output, errors = io.StringIO(), io.StringIO()
        with patch.object(cli.request, 'build_opener', return_value=opener), redirect_stdout(output), redirect_stderr(errors):
            code = cli.main(['--url', 'http://127.0.0.1:12345', '--token', TOKEN, *args])
        return code, output.getvalue(), errors.getvalue()

    def request(self, opener, operation, body, index=0):
        call = opener.open.call_args_list[index]
        req = call.args[0]
        self.assertEqual('http://127.0.0.1:12345/api/backend/v1/export-' + operation, req.full_url)
        self.assertEqual('POST', req.method)
        self.assertEqual(TOKEN, req.get_header('X-studio-token'))
        self.assertEqual(body, json.loads(req.data))
        self.assertEqual(15, call.kwargs['timeout'])

    def assert_failure(self, result, exit_code=1):
        self.assertEqual((exit_code, ''), result[:2])
        self.assertFalse(json.loads(result[2])['automatic_retry'])

    def test_start_uses_client_key_and_observed_snapshot_in_one_request(self):
        opener = Mock()
        opener.open.return_value = json_reply(job())
        result = self.invoke(opener, START_ARGUMENTS)
        self.assertEqual((0, job(), ''), (result[0], json.loads(result[1]), result[2]))
        opener.open.assert_called_once()
        self.request(opener, 'start', {'client_key': CLIENT_KEY, 'profile': 'final',
                                       'expected_revision': 12, 'fingerprint': BASELINE})

    def test_status_recover_and_cancel_use_distinct_commands_and_job_revision(self):
        variants = [('status', job('completed'), {}, []),
                    ('recover', job('interrupted', revision=2, error_code='export_interrupted'), {}, []),
                    ('cancel', job('stopping', revision=2), {'expected_job_revision': 1}, ['--job-revision', '1'])]
        for operation, value, fields, args in variants:
            with self.subTest(operation=operation):
                opener = Mock()
                opener.open.return_value = json_reply(value)
                result = self.invoke(opener, [f'backend-export-{operation}', '--id', ID, *args])
                self.assertEqual((0, value, ''), (result[0], json.loads(result[1]), result[2]))
                opener.open.assert_called_once()
                self.request(opener, operation, {'id': ID, **fields})

    def test_protocol_rejects_other_project_shape_and_wrong_cancellation_reply(self):
        variants = [
            (START_ARGUMENTS, job(project_revision=13)),
            (START_ARGUMENTS, job(project_fingerprint='D' * 64)),
            (START_ARGUMENTS, job(profile='draft')),
            (START_ARGUMENTS, job(version=True)),
            (START_ARGUMENTS, job(fps=float('inf'))),
            (START_ARGUMENTS, job(path='C:/anywhere')),
            (['backend-export-status', '--id', ID], job(id='export-' + 'b' * 32)),
            (['backend-export-status', '--id', ID], job('completed', artifact_size_bytes=2**30 + 1)),
            (['backend-export-status', '--id', ID], job(artifact_sha256=ARTIFACT_SHA)),
            (['backend-export-cancel', '--id', ID, '--job-revision', '1'], job(revision=2)),
            (['backend-export-cancel', '--id', ID, '--job-revision', '1'], job('stopping')),
        ]
        for args, value in variants:
            with self.subTest(value=value):
                opener = Mock()
                opener.open.return_value = json_reply(value)
                self.assert_failure(self.invoke(opener, args))
                opener.open.assert_called_once()

    def test_refuses_unknown_payload_fields_or_unsafe_ids_before_http(self):
        opener = Mock()
        with patch.object(cli.request, 'build_opener', return_value=opener):
            client = cli.BackendClient('http://127.0.0.1:12345', TOKEN)
            for method, endpoint, body in [
                ('GET', 'status', {'id': ID}),
                ('POST', 'recover', {'id': '..'}),
                ('POST', 'cancel', {'id': ID, 'expected_job_revision': True}),
                ('POST', 'status', {'id': ID, 'path': 'C:/secret'}),
                ('POST', 'start', {'client_key': 'ñ' * 20, 'expected_revision': 12, 'profile': 'draft', 'fingerprint': BASELINE}),
                ('POST', 'chunk', {'id': ID, 'artifact_sha256': ARTIFACT_SHA, 'offset': -1}),
            ]:
                with self.subTest(endpoint=endpoint, body=body), self.assertRaises(cli.ClientError):
                    client.call(method, f'/api/backend/v1/export-{endpoint}', body)
        opener.open.assert_not_called()

    def test_lost_reply_or_conflict_never_resubmits_start(self):
        for error, code in [(URLError('lost'), 1),
                            (HTTPError('http://localhost', 409, 'conflict', {},
                                       io.BytesIO(b'{"error":"export_conflict"}')), 3)]:
            with self.subTest(code=code):
                opener = Mock()
                opener.open.side_effect = error
                self.assert_failure(self.invoke(opener, START_ARGUMENTS), code)
                opener.open.assert_called_once()

    def download(self, opener, target, sha=ARTIFACT_SHA):
        return self.invoke(opener, ['backend-export-download', '--id', ID, '--sha256', sha, '--output', str(target)])

    def test_download_checks_two_ranges_and_publishes_only_verified_file(self):
        opener = Mock()
        boundary = 256 * 1024
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'export.mp4'

            responses = iter([json_reply(job('completed')), chunk_reply(MEDIA[:boundary])])

            def request(*args, **kwargs):
                self.assertFalse(target.exists())
                return next(responses, None) or chunk_reply(MEDIA[boundary:])

            opener.open.side_effect = request
            result = self.download(opener, target)
            self.assertEqual((0, ''), (result[0], result[2]))
            self.assertEqual(MEDIA, target.read_bytes())
            self.assertEqual({'id': ID, 'artifact_sha256': ARTIFACT_SHA, 'artifact_size_bytes': len(MEDIA),
                              'output_path': str(target)}, json.loads(result[1]))
            self.assertEqual([target], list(Path(folder).iterdir()))
        self.assertEqual(3, opener.open.call_count)
        self.request(opener, 'status', {'id': ID})
        self.request(opener, 'chunk', {'id': ID, 'artifact_sha256': ARTIFACT_SHA, 'offset': 0}, index=1)
        self.request(opener, 'chunk', {'id': ID, 'artifact_sha256': ARTIFACT_SHA, 'offset': boundary}, index=2)

    def test_download_corrupt_truncated_or_failed_transfer_leaves_no_partial_file(self):
        boundary = 256 * 1024
        variants = [chunk_reply(b'x' + MEDIA[boundary + 1:]), chunk_reply(MEDIA[boundary:-1]),
                    chunk_reply(MEDIA[boundary:], **{'Content-Type': 'application/json'}),
                    chunk_reply(MEDIA[boundary:], **{'Content-Length': '262145'}), URLError('broken pipe')]
        for last in variants:
            with self.subTest(response=last), tempfile.TemporaryDirectory() as folder:
                target = Path(folder) / 'export.mp4'
                opener = Mock()
                opener.open.side_effect = [json_reply(job('completed')), chunk_reply(MEDIA[:boundary]), last]
                self.assert_failure(self.download(opener, target))
                self.assertEqual(3, opener.open.call_count)
                self.assertEqual([], list(Path(folder).iterdir()))

    def test_download_rejects_wrong_sha_unfinished_job_and_existing_destination(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'export.mp4'
            for value, sha in [(job(), ARTIFACT_SHA), (job('completed'), 'D' * 64)]:
                opener = Mock()
                opener.open.return_value = json_reply(value)
                self.assert_failure(self.download(opener, target, sha))
                opener.open.assert_called_once()
                self.assertFalse(target.exists())
            target.write_bytes(b'keep')
            opener = Mock()
            self.assert_failure(self.download(opener, target))
            opener.open.assert_not_called()
            self.assertEqual(b'keep', target.read_bytes())

    def test_output_created_during_transfer_is_preserved(self):
        boundary = 256 * 1024
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'export.mp4'
            responses = iter([json_reply(job('completed')), chunk_reply(MEDIA[:boundary])])

            def request(*args, **kwargs):
                response = next(responses, None)
                if response is not None:
                    return response
                target.write_bytes(b'other file')
                return chunk_reply(MEDIA[boundary:])

            opener = Mock()
            opener.open.side_effect = request
            self.assert_failure(self.download(opener, target))
            self.assertEqual(b'other file', target.read_bytes())
            self.assertEqual([target], list(Path(folder).iterdir()))


if __name__ == '__main__':
    unittest.main()

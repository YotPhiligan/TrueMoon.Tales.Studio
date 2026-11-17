import io
import json
import threading
import unittest
from contextlib import redirect_stdout, redirect_stderr
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError

import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import studio_agent as cli


TOKEN='A'*64
SHA='B'*64


def snapshot():
    return {'api_version':1,'schema_version':2,'fingerprint':SHA,'scenario_active':False,
            'project':{'contract_version':1,'id':'synthetic','revision':12}}


def response(value):
    handle=Mock()
    handle.__enter__=Mock(return_value=io.BytesIO(json.dumps(value).encode('utf-8')))
    handle.__exit__=Mock(return_value=False)
    return handle


class BackendCliTests(unittest.TestCase):
    def invoke(self,command,opener):
        out,err=io.StringIO(),io.StringIO()
        with patch.object(cli.request,'build_opener',return_value=opener),redirect_stdout(out),redirect_stderr(err):
            code=cli.main(['--url','http://127.0.0.1:12345','--token',TOKEN,*command])
        return code,out.getvalue(),err.getvalue()

    def test_move_sends_one_exact_observed_baseline_without_bootstrap_or_actor(self):
        opener=Mock();opener.open.return_value=response(snapshot())
        code,out,err=self.invoke(['backend-move','--clip','clip-A','--start-frame','14','--revision','12','--sha256',SHA],opener)
        self.assertEqual((0,''),(code,err));self.assertEqual(12,json.loads(out)['project']['revision'])
        self.assertEqual(1,opener.open.call_count)
        req=opener.open.call_args.args[0]
        self.assertEqual('POST',req.method);self.assertEqual('http://127.0.0.1:12345/api/backend/v1/move-clip',req.full_url)
        self.assertEqual({'clip_id':'clip-A','start_frame':14,'expected_revision':12,'fingerprint':SHA},json.loads(req.data))
        self.assertEqual(TOKEN,req.get_header('X-studio-token'))

    def test_read_is_authenticated_and_does_not_call_legacy_bootstrap(self):
        opener=Mock();opener.open.return_value=response(snapshot())
        code,out,err=self.invoke(['backend-project'],opener)
        self.assertEqual((0,''),(code,err));self.assertEqual(snapshot(),json.loads(out))
        req=opener.open.call_args.args[0]
        self.assertIsNone(req.data);self.assertEqual(TOKEN,req.get_header('X-studio-token'))
        self.assertTrue(req.full_url.endswith('/api/backend/v1/project'));self.assertEqual(1,opener.open.call_count)

    def test_conflict_transport_error_and_lost_reply_never_retry(self):
        failures=[HTTPError('http://127.0.0.1',409,'conflict',{},io.BytesIO(b'{"error":"revision_conflict"}')),
                  URLError('connection lost'),ValueError('truncated JSON')]
        for error in failures:
            with self.subTest(error=type(error).__name__):
                opener=Mock();opener.open.side_effect=error
                code,out,err=self.invoke(['backend-move','--clip','clip-A','--start-frame','14','--revision','12','--sha256',SHA],opener)
                self.assertEqual(3 if isinstance(error,HTTPError) else 1,code)
                self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
                self.assertEqual(1,opener.open.call_count)

    def test_invalid_baseline_and_project_selection_fail_before_request(self):
        for args in [['backend-move','--clip','A','--start-frame','14','--revision','-1','--sha256',SHA],
                     ['backend-move','--clip','A','--start-frame','14','--revision','12','--sha256','invalid'],
                     ['--project','other','backend-project']]:
            with self.subTest(args=args):
                opener=Mock();code,out,err=self.invoke(args,opener)
                self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
                opener.open.assert_not_called()

    def test_invalid_endpoint_or_token_never_creates_http_client(self):
        for url,token in [('https://127.0.0.1',TOKEN),('http://example.test',TOKEN),('http://localhost:1234',TOKEN),
                          ('http://127.0.0.1:1234?token=x',TOKEN),('http://127.0.0.1:1234','bad')]:
            with self.subTest(url=url,token=token),patch.object(cli.request,'build_opener') as build:
                with self.assertRaises(cli.ClientError):cli.BackendClient(url,token)
                build.assert_not_called()

    def test_invalid_success_envelope_requires_reconciliation_without_retry(self):
        variants=[]
        for key,value in [('api_version',2),('schema_version',1),('schema_version',2.0),('fingerprint','bad')]:
            data=snapshot();data[key]=value;variants.append(data)
        data=snapshot();data['project']['contract_version']=True;variants.append(data)
        data=snapshot();data['project']['revision']=True;variants.append(data)
        for data in variants:
            with self.subTest(data=data):
                opener=Mock();opener.open.return_value=response(data)
                code,out,err=self.invoke(['backend-project'],opener)
                self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
                self.assertEqual(1,opener.open.call_count)

    def test_redirect_is_not_followed_and_environment_proxy_is_not_used(self):
        calls=[]
        class Handler(BaseHTTPRequestHandler):
            def log_message(self,*args):pass
            def do_GET(self):
                calls.append((self.path,self.headers.get('X-Studio-Token')))
                if self.path=='/redirect':
                    self.send_response(302);self.send_header('Location','/stolen');self.end_headers()
                else:
                    encoded=json.dumps(snapshot()).encode()
                    self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(encoded)));self.end_headers();self.wfile.write(encoded)
        with ThreadingHTTPServer(('127.0.0.1',0),Handler) as server:
            thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
            url='http://127.0.0.1:'+str(server.server_port)
            try:
                with patch.dict('os.environ',{'http_proxy':url,'HTTP_PROXY':url,'no_proxy':'','NO_PROXY':''}):
                    client=cli.BackendClient(url,TOKEN)
                    self.assertEqual(snapshot(),client.call('GET','/api/backend/v1/project'))
                    with self.assertRaises(cli.ClientError) as caught:client.call('GET','/redirect')
                self.assertEqual(302,caught.exception.status)
                self.assertEqual([('/api/backend/v1/project',TOKEN),('/redirect',TOKEN)],calls)
            finally:server.shutdown();thread.join(5)


if __name__=='__main__':unittest.main()

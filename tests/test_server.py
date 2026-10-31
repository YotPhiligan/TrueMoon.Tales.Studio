import http.client,json,threading
from unittest.mock import patch
from fixtures import Fixture
from studio.server import LocalServer,handler_for,byte_range
from studio.model import StudioError

class HttpTests(Fixture):
    def setUp(self):
        super().setUp();self.take,_=self.add_take();self.static=self.base/'static';self.static.mkdir();(self.static/'index.html').write_text('<!doctype html><title>Fixture</title>')
        handler=handler_for(self.store,self.jobs,self.static);handler.log_message=lambda *args:None
        self.server=LocalServer(('127.0.0.1',0),handler);self.port=self.server.server_address[1]
        self.thread=threading.Thread(target=self.server.serve_forever,kwargs={'poll_interval':.02},daemon=True);self.thread.start()
        self.addCleanup(self.close_server)
        status,_,body=self.request('GET','/api/bootstrap');self.assertEqual(200,status);self.token=json.loads(body)['token']
    def close_server(self):
        self.server.shutdown();self.server.server_close();self.thread.join(2)
    def request(self,method,path,payload=None,headers=None):
        h=dict(headers or {});body=None
        if payload is not None:body=json.dumps(payload);h.setdefault('Content-Type','application/json')
        con=http.client.HTTPConnection('127.0.0.1',self.port,timeout=5)
        try:
            con.request(method,path,body=body,headers=h);r=con.getresponse();return r.status,dict(r.getheaders()),r.read()
        finally:con.close()
    def media_path(self):return f'/api/media?project={self.pid}&asset={self.take["video_asset_id"]}'

    def test_bootstrap_context_and_health_do_not_prepare_or_launch(self):
        with patch.object(self.jobs,'prepare') as prepare,patch.object(self.jobs,'launch') as launch:
            health=self.request('GET','/api/health');context=self.request('GET',f'/api/projects/{self.pid}/context?block={self.bid}')
        self.assertEqual(200,health[0]);self.assertEqual(0,json.loads(health[2])['gpu_jobs_started_by_health'])
        data=json.loads(context[2]);self.assertEqual(200,context[0]);self.assertEqual(self.current()['revision'],data['project']['revision']);self.assertEqual(3,len(data['assets']))
        prepare.assert_not_called();launch.assert_not_called()

    def test_valid_patch_requires_token_and_conflicting_revision_is_409(self):
        revision=self.current()['revision'];path=f'/api/projects/{self.pid}/blocks/{self.bid}'
        denied=self.request('PATCH',path,{'expected_revision':revision,'block':{'notes':'denied'}})
        self.assertEqual(403,denied[0]);self.assertNotEqual('denied',self.block()['notes'])
        allowed=self.request('PATCH',path,{'expected_revision':revision,'block':{'notes':'Принятая правка'}},{'X-Studio-Token':self.token})
        self.assertEqual(200,allowed[0]);self.assertEqual(revision+1,json.loads(allowed[2])['revision'])
        stale=self.request('PATCH',path,{'expected_revision':revision,'block':{'notes':'stale'}},{'X-Studio-Token':self.token})
        self.assertEqual(409,stale[0]);self.assertEqual('Принятая правка',self.block()['notes'])

    def test_foreign_host_origin_and_fetch_site_are_rejected(self):
        for headers in [{'Host':'attacker.invalid'},{'Origin':'http://attacker.invalid'},{'Sec-Fetch-Site':'cross-site'}]:
            with self.subTest(headers=headers):self.assertEqual(403,self.request('GET','/api/projects',headers=headers)[0])
        self.assertEqual(200,self.request('GET','/api/projects',headers={'Origin':f'http://127.0.0.1:{self.port}'})[0])

    def test_media_full_ranges_suffix_and_head_return_correct_bytes(self):
        full=self.request('GET',self.media_path());self.assertEqual(200,full[0]);self.assertEqual(bytes(range(256)),full[2]);self.assertEqual('bytes',full[1]['Accept-Ranges'])
        for value,start,end in [('bytes=10-19',10,19),('bytes=-12',244,255),('bytes=254-',254,255),('bytes=250-999',250,255)]:
            with self.subTest(range=value):
                status,headers,body=self.request('GET',self.media_path(),headers={'Range':value})
                self.assertEqual(206,status);self.assertEqual(bytes(range(start,end+1)),body);self.assertEqual(f'bytes {start}-{end}/256',headers['Content-Range']);self.assertEqual(str(end-start+1),headers['Content-Length'])
        status,headers,body=self.request('HEAD',self.media_path(),headers={'Range':'bytes=0-3'})
        self.assertEqual(206,status);self.assertEqual('4',headers['Content-Length']);self.assertEqual(b'',body)

    def test_out_of_range_and_multi_range_return_416_without_media(self):
        for value in ['bytes=256-','bytes=8-7','bytes=-0','bytes=0-1,3-4','bytes=-','items=1-2']:
            with self.subTest(range=value):
                status,headers,body=self.request('GET',self.media_path(),headers={'Range':value})
                self.assertEqual(416,status);self.assertEqual('bytes */256',headers['Content-Range']);self.assertEqual(b'',body)

    def test_abandoned_media_connection_closes_without_500_or_server_error(self):
        for failure in [ConnectionAbortedError,ConnectionResetError,BrokenPipeError]:
            with self.subTest(failure=failure.__name__):
                with patch.object(self.server.RequestHandlerClass,'file',side_effect=failure('viewer closed')),patch.object(self.server,'handle_error') as server_error,patch('traceback.print_exc') as trace:
                    with self.assertRaises(http.client.RemoteDisconnected):self.request('GET',self.media_path())
                server_error.assert_not_called();trace.assert_not_called()
                self.assertEqual(200,self.request('GET','/api/health')[0])

    def test_latent_and_static_traversal_are_not_served(self):
        path=f'/api/media?project={self.pid}&asset={self.take["native_av_asset_id"]}'
        self.assertEqual(403,self.request('GET',path)[0])
        self.assertEqual(404,self.request('GET','/%2e%2e/studio/model.py')[0])
        self.assertEqual(404,self.request('GET','/app.py')[0])

    def test_prepare_is_explicit_separate_from_launch(self):
        path=f'/api/projects/{self.pid}/blocks/{self.bid}/prepare'
        with patch.object(self.jobs,'prepare',return_value={'id':'job-test','state':'prepared'}) as prepare,patch.object(self.jobs,'launch') as launch:
            status,_,body=self.request('POST',path,{'expected_revision':self.current()['revision']},{'X-Studio-Token':self.token})
        self.assertEqual(201,status);self.assertEqual('prepared',json.loads(body)['state']);prepare.assert_called_once_with(self.pid,self.bid,self.current()['revision'],'author');launch.assert_not_called()

class RangeTests(Fixture):
    def test_empty_full_file_and_empty_range_differ(self):
        self.assertEqual((0,-1,False),byte_range(None,0))
        with self.assertRaises(StudioError) as caught:byte_range('bytes=0-',0)
        self.assertEqual(416,caught.exception.status)

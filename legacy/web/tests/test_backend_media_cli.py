import base64
import copy
import hashlib
import io
import json
import os
import struct
import sys
import tempfile
import threading
import unittest
import zlib
from contextlib import redirect_stdout, redirect_stderr
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import studio_agent as cli
from studio.paths import WorkspacePaths, PathPolicyError

TOKEN='A'*64
SHA='B'*64
PIXELS=bytes(range(256))*2025
PIXEL_SHA=hashlib.sha256(PIXELS).hexdigest()
ASSET='asset-синтетический'


def snapshot(frame=-1):
    return {'api_version':1,'schema_version':2,'media':{
        'error_code':'','asset_id':ASSET,'revision':12,'fingerprint':SHA,'fps':24.0,'frame_count':48,
        'source_width':960,'source_height':540,'frame_index':frame,'width':480,'height':270,
        'pixels':'' if frame<0 else base64.b64encode(PIXELS).decode('ascii'),
        'pixel_sha256':'' if frame<0 else PIXEL_SHA}}


def response(value,after_read=None):
    encoded=value if isinstance(value,bytes) else json.dumps(value,ensure_ascii=False).encode('utf-8')
    class Body(io.BytesIO):
        def read(self,*args):
            result=super().read(*args)
            if after_read:after_read()
            return result
    result=Mock()
    result.__enter__=Mock(return_value=Body(encoded))
    result.__exit__=Mock(return_value=False)
    return result


class BackendMediaCliTests(unittest.TestCase):
    def invoke(self,args,opener,global_args=()):
        out,err=io.StringIO(),io.StringIO()
        with patch.object(cli.request,'build_opener',return_value=opener),redirect_stdout(out),redirect_stderr(err):
            code=cli.main(['--url','http://127.0.0.1:12345','--token',TOKEN,*global_args,*args])
        return code,out.getvalue(),err.getvalue()

    def args(self,output=None):
        args=['backend-media' if output is None else 'backend-frame','--asset',ASSET,'--revision','12','--sha256',SHA]
        return args if output is None else args+['--frame','7','--output',str(output)]

    def assert_failure(self,result,status=None):
        code,out,err=result
        self.assertEqual(3 if status==409 else 1,code)
        self.assertEqual('',out)
        data=json.loads(err)
        self.assertFalse(data['automatic_retry'])
        if status is not None:self.assertEqual(status,data['status'])

    def test_media_sends_one_exact_observed_baseline_with_token_without_bootstrap_paths_or_actor(self):
        opener=Mock();opener.open.return_value=response(snapshot())
        code,out,err=self.invoke(self.args(),opener)
        self.assertEqual((0,''),(code,err));self.assertEqual(snapshot(),json.loads(out))
        self.assertEqual(1,opener.open.call_count)
        req=opener.open.call_args.args[0]
        self.assertEqual('POST',req.method)
        self.assertEqual('http://127.0.0.1:12345/api/backend/v1/media-info',req.full_url)
        self.assertEqual(TOKEN,req.get_header('X-studio-token'))
        self.assertEqual({'asset_id':ASSET,'expected_revision':12,'fingerprint':SHA},json.loads(req.data))

    def test_frame_exports_png_whose_rgba_rows_crc_and_hash_match_validated_bgra_without_base64_stdout(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-frame-') as temp:
            output=Path(temp)/'кадр 07.png'
            opener=Mock();opener.open.return_value=response(snapshot(7))
            code,out,err=self.invoke(self.args(output),opener)
            self.assertEqual((0,''),(code,err));self.assertEqual(1,opener.open.call_count)
            req=opener.open.call_args.args[0]
            self.assertTrue(req.full_url.endswith('/api/backend/v1/frame'))
            self.assertEqual({'asset_id':ASSET,'expected_revision':12,'fingerprint':SHA,'frame':7},json.loads(req.data))
            result=json.loads(out);self.assertLess(len(out),2000)
            self.assertNotIn('pixels',result['media']);self.assertEqual(PIXEL_SHA,result['media']['pixel_sha256'])
            self.assertEqual(str(output),result['output_path'])
            png=output.read_bytes()
            self.assertEqual(hashlib.sha256(png).hexdigest(),result['png_sha256'])
            self.assertEqual(b'\x89PNG\r\n\x1a\n',png[:8])
            offset=8;chunks=[]
            while offset<len(png):
                length=struct.unpack('!I',png[offset:offset+4])[0];kind=png[offset+4:offset+8]
                data=png[offset+8:offset+8+length];crc=struct.unpack('!I',png[offset+8+length:offset+12+length])[0]
                self.assertEqual(zlib.crc32(kind+data)&0xffffffff,crc)
                chunks.append((kind,data));offset+=length+12
            self.assertEqual(len(png),offset);self.assertEqual([b'IHDR',b'IDAT',b'IEND'],[k for k,d in chunks])
            self.assertEqual((480,270,8,6,0,0,0),struct.unpack('!IIBBBBB',chunks[0][1]))
            rows=zlib.decompress(chunks[1][1]);self.assertEqual((480*4+1)*270,len(rows))
            rgba=bytearray(len(PIXELS));rgba[0::4]=PIXELS[2::4];rgba[1::4]=PIXELS[1::4];rgba[2::4]=PIXELS[0::4];rgba[3::4]=PIXELS[3::4]
            self.assertEqual(b''.join(b'\0'+rgba[i:i+1920] for i in range(0,len(rgba),1920)),rows)

    def test_first_and_last_frame_are_accepted_but_exclusive_end_is_refused_without_output(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-frame-boundaries-') as temp:
            for frame in [0,47,48]:
                with self.subTest(frame=frame):
                    output=Path(temp)/f'{frame}.png'
                    args=self.args(output);args[args.index('--frame')+1]=str(frame)
                    opener=Mock();opener.open.return_value=response(snapshot(frame))
                    result=self.invoke(args,opener)
                    self.assertEqual(1,opener.open.call_count)
                    if frame==48:
                        self.assert_failure(result);self.assertFalse(output.exists())
                    else:
                        self.assertEqual((0,''),(result[0],result[2]))
                        self.assertEqual(frame,json.loads(result[1])['media']['frame_index'])
                        self.assertTrue(output.read_bytes().startswith(b'\x89PNG\r\n\x1a\n'))

    def test_equivalent_baseline_sha_case_is_accepted_without_refresh_or_retry(self):
        data=snapshot();data['media']['fingerprint']=SHA.lower()
        opener=Mock();opener.open.return_value=response(data)
        code,out,err=self.invoke(self.args(),opener)
        self.assertEqual((0,''),(code,err));self.assertEqual(data,json.loads(out));self.assertEqual(1,opener.open.call_count)
        self.assertEqual(SHA,json.loads(opener.open.call_args.args[0].data)['fingerprint'])

    def test_corrupt_bytes_sha_dimensions_frame_and_baseline_never_create_output_or_retry(self):
        mutations=[('pixels','?'+snapshot(7)['media']['pixels'][1:]),
                   ('pixels',base64.b64encode(bytes([255])*len(PIXELS)).decode()),
                   ('pixels',snapshot(7)['media']['pixels'][:-4]),('pixels',None),
                   ('pixel_sha256','F'*64),('pixel_sha256',True),('width',479),('height',271),
                   ('asset_id','other'),('revision',13),('fingerprint','C'*64),('frame_index',8),('frame_count',7)]
        with tempfile.TemporaryDirectory(prefix='studio-cli-refuse-') as temp:
            for i,(field,value) in enumerate(mutations):
                with self.subTest(field=field,value_type=type(value).__name__):
                    data=snapshot(7);data['media'][field]=value
                    opener=Mock();opener.open.return_value=response(data);output=Path(temp)/f'{i}.png'
                    self.assert_failure(self.invoke(self.args(output),opener))
                    self.assertFalse(output.exists());self.assertEqual(1,opener.open.call_count)

    def test_response_integers_reject_bool_and_float_and_fps_rejects_nonfinite_or_out_of_range(self):
        variants=[]
        for field in ['revision','frame_count','source_width','source_height','width','height','frame_index']:
            for value in [True,False,float(snapshot(7)['media'][field])]:
                data=snapshot(7);data['media'][field]=value;variants.append((field,data))
        for field,value in [('api_version',True),('api_version',1.0),('schema_version',True),('schema_version',2.0)]:
            data=snapshot(7);data[field]=value;variants.append((field,data))
        for value in [True,False,0,-1,240.01,float('nan'),float('inf'),10**400,'24']:
            data=snapshot(7);data['media']['fps']=value;variants.append(('fps',data))
        for field,value in [('source_width',8193),('source_height',8193),('source_width',0),('frame_count',0),('frame_count',10_000_001)]:
            data=snapshot(7);data['media'][field]=value;variants.append((field,data))
        data=snapshot(7);data['media'].update(source_width=8192,source_height=8192);variants.append(('area',data))
        with tempfile.TemporaryDirectory(prefix='studio-cli-types-') as temp:
            output=Path(temp)/'rejected.png'
            for label,data in variants:
                with self.subTest(field=label,data_type=type(data['media'].get(label)).__name__):
                    opener=Mock();opener.open.return_value=response(data)
                    self.assert_failure(self.invoke(self.args(output),opener))
                    self.assertFalse(output.exists());self.assertEqual(1,opener.open.call_count)

    def test_media_rate_source_extent_and_frame_count_accept_supported_boundaries(self):
        for fps,width,height,count in [(0.01,1,1,1),(240,8192,1,10_000_000),(24,4096,4096,48)]:
            with self.subTest(fps=fps,width=width,height=height,count=count):
                data=snapshot();data['media'].update(fps=fps,source_width=width,source_height=height,frame_count=count)
                opener=Mock();opener.open.return_value=response(data)
                code,out,err=self.invoke(self.args(),opener)
                self.assertEqual((0,''),(code,err));self.assertEqual(data,json.loads(out))

    def test_metadata_reply_rejects_pixels_missing_unknown_and_duplicate_fields(self):
        variants=[]
        for field,value in [('pixels','AA=='),('pixel_sha256',PIXEL_SHA),('frame_index',0),('error_code','media_not_found')]:
            data=snapshot();data['media'][field]=value;variants.append(data)
        data=snapshot();del data['media']['fps'];variants.append(data)
        data=snapshot();data['media']['path']='C:/not-authority.mp4';variants.append(data)
        data=snapshot();data['project']={};variants.append(data)
        data=snapshot();data['media']=[];variants.append(data)
        encoded=json.dumps(snapshot()).encode();variants.append(encoded.replace(b'"fps": 24.0',b'"fps": 24.0, "fps": 24.0'))
        for data in variants:
            with self.subTest(data_type=type(data).__name__):
                opener=Mock();opener.open.return_value=response(data)
                self.assert_failure(self.invoke(self.args(),opener));self.assertEqual(1,opener.open.call_count)

    def test_existing_file_and_project_or_media_outputs_are_rejected_before_http_without_overwrite(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-preserve-') as temp:
            root=Path(temp);existing=root/'existing.png';existing.write_bytes(b'author image')
            for output in [existing,root/'Studio.project.json',root/'video.mp4',root/'missing'/'frame.png']:
                with self.subTest(output=output.name):
                    opener=Mock();self.assert_failure(self.invoke(self.args(output),opener));opener.open.assert_not_called()
                    self.assertEqual(b'author image',existing.read_bytes())
                    if output!=existing:self.assertFalse(output.exists())

    @unittest.skipUnless(os.name=='nt','Windows alternate stream output contract')
    def test_windows_ads_and_ambiguous_output_names_are_rejected_before_http_preserving_existing_media(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-no-ads-') as temp:
            root=Path(temp);media=root/'existing-video.mp4';media.write_bytes(b'author media must remain unchanged')
            stream=Path(str(media)+':frame.png')
            self.assertFalse(stream.exists())
            for output in [stream,root/'new.png.',root/'new.png ']:
                with self.subTest(output=output.name):
                    opener=Mock();self.assert_failure(self.invoke(self.args(output),opener))
                    opener.open.assert_not_called()
                    self.assertEqual(b'author media must remain unchanged',media.read_bytes())
                    self.assertFalse(stream.exists());self.assertFalse(output.exists())
            self.assertEqual([media],list(root.iterdir()))

    def test_output_created_during_http_is_preserved_by_exclusive_creation(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-output-race-') as temp:
            output=Path(temp)/'race.png'
            opener=Mock();opener.open.return_value=response(snapshot(7),lambda:output.write_bytes(b'concurrent author image'))
            self.assert_failure(self.invoke(self.args(output),opener))
            self.assertEqual(b'concurrent author image',output.read_bytes());self.assertEqual(1,opener.open.call_count)

    def test_http_conflict_refusal_lost_reply_and_oversize_never_create_output_or_retry(self):
        failures=[HTTPError('http://127.0.0.1',code,'refused',{},io.BytesIO(b'{"error":"refused"}')) for code in [403,404,409,422,503]]
        failures += [URLError('reply lost'),ValueError('truncated response')]
        with tempfile.TemporaryDirectory(prefix='studio-cli-http-fail-') as temp:
            output=Path(temp)/'absent.png'
            for error in failures:
                with self.subTest(error_type=type(error).__name__,status=getattr(error,'code',None)):
                    opener=Mock();opener.open.side_effect=error
                    self.assert_failure(self.invoke(self.args(output),opener),getattr(error,'code',None))
                    self.assertFalse(output.exists());self.assertEqual(1,opener.open.call_count)
            for encoded in [b'{"api_version":',b'X'*(2*1024*1024+1)]:
                opener=Mock();opener.open.return_value=response(encoded)
                self.assert_failure(self.invoke(self.args(output),opener))
                self.assertFalse(output.exists());self.assertEqual(1,opener.open.call_count)

    def test_invalid_baseline_asset_frame_or_project_selection_fails_before_http(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-bad-query-') as temp:
            output=Path(temp)/'none.png'
            for flag,value in [('--revision','-1'),('--revision',str(2**63)),('--sha256','bad'),('--asset',''),
                               ('--asset',' '),('--asset','a'*161),('--asset','🌙'*81),('--asset','bad\0id'),
                               ('--frame','-1'),('--frame',str(2**31))]:
                with self.subTest(flag=flag,value=value):
                    args=self.args(output);args[args.index(flag)+1]=value
                    opener=Mock();self.assert_failure(self.invoke(args,opener));opener.open.assert_not_called();self.assertFalse(output.exists())
            opener=Mock();self.assert_failure(self.invoke(self.args(output),opener,('--project','other')));opener.open.assert_not_called()

    def test_direct_media_query_rejects_boolean_and_float_integers_before_http(self):
        body={'asset_id':ASSET,'expected_revision':12,'fingerprint':SHA,'frame':7}
        variants=[]
        for field in ['expected_revision','frame']:
            for value in [True,False,7.0]:
                query=dict(body);query[field]=value;variants.append(query)
        query=dict(body);query['path']='C:/no-authority.mp4';variants.append(query)
        for query in variants:
            with self.subTest(query=query):
                opener=Mock()
                with patch.object(cli.request,'build_opener',return_value=opener):
                    client=cli.BackendClient('http://127.0.0.1:12345',TOKEN)
                    with self.assertRaises(cli.ClientError):client.call('POST','/api/backend/v1/frame',query)
                opener.open.assert_not_called()

    def test_frame_cli_requires_output_and_frame_without_creating_client(self):
        for omitted in ['--frame','--output','--asset','--revision','--sha256']:
            args=self.args(Path('new.png'));i=args.index(omitted);del args[i:i+2]
            with self.subTest(omitted=omitted),patch.object(cli.request,'build_opener') as build,redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as caught:cli.main(['--token',TOKEN,*args])
                self.assertEqual(2,caught.exception.code);build.assert_not_called()

    def test_post_ignores_environment_proxy_and_refuses_redirect_without_bootstrap_or_output(self):
        calls=[]
        class Handler(BaseHTTPRequestHandler):
            def log_message(self,*args):pass
            def do_POST(self):
                payload=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                calls.append((self.path,self.headers.get('X-Studio-Token'),payload))
                if self.path.endswith('/frame'):
                    self.send_response(302);self.send_header('Location','/stolen');self.end_headers()
                else:
                    encoded=json.dumps(snapshot()).encode()
                    self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(encoded)));self.end_headers();self.wfile.write(encoded)
            def do_GET(self):
                calls.append((self.path,None,None));self.send_response(500);self.end_headers()
        with tempfile.TemporaryDirectory(prefix='studio-cli-redirect-') as temp,ThreadingHTTPServer(('127.0.0.1',0),Handler) as server:
            thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
            url='http://127.0.0.1:'+str(server.server_port)
            output=Path(temp)/'none.png'
            try:
                with patch.dict('os.environ',{'http_proxy':'http://127.0.0.1:1','HTTP_PROXY':'http://127.0.0.1:1','no_proxy':'','NO_PROXY':''}):
                    out,err=io.StringIO(),io.StringIO()
                    with redirect_stdout(out),redirect_stderr(err):
                        code=cli.main(['--url',url,'--token',TOKEN,*self.args()])
                    self.assertEqual((0,''),(code,err.getvalue()))
                    self.assertEqual(snapshot(),json.loads(out.getvalue()))
                    out,err=io.StringIO(),io.StringIO()
                    with redirect_stdout(out),redirect_stderr(err):
                        code=cli.main(['--url',url,'--token',TOKEN,*self.args(output)])
                    self.assert_failure((code,out.getvalue(),err.getvalue()),302)
                self.assertFalse(output.exists())
                self.assertEqual(['/api/backend/v1/media-info','/api/backend/v1/frame'],[row[0] for row in calls])
                self.assertTrue(all(row[1]==TOKEN for row in calls))
            finally:server.shutdown();thread.join(5);self.assertFalse(thread.is_alive())


class BackendMediaConfigCompatibilityTests(unittest.TestCase):
    def test_backend_tool_keys_are_optional_validated_and_do_not_change_legacy_read_policy(self):
        with tempfile.TemporaryDirectory(prefix='studio-cli-config-') as temp:
            root=Path(temp);code=root/'Studio';code.mkdir()
            data={'version':1,'legacy_root':str(root/'disabled'),'legacy_read_enabled':False,
                  'amv_root':str(root/'videos'),'projects_root':str(root/'projects'),'state_root':str(root/'state'),
                  'mounts':{'videos':str(root/'videos')},'protected_roots':[str(root/'TrueMoon')]}
            config=code/'Studio.json';config.write_text(json.dumps(data))
            before=WorkspacePaths.load(config,code_root=code)
            data.update(ffmpeg_executable='../tools/ffmpeg.exe',ffprobe_executable='../tools/ffprobe.exe')
            config.write_text(json.dumps(data))
            after=WorkspacePaths.load(config,code_root=code)
            self.assertEqual(before.export_policy(),after.export_policy())
            self.assertEqual(before.mounts,after.mounts);self.assertFalse(after.legacy_read_enabled)
            self.assertFalse((root/'disabled').exists());self.assertFalse((root/'tools').exists())
            for key in ['ffmpeg_executable','ffprobe_executable']:
                for value in [None,False,123,'',[],{}]:
                    invalid=copy.deepcopy(data);invalid[key]=value;config.write_text(json.dumps(invalid))
                    with self.subTest(key=key,value_type=type(value).__name__),self.assertRaises(PathPolicyError) as caught:
                        WorkspacePaths.load(config,code_root=code)
                    self.assertEqual(422,caught.exception.status)
            invalid=copy.deepcopy(data);invalid['ffmpeg_path']='typo';config.write_text(json.dumps(invalid))
            with self.assertRaises(PathPolicyError):WorkspacePaths.load(config,code_root=code)


if __name__=='__main__':unittest.main()

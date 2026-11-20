import io
import json
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import studio_agent as cli

TOKEN='A'*64
SHA='B'*64
SAVED='C'*64


def snapshot(revision=13, fingerprint=SAVED):
    return {'api_version':1,'schema_version':2,'fingerprint':fingerprint,'scenario_active':False,
            'project':{'contract_version':1,'id':'synthetic','revision':revision,'fps':24,
                       'montage':{'clips':[{'id':'clip-A','start_frame':14,'duration_frames':20,
                                           'source_in_frame':8,'source_out_frame':48,'speed':2}]}}}


def response(value):
    handle=Mock()
    handle.__enter__=Mock(return_value=io.BytesIO(json.dumps(value).encode('utf-8')))
    handle.__exit__=Mock(return_value=False)
    return handle


def query():
    return {'clip_id':'clip-A','source_in_frame':8.0,'source_out_frame':48.0,'start_frame':14,
            'expected_revision':12,'fingerprint':SHA}


class BackendTrimCliTests(unittest.TestCase):
    def args(self):
        return ['backend-trim','--clip','clip-A','--source-in','8','--source-out','48',
                '--start','14','--revision','12','--sha256',SHA]

    def invoke(self, opener, args=None):
        out,err=io.StringIO(),io.StringIO()
        with patch.object(cli.request,'build_opener',return_value=opener),redirect_stdout(out),redirect_stderr(err):
            code=cli.main(['--url','http://127.0.0.1:12345','--token',TOKEN,*(args or self.args())])
        return code,out.getvalue(),err.getvalue()

    def test_trim_sends_one_exact_baseline_without_actor_bootstrap_or_file_authority(self):
        opener=Mock();opener.open.return_value=response(snapshot())
        code,out,err=self.invoke(opener)
        self.assertEqual((0,''),(code,err));self.assertEqual(snapshot(),json.loads(out))
        opener.open.assert_called_once()
        req=opener.open.call_args.args[0]
        self.assertEqual('POST',req.method)
        self.assertEqual('http://127.0.0.1:12345/api/backend/v1/trim-clip',req.full_url)
        self.assertEqual(query(),json.loads(req.data));self.assertEqual(TOKEN,req.get_header('X-studio-token'))
        self.assertEqual(15,opener.open.call_args.kwargs['timeout'])

    def test_trim_accepts_noop_baseline_and_implicit_source_end_without_inventing_commit(self):
        value=snapshot(12,SHA);value['project']['montage']['clips'][0]['source_out_frame']=None
        opener=Mock();opener.open.return_value=response(value)
        code,out,err=self.invoke(opener)
        self.assertEqual((0,''),(code,err));self.assertEqual(12,json.loads(out)['project']['revision'])
        self.assertEqual(SHA,json.loads(out)['fingerprint']);opener.open.assert_called_once()

    def test_start_frame_alias_preserves_exact_argument_and_single_post(self):
        args=self.args();args[args.index('--start')]='--start-frame'
        opener=Mock();opener.open.return_value=response(snapshot())
        code,out,err=self.invoke(opener,args)
        self.assertEqual((0,''),(code,err));self.assertEqual(13,json.loads(out)['project']['revision'])
        self.assertEqual(query(),json.loads(opener.open.call_args.args[0].data));opener.open.assert_called_once()

    def test_nonfinite_reversed_negative_or_oversized_arguments_fail_before_http(self):
        variants=[('--source-in','nan'),('--source-in','-1'),('--source-out','inf'),('--source-out','8'),
                  ('--source-out','4'),('--start','-1'),('--start',str(2**31)),('--revision','-1'),
                  ('--revision',str(2**63)),('--sha256','invalid'),('--clip',' '),('--clip','x'*161)]
        for flag,value in variants:
            with self.subTest(flag=flag,value=value):
                args=self.args();args[args.index(flag)+1]=value
                opener=Mock();code,out,err=self.invoke(opener,args)
                self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
                opener.open.assert_not_called()

    def test_project_override_is_refused_before_http(self):
        opener=Mock();code,out,err=self.invoke(opener,['--project','different',*self.args()])
        self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
        opener.open.assert_not_called()

    def test_raw_client_requires_post_and_exact_typed_payload_before_http(self):
        variants=[]
        for key,value in [('expected_revision',True),('source_in_frame',False),('source_out_frame',float('nan')),
                          ('start_frame',1.0),('clip_id','clip\0unsafe'),('source_out_frame',10**400)]:
            body=query();body[key]=value;variants.append(body)
        body=query();body['file']='outside.mp4';variants.append(body)
        body=query();del body['source_in_frame'];variants.append(body)
        opener=Mock()
        with patch.object(cli.request,'build_opener',return_value=opener):
            client=cli.BackendClient('http://127.0.0.1:12345',TOKEN)
            with self.assertRaises(cli.ClientError):client.call('GET','/api/backend/v1/trim-clip',query())
            for body in variants:
                with self.subTest(body=body),self.assertRaises(cli.ClientError):
                    client.call('POST','/api/backend/v1/trim-clip',body)
        opener.open.assert_not_called()

    def test_conflict_domain_refusal_and_lost_reply_are_not_retried(self):
        failures=[HTTPError('http://127.0.0.1',409,'conflict',{},io.BytesIO(b'{"error":"revision_conflict"}')),
                  HTTPError('http://127.0.0.1',422,'trim',{},io.BytesIO(b'{"error":"invalid_trim"}')),
                  URLError('lost reply'),TimeoutError('deadline')]
        for failure in failures:
            with self.subTest(error=type(failure).__name__):
                opener=Mock();opener.open.side_effect=failure
                code,out,err=self.invoke(opener)
                self.assertEqual(3 if getattr(failure,'code',None)==409 else 1,code)
                self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry']);opener.open.assert_called_once()

    def test_invalid_revision_or_sha_transition_is_not_reported_as_success(self):
        for revision,digest in [(11,SAVED),(14,SAVED),(12,SAVED),(13,SHA),(True,SAVED)]:
            with self.subTest(revision=revision,digest=digest):
                opener=Mock();opener.open.return_value=response(snapshot(revision,digest))
                code,out,err=self.invoke(opener)
                self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
                opener.open.assert_called_once()

    def test_invalid_saved_clip_timing_identity_or_duplicate_is_refused_without_retry(self):
        values=[]
        for key,value in [('source_in_frame',9),('source_in_frame',True),('source_out_frame',49),
                          ('source_out_frame',float('nan')),('start_frame',15),('start_frame',14.0),
                          ('duration_frames',21),('duration_frames',True),('speed',0),('speed',.249),('speed',4.001),('speed',1e-320),('speed',float('inf')),('id','other')]:
            data=snapshot();data['project']['montage']['clips'][0][key]=value;values.append(data)
        data=snapshot();data['project']['montage']['clips']*=2;values.append(data)
        data=snapshot();data['project'].pop('montage');values.append(data)
        data=snapshot();data['project']['montage']['clips']=None;values.append(data)
        for data in values:
            with self.subTest(data=data):
                opener=Mock();opener.open.return_value=response(data)
                code,out,err=self.invoke(opener)
                self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
                opener.open.assert_called_once()

    def test_extreme_finite_response_extent_is_a_classified_error_without_retry(self):
        args=self.args();args[args.index('--source-out')+1]='1e308'
        data=snapshot();data['project']['montage']['clips'][0].update(source_out_frame=1e308,speed=.25)
        opener=Mock();opener.open.return_value=response(data)
        code,out,err=self.invoke(opener,args)
        self.assertEqual(1,code);self.assertEqual('',out);self.assertFalse(json.loads(err)['automatic_retry'])
        opener.open.assert_called_once()

    def test_missing_required_absolute_trim_arguments_are_parser_errors(self):
        for flag in ['--source-in','--source-out','--start','--revision','--sha256']:
            with self.subTest(flag=flag):
                args=self.args();index=args.index(flag);del args[index:index+2]
                with redirect_stderr(io.StringIO()),self.assertRaises(SystemExit) as caught:cli.parser().parse_args(args)
                self.assertEqual(2,caught.exception.code)


if __name__=='__main__':unittest.main()

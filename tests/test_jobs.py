import copy,subprocess
from pathlib import Path
from unittest.mock import Mock,patch
from fixtures import Fixture
from studio.model import StudioError,atomic,digest,read
from studio.jobs import Jobs

class JobTests(Fixture):
    def test_missing_powershell7_refuses_preparation_before_job_creation(self):
        with patch.object(self.jobs,'powershell',side_effect=StudioError('PowerShell 7 required',422)),patch.object(self.jobs,'command') as command:
            with self.assertRaises(StudioError) as caught:self.jobs.prepare(self.pid,self.bid,self.current()['revision'])
        self.assertEqual(422,caught.exception.status);command.assert_not_called();self.assertEqual([],self.jobs.all())

    def test_worker_uses_powershell7_with_bounded_wait_and_literal_job_path(self):
        job=self.prepared();job.update(state='queued',launch_requested=True);self.jobs.write(job)
        process=Mock(pid=12345);process.wait.return_value=0
        identity={'pid':12345,'created':1,'exe':'pwsh.exe','cmdline':[]}
        with patch.object(self.jobs,'command',side_effect=self.fake_prepare),patch.object(self.jobs,'powershell',return_value='C:/Program Files/PowerShell/7/pwsh.exe'),patch('studio.jobs.subprocess.Popen',return_value=process) as launch,patch('studio.jobs.psutil.Process'),patch.object(self.jobs,'process_identity',return_value=identity),patch.object(self.jobs,'finish') as finish:
            self.jobs.worker(self.pid,job['id'])
        args=launch.call_args.args[0]
        self.assertEqual('C:/Program Files/PowerShell/7/pwsh.exe',args[0]);self.assertIn('-File',args)
        self.assertEqual(str(self.core/'run.ps1'),args[args.index('-File')+1]);self.assertEqual(job['run_dir'],args[args.index('-JobDir')+1])
        self.assertEqual('api',args[args.index('-Mode')+1]);process.wait.assert_called_once_with(timeout=900);finish.assert_called_once_with(self.pid,job['id'])

    def test_prepare_freezes_exact_config_without_staging_or_starting_worker(self):
        self.save({'settings':{**self.block()['settings'],'seed':str(2**64-1)}});p=self.current()
        with patch.object(self.jobs,'command',side_effect=self.fake_prepare) as command,patch.object(self.jobs,'worker') as worker:
            job=self.jobs.prepare(self.pid,self.bid,p['revision'])
        args=command.call_args.args[0];self.assertNotIn('--stage',args);self.assertEqual(1,command.call_count);worker.assert_not_called()
        cfg=read(job['config']);snapshot=read(Path(job['config']).parent/'Block.snapshot.json')
        self.assertEqual(2**64-1,cfg['seed']);self.assertEqual('omni-guided',cfg['profile']);self.assertEqual([{'picture':3,'frame':0}],cfg['guides'])
        self.assertEqual(p['revision'],snapshot['project_revision']);self.assertEqual(self.initial_prompt,snapshot['block']['prompt'])
        self.assertEqual('prepared',job['state']);self.assertFalse(job['launch_requested']);self.assertFalse(Path(job['run_dir']).exists())
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists());self.assertEqual(p,self.current())

    def test_invalid_prompt_prevents_preparation_command_and_job_creation(self):
        self.save({'prompt':'invalid prompt'});p=self.current()
        with patch.object(self.jobs,'command') as command,self.assertRaises(StudioError) as caught:self.jobs.prepare(self.pid,self.bid,p['revision'])
        self.assertEqual(422,caught.exception.status);command.assert_not_called();self.assertEqual([],self.jobs.all())

    def test_launch_is_exclusive_and_second_click_does_not_start_another_thread(self):
        job=self.prepared()
        with patch('studio.jobs.threading.Thread') as thread:
            first=self.jobs.launch(self.pid,job['id']);self.assertEqual('queued',first['state'])
            with self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.return_value.start.assert_called_once()
        self.assertTrue((self.jobs.directory(self.pid,job['id'])/'Launch.intent').is_file())

    def test_second_job_is_refused_while_an_owned_launch_is_active(self):
        first=self.prepared();second=self.prepared()
        with patch('studio.jobs.threading.Thread') as thread:
            self.jobs.launch(self.pid,first['id'])
            with self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,second['id'])
        self.assertEqual(409,caught.exception.status);thread.return_value.start.assert_called_once()
        self.assertFalse((self.jobs.directory(self.pid,second['id'])/'Launch.intent').exists())
        self.assertEqual('prepared',self.jobs.get(self.pid,second['id'])['state'])

    def test_changed_source_refuses_launch_before_intent_or_thread(self):
        job=self.prepared();self.images[0].write_bytes(b'changed after prepare')
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called()
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists())

    def test_changed_settings_refuse_launch_before_intent(self):
        job=self.prepared();self.save({'prompt':self.initial_prompt+'\nchanged action'})
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called();self.assertFalse(self.jobs.get(self.pid,job['id'])['launch_requested'])

    def test_changed_prepared_config_refuses_launch_before_intent(self):
        job=self.prepared();config=Path(job['config']);data=read(config);data['seed']=7;atomic(config,data)
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called()
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists())

    def test_changed_prepared_prompt_refuses_launch_before_intent(self):
        job=self.prepared();prompt=Path(job['config']).parent/'Prompt.txt';prompt.write_text(self.initial_prompt+'\nnew hidden action',encoding='utf-8')
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called()
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists())

    def test_changed_block_snapshot_refuses_launch_before_intent(self):
        job=self.prepared();snapshot=Path(job['config']).parent/'Block.snapshot.json';data=read(snapshot);data['block']['title']='Tampered';atomic(snapshot,data)
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called()
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists())

    def test_changed_runner_implementation_refuses_launch_before_intent(self):
        job=self.prepared();(self.core/'run.ps1').write_text('# different runner',encoding='utf-8')
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called()
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists())

    def test_legacy_job_without_input_manifest_requires_new_preparation(self):
        job=self.prepared();del job['input_files'];self.jobs.write(job)
        with patch('studio.jobs.threading.Thread') as thread,self.assertRaises(StudioError) as caught:self.jobs.launch(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);thread.assert_not_called()
        self.assertFalse((self.jobs.directory(self.pid,job['id'])/'Launch.intent').exists())

    def test_worker_cancelled_before_staging_never_calls_execution_seam(self):
        job=self.prepared();job.update(state='queued',launch_requested=True,cancel_requested=True);self.jobs.write(job)
        with patch.object(self.jobs,'command') as command,patch('studio.jobs.subprocess.Popen') as launch:self.jobs.worker(self.pid,job['id'])
        self.assertEqual('cancelled',self.jobs.get(self.pid,job['id'])['state']);command.assert_not_called();launch.assert_not_called()
        self.assertFalse(Path(job['run_dir']).exists())

    def test_worker_retains_cancellation_when_staging_raises(self):
        job=self.prepared();job.update(state='queued',launch_requested=True);self.jobs.write(job)
        def fail_after_stop(*args,**kwargs):
            self.jobs.stop(self.pid,job['id'])
            raise StudioError('Fixture staging failure',422)
        with patch.object(self.jobs,'matching',return_value=None),patch.object(self.jobs,'command',side_effect=fail_after_stop),patch('studio.jobs.subprocess.Popen') as launch:
            self.jobs.worker(self.pid,job['id'])
        result=self.jobs.get(self.pid,job['id']);self.assertEqual('cancelled',result['state']);self.assertTrue(result['cancel_requested'])
        self.assertEqual('Fixture staging failure',result['error']);launch.assert_not_called();self.assertEqual([],self.block()['takes'])

    def test_staged_graph_drift_fails_before_starting_a_runner(self):
        job=self.prepared();preview=self.jobs.directory(self.pid,job['id'])/'Preview/Prepared.api.json';atomic(preview,{'graph':'reviewed'})
        job.update(state='queued',launch_requested=True,preview_api_sha256=digest(preview));self.jobs.write(job);before=self.current()
        def changed_stage(args,*other):
            run=Path(args[2]);run.mkdir();atomic(run/'Prepared.api.json',{'graph':'unexpected'})
        with patch.object(self.jobs,'command',side_effect=changed_stage),patch('studio.jobs.subprocess.Popen') as launch:self.jobs.worker(self.pid,job['id'])
        result=self.jobs.get(self.pid,job['id']);self.assertEqual('failed',result['state']);self.assertIn('Граф изменился',result['error'])
        launch.assert_not_called();self.assertEqual(before,self.current())

    def test_reused_pid_identity_is_not_considered_owned(self):
        identity={'pid':123,'created':100,'exe':'python.exe','cmdline':['python.exe','runner.py']}
        proc=Mock();proc.create_time.return_value=101;proc.exe.return_value='python.exe';proc.cmdline.return_value=identity['cmdline']
        with patch('studio.jobs.psutil.Process',return_value=proc):self.assertIsNone(Jobs.matching(identity))
        proc.kill.assert_not_called()

    def test_stop_with_unverified_runner_does_not_kill_a_process(self):
        job=self.prepared();job.update(state='running',launch_requested=True,runner={'pid':7,'created':1,'exe':'other.exe','cmdline':[]});self.jobs.write(job)
        with patch.object(self.jobs,'matching',return_value=None),patch('studio.jobs.psutil.Process') as process:
            stopped=self.jobs.stop(self.pid,job['id'])
        process.return_value.kill.assert_not_called();self.assertTrue(stopped['cancel_requested']);self.assertEqual('stopping',stopped['state'])

    def test_stop_kills_only_verified_runner_and_its_verified_children(self):
        job=self.prepared();identity={'pid':1,'created':1,'exe':'runner.exe','cmdline':['runner']};job.update(state='running',launch_requested=True,runner=identity);self.jobs.write(job)
        root=Mock();child=Mock();child.pid=2;child.create_time.return_value=2;child.exe.return_value='child.exe';child.cmdline.return_value=['child'];root.children.return_value=[child]
        def matching(value):return root if value and value['pid']==1 else child if value and value['pid']==2 else None
        with patch.object(self.jobs,'matching',side_effect=matching),patch('studio.jobs.psutil.net_connections') as ports:self.jobs.stop(self.pid,job['id'])
        root.children.assert_called_once_with(recursive=True);child.kill.assert_called_once();root.kill.assert_called_once();ports.assert_not_called()

    def test_native_server_identity_requires_pid_time_python_and_core_script(self):
        job=self.prepared();run=Path(job['run_dir']);run.mkdir();atomic(run/'Watchdog.json',{'server_pid':731,'server_created_epoch':100})
        server=Mock();server.create_time.return_value=100;server.exe.return_value=str(self.jobs.python);server.cmdline.return_value=[str(self.jobs.python),str(self.core/'server.py')]
        with patch('studio.jobs.psutil.Process',return_value=server) as lookup:
            self.assertIs(server,self.jobs.owned_server(job));lookup.assert_called_once_with(731)
            for changed_method,value in [('create_time',101),('exe',str(self.base/'foreign.exe')),('cmdline',[str(self.jobs.python),str(self.base/'foreign/server.py')])]:
                method=getattr(server,changed_method);original=method.return_value;method.return_value=value
                with self.subTest(identity_component=changed_method):self.assertIsNone(self.jobs.owned_server(job))
                method.return_value=original
        server.kill.assert_not_called()

    def test_stop_fallback_kills_owned_native_server_without_port_lookup(self):
        job=self.prepared();job.update(state='running',launch_requested=True);self.jobs.write(job)
        run=Path(job['run_dir']);run.mkdir();atomic(run/'Watchdog.json',{'server_pid':731,'server_created_epoch':100})
        server=Mock();server.create_time.return_value=100;server.exe.return_value=str(self.jobs.python);server.cmdline.return_value=[str(self.jobs.python),str(self.core/'server.py')]
        with patch.object(self.jobs,'matching',return_value=None),patch('studio.jobs.psutil.Process',return_value=server),patch('studio.jobs.psutil.net_connections') as ports:
            result=self.jobs.stop(self.pid,job['id'])
        server.kill.assert_called_once();ports.assert_not_called();self.assertTrue(result['cancel_requested']);self.assertEqual('stopping',result['state'])

    def test_restart_observes_owned_native_server_without_resubmitting(self):
        job=self.prepared();job.update(state='running',launch_requested=True);self.jobs.write(job)
        with patch.object(self.jobs,'matching',return_value=None),patch.object(self.jobs,'owned_server',return_value=Mock()),patch('studio.jobs.threading.Thread') as thread,patch.object(self.jobs,'command') as command,patch('studio.jobs.subprocess.Popen') as launch:
            self.jobs.recover()
        self.assertEqual('running',self.jobs.get(self.pid,job['id'])['state']);thread.assert_called_once_with(target=self.jobs.observe,args=(self.pid,job['id']),daemon=True)
        thread.return_value.start.assert_called_once();command.assert_not_called();launch.assert_not_called()

    def test_restart_marks_unconfirmed_job_interrupted_without_resubmission(self):
        job=self.prepared();job.update(state='queued',launch_requested=True);self.jobs.write(job)
        with patch.object(self.jobs,'matching',return_value=None),patch.object(self.jobs,'command') as command,patch('studio.jobs.subprocess.Popen') as launch,patch('studio.jobs.threading.Thread') as thread:
            self.jobs.recover()
        self.assertEqual('interrupted',self.jobs.get(self.pid,job['id'])['state']);command.assert_not_called();launch.assert_not_called();thread.assert_not_called()

    def test_finish_requires_owned_cleanup_before_collect(self):
        job=self.prepared()
        with patch.object(self.jobs,'command') as command,self.assertRaises(StudioError) as caught:self.jobs.finish(self.pid,job['id'])
        self.assertEqual(409,caught.exception.status);command.assert_not_called();self.assertEqual([],self.block()['takes'])

    def test_successful_collect_is_idempotent_and_does_not_select_take(self):
        job=self.prepared();run=Path(job['run_dir']);run.mkdir();atomic(run/'Cleanup.json',{'stopped':True})
        raw=run/'raw.mp4';raw.write_bytes(b'raw');new=run/'new.mp4';new.write_bytes(b'new')
        image=run/'last.png';image.write_bytes(self.images[0].read_bytes());av=run/'native.safetensors';av.write_bytes(b'native')
        result={'outputs':{k:{'path':str(p)} for k,p in [('92.0',raw),('207.0',new),('202.0',image),('200.0',av)]},
                'videos':{'92':{'frames':56},'207':{'frames':34}},'server_wall_seconds':40.25,'device_gpu_peak_GiB':15.1,'rss_peak_GiB':16.2}
        atomic(run/'Result.json',result)
        with patch.object(self.jobs,'command') as command:
            self.jobs.finish(self.pid,job['id']);first=self.current();self.jobs.finish(self.pid,job['id'])
        self.assertEqual(first,self.current());takes=self.block()['takes'];self.assertEqual(1,len(takes));self.assertEqual('unreviewed',takes[0]['review_state'])
        self.assertIsNone(self.block()['selected_take_id']);self.assertEqual('success',takes[0]['technical_status']);self.assertEqual(34,takes[0]['video_info']['frames'])
        self.assertEqual(new.resolve(),self.store.resolve_asset(self.current(),takes[0]['video_asset_id']));self.assertEqual('completed',self.jobs.get(self.pid,job['id'])['state'])
        self.assertEqual(2,command.call_count);self.assertTrue(all(call.args[0][0]==self.core/'collect.py' for call in command.call_args_list))

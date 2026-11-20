"""Run the opt-in move HTTP acceptance inside an existing registered QA job."""
from pathlib import Path
import hashlib,json,os,re,subprocess,sys,time

TOOLS=Path(__file__).resolve().parent
SOURCE=TOOLS.parents[1]/'src'
sys.path.insert(0,str(TOOLS))
from QAProcesses import Job,matches

def worker(identifier,mode):
    run=SOURCE/'.local/qa-sessions'/identifier
    data=json.loads((run/'session.json').read_text(encoding='utf-8'))
    if data['state']!='running' or not matches(data['owner']):raise RuntimeError('Live owned QA session required')
    if mode=='cpu':return cpu(run)
    return subprocess.call(['dotnet','test','TrueMoon.Tales.Studio.slnx','--no-build','--no-restore','--artifacts-path','.local/studio-build',
        '-c','Debug','--filter','Category=MoveHttpIntegration','--logger','trx','--results-directory',str(run/'move-tests'),
        '-bl:'+str(run/'move-http-{}.binlog')],cwd=SOURCE,env={**os.environ,
        'STUDIO_MOVE_QA_ENDPOINT':data['endpoint']+'/', 'STUDIO_MOVE_QA_RUN':str(run),'DOTNET_CLI_UI_LANGUAGE':'en-US'})

def cpu(run):
    import av
    import numpy as np
    sys.path.insert(0,str(SOURCE.parent/'legacy/web'))
    from studio.model import Store
    from studio.paths import WorkspacePaths
    from studio.exports import Exports
    config=run/'copy/Studio.config.json'
    paths=WorkspacePaths.load(config,code_root=SOURCE.parent)
    store=Store(paths.legacy_root,paths.amv_root,paths.state_root,paths=paths)
    path=store.location('argentis-qa');before=path.read_bytes();project=json.loads(before)
    assert json.loads((run/'move-http.json').read_text())['ok']
    media={}
    source_frames={}
    for block in project['blocks']:
        for take in block['takes']:
            asset=take['video_asset_id'];file=store.resolve_asset(project,asset)
            media[str(file)]=hashlib.sha256(file.read_bytes()).hexdigest()
            with av.open(str(file)) as stream:
                source_frames[(block['id'],take['id'])]=[f.reformat(width=320,height=180,format='rgb24').to_ndarray() for f in stream.decode(video=0)]
    exports=Exports(store)
    try:
        row=exports.create(project['id'],project['revision'],actor='qa:move-cpu')
        deadline=time.monotonic()+55
        while row['state'] in ('queued','running','stopping'):
            if time.monotonic()>deadline:raise TimeoutError('CPU export deadline')
            time.sleep(.1);row=exports.get(project['id'],row['id'])
        assert row['state']=='completed',row
        output=exports.video(project['id'],row['id'])
        with av.open(str(output)) as stream:
            actual=[f.reformat(width=320,height=180,format='rgb24').to_ndarray() for f in stream.decode(video=0)]
        assert len(actual)==24,len(actual)
        errors=[]
        for index,frame in enumerate(actual):
            clip=next((c for c in project['timeline']['clips'] if c['start_frame']<=index<c['start_frame']+c['duration_frames']),None)
            if clip is None:assert frame.max()<6;continue
            source_index=round(clip['source_in_frame']+(index-clip['start_frame'])*clip.get('speed',1))
            expected=source_frames[(clip['block_id'],clip['take_id'])][source_index]
            error=float(np.abs(frame.astype(np.int16)-expected.astype(np.int16)).mean())
            assert error<8,(index,error)
            errors.append({'timeline_frame':index,'source_frame':source_index,'clip_id':clip['id'],'mae':error})
        assert path.read_bytes()==before
        for file,digest in media.items():assert hashlib.sha256(Path(file).read_bytes()).hexdigest()==digest
        report={'ok':True,'revision':project['revision'],'frames':len(actual),'checked_media_frames':len(errors),
            'max_mae':max(e['mae'] for e in errors),'project_unchanged':True,'media_sha256':media,'checks':errors}
        (run/'move-cpu.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k not in ('checks','media_sha256')}))
    finally:exports.close()
    return 0

if __name__=='__main__':
    if len(sys.argv)==4 and sys.argv[1]=='--worker':
        if sys.stdin.buffer.readline()!=b'start\n':sys.exit(2)
        identifier=sys.argv[2]
        if not re.fullmatch(r'[0-9a-f]{32}',identifier):raise ValueError('Expected QA session ID')
        if sys.argv[3] not in ('http','cpu'):raise ValueError('Unknown check')
        sys.exit(worker(identifier,sys.argv[3]))
    if len(sys.argv) not in (2,3) or not re.fullmatch(r'[0-9a-f]{32}',sys.argv[1]):raise ValueError('Pass the registered QA session ID')
    mode='cpu' if len(sys.argv)==3 and sys.argv[2]=='--cpu' else 'http'
    if len(sys.argv)==3 and sys.argv[2]!='--cpu':raise ValueError('Expected --cpu')
    identifier=sys.argv[1]
    job=Job();child=None
    try:
        logpath=SOURCE/'.local/qa-sessions'/identifier/('move-'+mode+'-check.log')
        with logpath.open('wb') as log:
            child=job.start([sys.executable,'-s','-B',str(Path(__file__)),'--worker',identifier,mode],cwd=SOURCE,log=log)
        child.wait(timeout=75)
        print(logpath.read_text(encoding='utf-8',errors='replace'))
        result=child.returncode
    finally:
        job.close()
        if child is not None:child.wait(timeout=5)
    sys.exit(result)

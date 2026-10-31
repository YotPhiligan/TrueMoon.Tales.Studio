"""Derive shared bounded runner from previously executed, instrumented helpers."""
from pathlib import Path
import ast
ROOT=Path(__file__).resolve().parent
EX=ROOT.parents[1]/'Research/Experiments'
assert EX.exists()
for kind,name in [('Server','server.py'),('Validate','validate.py'),('Watchdog','watchdog.py'),('Driver','driver.py')]:
    s=(EX/f'R-03.ST01.{kind}.v1.py').read_text(encoding='utf-8')
    s=s.replace('R-03.ST01.Server.v1.py','server.py').replace('r03-st01-','tmt-omni-v1-')
    if kind=='Server':
        s=s.replace('RESULT = Path(__file__).resolve().parents[1] / "Results/R-03.ST01.OmniFace.Run01"','RESULT = Path(os.environ["TMT_R08_JOB"]).resolve()')
        s=s.replace('"--disable-all-custom-nodes", "--offline"','"--disable-all-custom-nodes", "--whitelist-custom-nodes", "ComfyUI-H3-Motion-Context", "--offline"')
        s=s.replace('CALLBACKS = {','''def install_http(module):
    if state.get('http_guard_installed'):return
    from aiohttp import web
    original = module.PromptServer.__init__
    def init(self, *a, **kw):
        original(self, *a, **kw)
        state['http_guard_attached']=True
        persist()
        @web.middleware
        async def guard(request, handler):
            if request.method != 'POST' or request.path not in ['/prompt','/api/prompt'] or os.environ.get('TMT_R08_MODE') != 'ui':
                return await handler(request)
            payload = await request.json()
            graph = payload.get('prompt', {})
            prepared = json.loads((RESULT/'Prepared.api.json').read_text())
            actual = {k:{'class_type':v['class_type'],'inputs':dict(v['inputs'])} for k,v in graph.items()}
            for k,v in actual.items():
                if v['class_type']=='SaveVideo' and v['inputs'].get('codec')=='auto' and 'codec' not in prepared.get(k,{}).get('inputs',{}):
                    v['inputs'].pop('codec')
            if actual != prepared:
                (RESULT/'UI.ExportMismatch.json').write_text(json.dumps({'actual':actual,'prepared':prepared},indent=2),encoding='utf-8')
                return web.json_response({'error':'UI export differs from prepared job; inspect UI.ExportMismatch.json. Nothing queued.'},status=400)
            claim=RESULT/'UI.claim'
            try:
                with claim.open('x') as f:f.write(str(time.time()))
            except FileExistsError:
                return web.json_response({'error':'One job only; duplicate refused'},status=409)
            (RESULT/'UI.Request.json').write_text(json.dumps(payload,indent=2),encoding='utf-8')
            record={'client_id':payload.get('client_id'),'submitted_epoch':time.time(),'server_pid':os.getpid(),'status':'submission_intent','submissions':1,'route':'ui'}
            (RESULT/'Submission.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
            response=await handler(request)
            body=json.loads(response.body)
            record.update(response=body,status='submitted' if 'prompt_id' in body else 'rejected')
            (RESULT/'Submission.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
            return response
        self.app.middlewares.append(guard)
    module.PromptServer.__init__=init
    state['http_guard_installed']=True
    state['http_module_file']=module.__file__
    persist()

CALLBACKS = {"server": install_http,''')
    elif kind=='Validate':
        s=s.replace('RESULT = Path(__file__).resolve().parents[1] / "Results/R-03.ST01.OmniFace.Run01"','RESULT = Path(os.environ["TMT_R08_JOB"]).resolve()')
        s=s.replace('"--disable-all-custom-nodes", "--offline"','"--disable-all-custom-nodes", "--whitelist-custom-nodes", "ComfyUI-H3-Motion-Context", "--offline"')
        s=s.replace('init_custom_nodes=False','init_custom_nodes=True')
        s=s.replace('async def main():','folder_paths.set_output_directory(str(RESULT / "output"))\n\nasync def main():')
        s=s.replace('import sys, os, json, asyncio','import sys, os, json, asyncio, hashlib')
        guard='''build=json.loads((RESULT/'Build.json').read_text(encoding='utf-8'))
assert build['staged'],'Use prepare.py --stage before running'
for row in build['source_input_hashes']:
    p=Path(row['Target'])
    assert p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==row['SHA256'],'Staged input changed: '+str(p)
for name,key in [('Prepared.api.json','api_sha256'),('Workflow.ui.json','workflow_sha256'),('Prompt.txt','prompt_sha256')]:
    assert hashlib.sha256((RESULT/name).read_bytes()).hexdigest()==build[key],name+' changed after preparation'
'''
        s=s.replace('sys.path.insert(0, str(COMFY))',guard+'\nsys.path.insert(0, str(COMFY))')
    elif kind=='Watchdog':
        s=s.replace("ROOT = Path(__file__).resolve().parents[1] / 'Results/R-03.ST01.OmniFace.Run01'","ROOT = Path(os.environ['TMT_R08_JOB']).resolve()")
    else:
        s=s.replace('import psutil, sys','import psutil, sys, os')
        s=s.replace('RESULT = RESEARCH / "Results/R-03.ST01.OmniFace.Run01"','RESULT = Path(os.environ["TMT_R08_JOB"]).resolve()')
        s=s.replace('workflow = json.loads((SCENE / "Workflows/CL-ST01.OmniFace.v1.json").read_text(encoding="utf-8"))','workflow = json.loads((RESULT / "Workflow.ui.json").read_text(encoding="utf-8"))')
        s=s.replace('(RESULT / "Submission.json").write_text(json.dumps(record, indent=2), encoding="utf-8")\ntry:', 'if os.environ.get("TMT_R08_MODE") != "ui":\n    (RESULT / "Submission.json").write_text(json.dumps(record, indent=2), encoding="utf-8")\ntry:')
        start=s.index('    response = api("/prompt"')
        stop=s.index('    prompt_id = response["prompt_id"]',start)
        old=s[start:stop]
        s=s[:start]+'''    if os.environ.get('TMT_R08_MODE') == 'ui':
        print('AWAITING ONE UI RUN; 300-second UI wait limit', flush=True)
        while True:
            path=RESULT/'Submission.json'
            if path.exists():
                record=json.loads(path.read_text(encoding='utf-8'))
                if 'response' in record:
                    response=record['response'];break
            if time.perf_counter()-started>300:raise TimeoutError('UI did not enqueue within 300s')
            time.sleep(.5)
        started=time.perf_counter()
    else:
'''+''.join('    '+line+'\n' for line in old.rstrip().splitlines())+s[stop:]
    ast.parse(s)
    target=ROOT/name;assert not target.exists(),str(target);target.write_text(s,encoding='utf-8')

s=(EX/'R-03.ST01.Start.ps1').read_text(encoding='utf-8')
s=s.replace("param([ValidateSet('Run01')][string]$Job='Run01')", "param([Parameter(Mandatory=$true)][string]$JobDir,[ValidateSet('api','ui')][string]$Mode='api')\n$Job='Run01'")
s=s.replace("$root='E:\\source\\my\\TOSAB\\Book\\TrueMoonTales\\AMV\\Research\\Results\\R-03.ST01.OmniFace.Run01'", "$root=(Resolve-Path -LiteralPath $JobDir).Path\n$env:TMT_R08_JOB=$root\n$env:TMT_R08_MODE=$Mode")
s=s.replace("$helpers='E:\\source\\my\\TOSAB\\Book\\TrueMoonTales\\AMV\\Research\\Experiments'",'$helpers=$PSScriptRoot')
for kind,name in [('Server','server.py'),('Driver','driver.py'),('Validate','validate.py'),('Watchdog','watchdog.py')]:
    s=s.replace('R-03.ST01.'+kind+'.v1.py',name)
s=s.replace('Ready ST01','Ready Omni.v1')
s=s.replace("if (-not $driver.WaitForExit(510000)) { $driver.Kill(); throw 'Driver exceeded 510 seconds' }", "$driverLimit=if($Mode -eq 'ui'){660000}else{510000}\n    if (-not $driver.WaitForExit($driverLimit)) { $driver.Kill(); throw 'Driver exceeded its bounded wait' }")
# Publish browser workflow only into this disposable diagnostic server's own user directory.
s=s.replace('$launchClock=[Diagnostics.Stopwatch]::StartNew()', '''$workflowDir=Join-Path $root 'user/default/workflows'
New-Item -ItemType Directory -Path $workflowDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Workflow.ui.json') -Destination (Join-Path $workflowDir 'R08.Current.json')
$launchClock=[Diagnostics.Stopwatch]::StartNew()''')
target=ROOT/'run.ps1';assert not target.exists();target.write_text(s,encoding='utf-8')
print('Shared server, CPU validator, one-job driver, independent watchdog and launcher created. No generation.')

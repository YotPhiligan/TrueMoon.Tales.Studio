"""One-shot Omni server: native startup order, in-memory timing hooks only."""
from pathlib import Path
import sys, os, json, time, threading, functools, runpy
import importlib.abc, importlib.machinery

RESULT = Path(os.environ["TMT_R08_JOB"]).resolve()
COMFY = Path(os.environ.get("TMT_COMFY_ROOT","E:/ComfyUI_windows_portable/ComfyUI"))
for name in ("output", "temp", "user"):
    (RESULT / name).mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(COMFY))
os.chdir(COMFY)
sys.argv = [str(COMFY / "main.py"), "--listen", "127.0.0.1", "--port", "8199",
    "--windows-standalone-build", "--use-sage-attention", "--fast", "fp16_accumulation",
    "--disable-auto-launch", "--disable-all-custom-nodes", "--whitelist-custom-nodes", "ComfyUI-H3-Motion-Context", "--offline", "--disable-assets",
    "--input-directory", str(COMFY / "input"),
    "--output-directory", str(RESULT / "output"),
    "--temp-directory", str(RESULT / "temp"),
    "--user-directory", str(RESULT / "user")]
state = {"python": sys.executable, "argv": list(sys.argv), "pid": os.getpid(),
    "nodes": [], "solver": [], "h3_forwards": [], "sage_calls": 0,
    "sage_success": 0, "sage_errors": [], "sdpa_calls": 0, "sage_shapes": {},
    "peak_reset": False, "timing_scope": "instrumented single cold job; CUDA sync at solver/forward boundaries"}
local = threading.local()

def persist():
    target = RESULT / "Runtime.json"
    temp = target.with_suffix(".tmp")
    temp.write_text(json.dumps(state, indent=2), encoding="utf-8")
    temp.replace(target)

def memory():
    import torch
    state["torch_cuda_peak_allocated_bytes"] = torch.cuda.max_memory_allocated()
    state["torch_cuda_peak_reserved_bytes"] = torch.cuda.max_memory_reserved()

def install_attention(module):
    import torch
    state["default_attention"] = module.optimized_attention.__name__
    state["sage_available"] = module.SAGE_ATTENTION_IS_AVAILABLE
    state["torch"] = torch.__version__
    original = module.sageattn
    @functools.wraps(original)
    def sage(q, k, v, *a, **kw):
        active = getattr(local, "h3", False)
        if active:
            state["sage_calls"] += 1
            shape = str(tuple(q.shape)) + " " + str(q.dtype)
            state["sage_shapes"][shape] = state["sage_shapes"].get(shape, 0) + 1
        try:
            output = original(q, k, v, *a, **kw)
        except Exception as e:
            if active:
                state["sage_errors"].append(type(e).__name__ + ": " + str(e))
                persist()
            raise
        if active:
            state["sage_success"] += 1
        return output
    module.sageattn = sage
    old_sdpa = torch.nn.functional.scaled_dot_product_attention
    @functools.wraps(old_sdpa)
    def sdpa(*a, **kw):
        if getattr(local, "h3", False):
            state["sdpa_calls"] += 1
        return old_sdpa(*a, **kw)
    torch.nn.functional.scaled_dot_product_attention = sdpa
    persist()

def install_h3(module):
    old = module.MiniMaxH3Model.forward
    @functools.wraps(old)
    def forward(self, *a, **kw):
        import torch
        local.h3 = True
        row = {"index": len(state["h3_forwards"]) + 1, "started_epoch": time.time()}
        state["h3_forwards"].append(row)
        persist()
        start = time.perf_counter()
        try:
            result = old(self, *a, **kw)
            torch.cuda.synchronize()
            row["seconds"] = time.perf_counter() - start
            row["output_finite"] = [bool(torch.isfinite(x).all()) for x in result]
            row["output_shapes"] = [list(x.shape) for x in result]
            return result
        except Exception as e:
            row["error"] = type(e).__name__ + ": " + str(e)
            raise
        finally:
            local.h3 = False
            memory()
            persist()
    module.MiniMaxH3Model.forward = forward

def install_sampler(module):
    old = module.KSAMPLER.sample
    @functools.wraps(old)
    def sample(self, *a, **kw):
        import torch
        torch.cuda.synchronize()
        row = {"started_epoch": time.time(), "sampler": self.sampler_function.__name__}
        state["solver"].append(row)
        persist()
        start = time.perf_counter()
        try:
            return old(self, *a, **kw)
        finally:
            torch.cuda.synchronize()
            row["seconds"] = time.perf_counter() - start
            row["ended_epoch"] = time.time()
            memory()
            persist()
    module.KSAMPLER.sample = sample

def install_execution(module):
    old = module.get_output_data
    @functools.wraps(old)
    async def output(prompt_id, unique_id, obj, *a, **kw):
        import torch
        if not state["peak_reset"]:
            torch.cuda.reset_peak_memory_stats()
            state["peak_reset"] = True
        row = {"id": str(unique_id), "class": type(obj).__name__, "started_epoch": time.time()}
        state["nodes"].append(row)
        persist()
        start = time.perf_counter()
        try:
            return await old(prompt_id, unique_id, obj, *a, **kw)
        except Exception as e:
            row["error"] = type(e).__name__ + ": " + str(e)
            raise
        finally:
            row["seconds"] = time.perf_counter() - start
            row["ended_epoch"] = time.time()
            memory()
            persist()
    module.get_output_data = output

def install_http(module):
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

CALLBACKS = {"server": install_http,"comfy.ldm.modules.attention": install_attention,
    "comfy.ldm.minimax.model": install_h3, "comfy.samplers": install_sampler,
    "execution": install_execution}

class Loader(importlib.abc.Loader):
    def __init__(self, original, callback):
        self.original, self.callback = original, callback
    def create_module(self, spec):
        return self.original.create_module(spec)
    def exec_module(self, module):
        self.original.exec_module(module)
        self.callback(module)

class Finder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname not in CALLBACKS:
            return None
        spec = importlib.machinery.PathFinder.find_spec(fullname, path)
        if spec:
            spec.loader = Loader(spec.loader, CALLBACKS[fullname])
        return spec

sys.meta_path.insert(0, Finder())
persist()
runpy.run_path(str(COMFY / "main.py"), run_name="__main__")

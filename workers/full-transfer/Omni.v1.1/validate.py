"""Use native Comfy validation on CPU only; never enqueue or execute a graph."""
from pathlib import Path
import sys, os, json, asyncio, hashlib

COMFY = Path(os.environ.get("TMT_COMFY_ROOT","E:/ComfyUI_windows_portable/ComfyUI"))
RESULT = Path(os.environ["TMT_R08_JOB"]).resolve()
build=json.loads((RESULT/'Build.json').read_text(encoding='utf-8'))
assert build['staged'],'Use prepare.py --stage before running'
for row in build['source_input_hashes']:
    p=Path(row['Target'])
    assert p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==row['SHA256'],'Staged input changed: '+str(p)
for name,key in [('Prepared.api.json','api_sha256'),('Workflow.ui.json','workflow_sha256'),('Prompt.txt','prompt_sha256')]:
    assert hashlib.sha256((RESULT/name).read_bytes()).hexdigest()==build[key],name+' changed after preparation'
sys.path.insert(0, str(COMFY))
os.chdir(COMFY)
sys.argv = ["r02-validate", "--cpu", "--disable-all-custom-nodes", "--whitelist-custom-nodes", "ComfyUI-H3-Motion-Context", "--offline", "--disable-assets"]
import comfy.options
comfy.options.enable_args_parsing()
import nodes, execution, folder_paths

folder_paths.set_output_directory(str(RESULT / "output"))

async def main():
    await nodes.init_extra_nodes(init_custom_nodes=True, init_api_nodes=False)
    graph = json.loads((RESULT / "Prepared.api.json").read_text(encoding="utf-8"))
    validation = await execution.validate_prompt("r02-schema-only", graph, None)
    report = {"scope": "Native API graph validation, CPU only; no execution or queue submission",
              "valid": bool(validation[0]), "validation": validation,
              "registered_node_types": sorted({n["class_type"] for n in graph.values()}),
              "generation_executed": False}
    (RESULT / "Validation.json").write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")
    print(json.dumps(report, indent=2, default=str), flush=True)
    if not report["valid"]:
        raise SystemExit(1)

asyncio.run(main())

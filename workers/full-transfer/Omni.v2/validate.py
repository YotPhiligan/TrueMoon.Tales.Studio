"""Native schema validation on CPU; no execution, queue or model loading."""
import asyncio
import os
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import COMFY, read, verify_job, write

JOB = Path(os.environ['TMT_OMNI2_JOB']).resolve()
verify_job(JOB)
sys.path.insert(0, str(COMFY))
os.chdir(COMFY)
sys.argv = ['omni-v2-validate', '--cpu', '--disable-all-custom-nodes', '--offline', '--disable-assets']
import comfy.options
comfy.options.enable_args_parsing()
import nodes
import execution
import folder_paths
folder_paths.set_output_directory(str(JOB / 'output'))


async def main():
    await nodes.init_extra_nodes(init_custom_nodes=False, init_api_nodes=False)
    validation = await execution.validate_prompt('omni-v2-schema-only', read(JOB / 'Prepared.api.json'), None)
    record = {'scope': 'Native CPU schema and job hashes only; no graph execution',
              'valid': bool(validation[0]), 'validation': validation, 'generation_executed': False}
    write(JOB / 'Validation.json', record)
    print(record, flush=True)
    if not record['valid']:
        raise SystemExit(1)


asyncio.run(main())

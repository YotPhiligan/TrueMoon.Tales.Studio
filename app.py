"""Run a local production desk; ComfyUI starts only through explicit launch API."""
from pathlib import Path
import argparse,json,os,sys,threading,webbrowser
from http.server import BaseHTTPRequestHandler
ROOT=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT))
from studio.model import Store
from studio.paths import WorkspacePaths
from studio.jobs import Jobs
from studio.server import LocalServer,handler_for
from studio.exports import Exports

def main():
    p=argparse.ArgumentParser();p.add_argument('--port',type=int,default=8787)
    p.add_argument('--config',type=Path,default=Path(os.environ.get('TRUE_MOON_STUDIO_CONFIG',ROOT/'Studio.config.local.json')))
    p.add_argument('--adopt-registry',type=Path,help='Validate registry without rewriting projects')
    p.add_argument('--sync-imports',action='store_true');p.add_argument('--no-import',action='store_true',help=argparse.SUPPRESS)
    p.add_argument('--check',action='store_true');p.add_argument('--open-browser',action='store_true');a=p.parse_args()
    paths=WorkspacePaths.load(a.config,code_root=ROOT);store=Store(paths.legacy_root,paths.amv_root,paths.state_root,paths=paths)
    if a.adopt_registry:store.adopt_registry(a.adopt_registry)
    if a.sync_imports and not a.no_import:store.seed();store.reconcile_imports()
    projects=store.list_projects()
    if a.check:
        print(json.dumps({'ok':True,'config':str(a.config.resolve()),'projects':projects,'generation_started':False},ensure_ascii=False,indent=2));return
    server=LocalServer(('127.0.0.1',a.port),BaseHTTPRequestHandler)
    exports=None
    try:
        jobs=Jobs(store);jobs.recover()
        exports=Exports(store);exports.recover()
        server.RequestHandlerClass=handler_for(store,jobs,ROOT/'static',exports)
        print(f'True Moon Studio 0.15\nLocal: http://127.0.0.1:{server.server_address[1]}\nConfig: {a.config.resolve()}\nGeneration starts only from explicit launch.',flush=True)
        if a.open_browser:
            opener=threading.Timer(.4,lambda:webbrowser.open(f'http://127.0.0.1:{server.server_address[1]}'));opener.daemon=True;opener.start()
        server.serve_forever(poll_interval=.3)
    except KeyboardInterrupt:pass
    finally:
        if exports:exports.close()
        server.server_close()

if __name__=='__main__':main()

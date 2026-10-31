"""Bounded read-only check before owning 8199. Never stop an existing process."""
from pathlib import Path
import json,os,socket,sys,time
import psutil

def main():
    root=Path(os.environ['TMT_R08_JOB']).resolve()
    assert not (root/'Submission.json').exists(),'Preserve submitted job'
    start=time.monotonic();samples=[];status='waiting'
    try:
        while True:
            table_error=None
            try:listeners=[x for x in psutil.net_connections(kind='tcp') if x.status=='LISTEN' and x.laddr.port==8199]
            except RuntimeError as error:listeners=[];table_error=str(error)
            active=False
            try:
                with socket.create_connection(('127.0.0.1',8199),timeout=0.25):active=True
            except OSError:pass
            bind_error=None
            with socket.socket() as probe:
                if hasattr(socket,'SO_EXCLUSIVEADDRUSE'):probe.setsockopt(socket.SOL_SOCKET,socket.SO_EXCLUSIVEADDRUSE,1)
                try:probe.bind(('127.0.0.1',8199))
                except OSError as error:bind_error=str(error)
            samples.append({'seconds':time.monotonic()-start,'listeners':[{'pid':x.pid,'address':str(x.laddr)} for x in listeners],
                            'table_error':table_error,'tcp_connect_succeeded':active,'exclusive_bind_error':bind_error})
            if active:
                status='preserved_live_listener';raise RuntimeError('Preserve responding listener on 8199')
            for row in listeners:
                if row.pid not in [None,0] and psutil.pid_exists(row.pid):
                    status='preserved_live_listener';raise RuntimeError(f'Preserve existing listener on 8199, PID={row.pid}')
            if bind_error is None:status='ready';return
            if time.monotonic()-start>=45:
                status='teardown_timeout';raise TimeoutError('8199 teardown exceeded 45 seconds; no server started')
            time.sleep(1)
    finally:
        (root/'Port.prelaunch.json').write_text(json.dumps({'status':status,'elapsed_seconds':time.monotonic()-start,
            'max_wait_seconds':45,'samples':samples,'processes_stopped':False,'submitted':False},indent=2),encoding='utf-8')

if __name__=='__main__':main()

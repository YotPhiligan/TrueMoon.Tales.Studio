"""Run one frozen Studio Full Transfer job for the lifetime of the owner pipe."""
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.request

ROOT = Path(__file__).resolve().parent


def isolate_worker():
    if os.name != 'nt':
        os.setsid()
        return lambda: os.killpg(os.getpgrp(), signal.SIGKILL)

    import ctypes as ct
    from ctypes import wintypes as wt

    kernel = ct.WinDLL('kernel32', use_last_error=True)

    def api(name, result, *arguments):
        function = getattr(kernel, name)
        function.restype, function.argtypes = result, arguments
        return function

    class BasicLimits(ct.Structure):
        _fields_ = [('process_time', ct.c_int64), ('job_time', ct.c_int64), ('flags', wt.DWORD),
                    ('min_set', ct.c_size_t), ('max_set', ct.c_size_t), ('process_limit', wt.DWORD),
                    ('affinity', ct.c_size_t), ('priority', wt.DWORD), ('scheduling', wt.DWORD)]

    class ExtendedLimits(ct.Structure):
        _fields_ = [('basic', BasicLimits), ('io', ct.c_uint64 * 6), ('memory', ct.c_size_t * 4)]

    create = api('CreateJobObjectW', wt.HANDLE, ct.c_void_p, wt.LPCWSTR)
    configure = api('SetInformationJobObject', wt.BOOL, wt.HANDLE, ct.c_int, ct.c_void_p, wt.DWORD)
    assign = api('AssignProcessToJobObject', wt.BOOL, wt.HANDLE, wt.HANDLE)
    current = api('GetCurrentProcess', wt.HANDLE)
    close = api('CloseHandle', wt.BOOL, wt.HANDLE)
    job = create(None, None)
    if not job:
        raise ct.WinError(ct.get_last_error())
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # Kill the worker and all descendants when its job handle closes.
    if not configure(job, 9, ct.byref(limits), ct.sizeof(limits)) or not assign(job, current()):
        error = ct.WinError(ct.get_last_error())
        close(job)
        raise error
    return lambda: os._exit(2)


def lock_worker(file):
    if os.name == 'nt':
        import msvcrt
        msvcrt.locking(file.fileno(), msvcrt.LK_NBLCK, 1)
    else:
        import fcntl
        fcntl.lockf(file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB, 1)


def run(job, comfy, expected_build, metadata):
    stop = isolate_worker()

    def watch_owner():
        os.read(sys.stdin.fileno(), 1)
        stop()

    threading.Thread(target=watch_owner, daemon=True).start()
    os.environ['TMT_COMFY_ROOT'] = str(comfy)
    sys.path.insert(0, str(ROOT))
    from common import atomic_write, read, require, sha, verify_job
    import psutil

    launch = read(job / 'Launch.json')
    require(launch['build_sha256'].lower() == expected_build.lower() == sha(job / 'Build.json'),
            'Frozen build SHA changed')
    require(launch['runner_sha256'].lower() == sha(Path(__file__)), 'Runner SHA changed')
    require(launch['request_sha256'].lower() == sha(metadata / 'Request.json'), 'Request SHA changed')
    require(launch['staged_sha256'].lower() == sha(metadata / 'Staged.json'), 'Staged receipt SHA changed')
    build = read(job / 'Build.json')
    require(build['profile'] == 'full-transfer' and build['staged'] and not build['generation_executed'],
            'Expected a staged, unused Full Transfer graph')
    for name in build['implementation_hashes']:
        require(Path(name).name == name and (ROOT / name).resolve().parent == ROOT, 'Unexpected helper path')
    models = (comfy / 'models').resolve()
    require(len(build['model_files']) == 4 and all(
        Path(row['path']).resolve().is_relative_to(models) for row in build['model_files']), 'Unexpected model paths')
    require((comfy / 'main.py').is_file(), 'ComfyUI installation is incomplete')

    owner = secrets.token_hex(32)
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as candidate:
        candidate.bind(('127.0.0.1', 0))
        port = candidate.getsockname()[1]
    env = dict(os.environ, TMT_OMNI2_JOB=str(job), TMT_OMNI2_MODE='api', TMT_OMNI2_OWNER=owner,
               TMT_STUDIO_PYTHON=sys.executable, TMT_OMNI2_PORT=str(port), NO_PROXY='*')
    deadline = time.monotonic() + 720
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def start(name, *args, cpu=False):
        child_env = dict(env, CUDA_VISIBLE_DEVICES='') if cpu else env
        with (job / (name + '.stdout.log')).open('xb') as out, (job / (name + '.stderr.log')).open('xb') as err:
            return subprocess.Popen([sys.executable, '-I', '-B', str(ROOT / (name + '.py')), *args],
                                    cwd=comfy, env=child_env, stdin=subprocess.DEVNULL, stdout=out, stderr=err)

    def reap(proc):
        if proc.poll() is None:
            proc.kill()
        proc.wait(timeout=5)

    def bounded(name, seconds, *args, cpu=False):
        child = start(name, *args, cpu=cpu)
        try:
            require(child.wait(timeout=max(0.01, min(seconds, deadline - time.monotonic()))) == 0,
                    name + ' failed; inspect its logs')
        finally:
            reap(child)

    def listening(rows):
        return any(row.status == psutil.CONN_LISTEN and row.laddr.ip == '127.0.0.1' and row.laddr.port == port for row in rows)

    def wait_ready(server):
        startup = min(deadline, time.monotonic() + 90)
        owned = psutil.Process(server.pid)
        while time.monotonic() < startup:
            require(server.poll() is None, 'Owned server exited during startup')
            if listening(owned.net_connections(kind='tcp')):
                try:
                    request = urllib.request.Request(f'http://127.0.0.1:{port}/system_stats',
                                                     headers={'X-Studio-Owner': owner})
                    with opener.open(request, timeout=2) as response:
                        require(response.status == 200, 'Owned server refused readiness')
                    return
                except OSError:
                    pass
            time.sleep(0.25)
        raise TimeoutError('Owned server startup exceeded 90 seconds')

    def wait_driver(driver):
        submitted = None
        while driver.poll() is None:
            now = time.monotonic()
            if submitted is None and (job / 'Submission.claim').exists():
                submitted = now
            heartbeat = job / 'Runtime.json'
            age = 0 if submitted is None else now - submitted
            idle = time.time() - heartbeat.stat().st_mtime if heartbeat.exists() else age
            if now >= deadline or age > 490 or min(age, idle) > 180:
                raise TimeoutError('Generation deadline or no progress; inspect worker logs')
            time.sleep(0.25)
        require(driver.returncode == 0, 'Submission or generation failed; inspect driver logs')

    verify_job(job)
    bounded('validate', 90, cpu=True)
    server = start('server')
    try:
        (job / 'server.pid').write_text(str(server.pid), encoding='ascii')
        wait_ready(server)
        driver = start('driver')
        try:
            wait_driver(driver)
        finally:
            reap(driver)
    finally:
        reap(server)
        atomic_write(job / 'Cleanup.json', {'pid': server.pid, 'stopped': True,
                                           'scope': 'Studio-owned server process'})
    bounded('collect', 90, str(job), cpu=True)
    atomic_write(job / 'Completed.json', {'build_sha256': expected_build,
                                         'result_sha256': sha(job / 'Result.json')})


if __name__ == '__main__':
    job = Path(sys.argv[1]).resolve()
    try:
        with (job / 'Worker.lock').open('a+b') as lease:
            lock_worker(lease)
            os.environ['TMT_OMNI2_INPUT'] = str(job / 'input')
            run(job, Path(sys.argv[2]).resolve(), sys.argv[3], Path(sys.argv[4]).resolve())
    except Exception as error:
        import traceback
        with (job / 'Runner.failure.log').open('x', encoding='utf-8') as log:
            traceback.print_exc(file=log)
        raise SystemExit(3 if isinstance(error, (TimeoutError, subprocess.TimeoutExpired)) else 1)

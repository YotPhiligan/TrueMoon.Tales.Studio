"""Independent deadline for one owned MC01 server; survives a disconnected chat."""
from pathlib import Path
import json, os, subprocess, sys, time, urllib.request
import psutil

PYTHON = Path(os.environ.get('TMT_STUDIO_PYTHON',sys.executable)).resolve()
SERVER = str(Path(__file__).with_name('server.py')).lower()
ROOT = Path(os.environ['TMT_R08_JOB']).resolve()


def owned(pid, created, script=SERVER):
    try:
        p = psutil.Process(pid)
        return (p if abs(p.create_time() - created) < 0.01
                and Path(p.exe()).resolve() == PYTHON
                and script in ' '.join(p.cmdline()).lower() else None)
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return None


def deadline_reason(elapsed, submitted_age=None, heartbeat_age=None):
    if elapsed >= 720:
        return 'total_launch_deadline_720s'
    if submitted_age is not None and submitted_age >= 480:
        return 'generation_deadline_480s'
    if heartbeat_age is not None and heartbeat_age >= 180:
        return 'no_runtime_progress_180s'
    return None


def api(path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request('http://127.0.0.1:8199' + path, data=data,
                                 headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=3) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def write(path, record):
    tmp = path.with_suffix('.tmp')
    tmp.write_text(json.dumps(record, indent=2), encoding='utf-8')
    tmp.replace(path)


def main(job, pid, remaining):
    assert job == 'Run01' and 0 < remaining <= 720
    result = ROOT
    p = psutil.Process(pid)
    created = p.create_time()
    assert owned(pid, created), 'Refuse to control a process not owned by this test'
    started = time.monotonic()
    record = {'job': job, 'watchdog_pid': os.getpid(), 'server_pid': pid,
              'server_created_epoch': created, 'started_epoch': time.time(),
              'limits_seconds': {'total_launch': 720, 'generation': 480, 'no_progress': 180},
              'remaining_total_seconds_when_armed': remaining,
              'status': 'watching', 'automatic_resubmission': False}
    path = result / 'Watchdog.json'
    write(path, record)
    submitted_at = None
    while owned(pid, created):
        sub = result / 'Submission.json'
        if submitted_at is None and sub.is_file():
            submitted_at = time.monotonic()
        age = None if submitted_at is None else time.monotonic() - submitted_at
        heartbeat = result / 'Runtime.json'
        heartbeat_age = None
        if submitted_at is not None:
            heartbeat_age = max(0, time.time() - heartbeat.stat().st_mtime) if heartbeat.exists() else age
        try:
            status = json.loads(sub.read_text(encoding='utf-8')).get('status', {})
            if isinstance(status, dict) and status.get('completed'):
                age = heartbeat_age = None
        except (OSError, ValueError):
            pass
        reason = deadline_reason(720 - remaining + time.monotonic() - started, age, heartbeat_age)
        if reason:
            record.update(status='timeout', reason=reason, timeout_epoch=time.time())
            for name, route, body in [('queue_at_timeout', '/queue', None), ('interrupt', '/interrupt', {})]:
                try:
                    record[name] = api(route, body)
                except Exception as e:
                    record[name] = type(e).__name__ + ': ' + str(e)
            write(path, record)
            time.sleep(5)
            target = owned(pid, created)
            if target:
                target.terminate()
                try:
                    target.wait(timeout=5)
                except psutil.TimeoutExpired:
                    target.kill()
                    target.wait(timeout=5)
                record['owned_server_terminated'] = True
            break
        time.sleep(1)
    if record['status'] == 'watching':
        record['status'] = 'server_exited'
    record['ended_epoch'] = time.time()
    write(path, record)


def self_test():
    assert deadline_reason(719, 479, 179) is None
    assert deadline_reason(720) == 'total_launch_deadline_720s'
    assert deadline_reason(100, 480) == 'generation_deadline_480s'
    assert deadline_reason(100, 20, 180) == 'no_runtime_progress_180s'
    probe = subprocess.Popen([str(PYTHON), '-s', '-B', str(Path(__file__).resolve()), '--probe'],
                             creationflags=0x08000000)
    try:
        p = psutil.Process(probe.pid)
        created = p.create_time()
        script = str(Path(__file__).resolve()).lower() + ' --probe'
        assert owned(probe.pid, created + 10, script) is None
        assert owned(probe.pid, created, SERVER) is None
        target = owned(probe.pid, created, script)
        assert target is not None
        target.terminate()
        target.wait(timeout=5)
    finally:
        if probe.poll() is None:
            probe.kill()
        probe.wait(timeout=5)
    print(json.dumps({'deadline_boundaries': 'pass', 'pid_reuse_guard': 'pass',
                      'wrong_command_guard': 'pass', 'owned_probe_termination': 'pass',
                      'gpu_generation': False}))


if __name__ == '__main__':
    if sys.argv[1:] == ['--probe']:
        time.sleep(30)
    elif sys.argv[1:] == ['--self-test']:
        self_test()
    else:
        main(sys.argv[1], int(sys.argv[2]), float(sys.argv[3]))

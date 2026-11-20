"""Run a frozen CPU request while its backend connection remains open."""
import hashlib
import os
from pathlib import Path
import signal
import sys
import threading


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


def run(folder, expected_sha):
    scope = folder.parent.parent
    with (scope / 'WorkerActive.lock').open('a+b') as active, (folder / 'Worker.lock').open('a+b') as lock:
        lock_worker(active)
        lock_worker(lock)
        stop = isolate_worker()
        # Load native extensions before the lifetime watcher makes its blocking pipe read.
        import av
        import imageio_ffmpeg
        import numpy
        from PIL import Image

        sys.stdout.buffer.write(b'READY\n')
        sys.stdout.buffer.flush()
        input_fd = sys.stdin.fileno()
        if os.read(input_fd, 1) != b'S':
            return

        def watch_owner():
            os.read(input_fd, 1)
            stop()

        threading.Thread(target=watch_owner, daemon=True).start()
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from studio.export_worker import DraftRenderer
        from studio.model import atomic

        request = folder / 'Request.json'
        with request.open('rb') as source:
            data = source.read(1024 * 1024 + 1)
        if len(data) > 1024 * 1024 or hashlib.sha256(data).hexdigest() != expected_sha.lower():
            raise ValueError('CPU export request SHA mismatch.')
        renderer = DraftRenderer(folder, request_data=data)
        if renderer.request.get('backend_mode') is not True:
            raise ValueError('Expected a backend export request.')
        result = renderer.run()
        atomic(folder / 'Result.json', {**result, 'request_sha256': expected_sha})


if __name__ == '__main__':
    run(Path(sys.argv[1]).resolve(), sys.argv[2])

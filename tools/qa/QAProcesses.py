"""Windows-only ownership primitives for disposable QA processes (standard library)."""

import ctypes as ct
from ctypes import wintypes as wt
import os
import subprocess
import time

if os.name != "nt":
    raise RuntimeError("Studio native QA requires Windows")

kernel = ct.WinDLL("kernel32", use_last_error=True)


def api(name, result, *args):
    function = getattr(kernel, name)
    function.restype, function.argtypes = result, args
    return function


close_handle = api("CloseHandle", wt.BOOL, wt.HANDLE)
open_process = api("OpenProcess", wt.HANDLE, wt.DWORD, wt.BOOL, wt.DWORD)
process_times = api("GetProcessTimes", wt.BOOL, wt.HANDLE, *([ct.POINTER(wt.FILETIME)] * 4))
process_image = api("QueryFullProcessImageNameW", wt.BOOL, wt.HANDLE, wt.DWORD, wt.LPWSTR, ct.POINTER(wt.DWORD))
wait_handle = api("WaitForSingleObject", wt.DWORD, wt.HANDLE, wt.DWORD)
terminate_process = api("TerminateProcess", wt.BOOL, wt.HANDLE, wt.UINT)
create_job = api("CreateJobObjectW", wt.HANDLE, ct.c_void_p, wt.LPCWSTR)
set_job = api("SetInformationJobObject", wt.BOOL, wt.HANDLE, ct.c_int, ct.c_void_p, wt.DWORD)
assign_job = api("AssignProcessToJobObject", wt.BOOL, wt.HANDLE, wt.HANDLE)
terminate_job = api("TerminateJobObject", wt.BOOL, wt.HANDLE, wt.UINT)
query_job = api("QueryInformationJobObject", wt.BOOL, wt.HANDLE, ct.c_int, ct.c_void_p, wt.DWORD, ct.c_void_p)


def checked(value):
    if not value:
        raise ct.WinError(ct.get_last_error())
    return value


class BasicLimits(ct.Structure):
    _fields_ = [("process_time", ct.c_int64), ("job_time", ct.c_int64), ("flags", wt.DWORD),
                ("min_working_set", ct.c_size_t), ("max_working_set", ct.c_size_t),
                ("process_limit", wt.DWORD), ("affinity", ct.c_size_t),
                ("priority", wt.DWORD), ("scheduling", wt.DWORD)]


class ExtendedLimits(ct.Structure):
    _fields_ = [("basic", BasicLimits), ("io", ct.c_uint64 * 6),
                ("process_memory", ct.c_size_t), ("job_memory", ct.c_size_t),
                ("peak_process_memory", ct.c_size_t), ("peak_job_memory", ct.c_size_t)]


class Accounting(ct.Structure):
    _fields_ = [("times", ct.c_int64 * 4), ("page_faults", wt.DWORD),
                ("total", wt.DWORD), ("active", wt.DWORD), ("terminated", wt.DWORD)]


def identity_from_handle(handle, pid):
    times = [wt.FILETIME() for _ in range(4)]
    checked(process_times(handle, *(ct.byref(value) for value in times)))
    size = wt.DWORD(32768)
    image = ct.create_unicode_buffer(size.value)
    checked(process_image(handle, 0, image, ct.byref(size)))
    return {"pid": pid, "created": (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime,
            "exe": os.path.normcase(image.value)}


def identity(pid):
    handle = checked(open_process(0x1000 | 0x100000, False, pid))
    try:
        if wait_handle(handle, 0) == 0:
            raise ProcessLookupError(pid)
        return identity_from_handle(handle, pid)
    finally:
        close_handle(handle)


def matches(record, *, terminate=False):
    """Check and optionally stop via the SAME handle, never a bare PID lookup."""
    handle = open_process(0x1000 | 0x100000 | (1 if terminate else 0), False, record["pid"])
    if not handle:
        error = ct.get_last_error()
        if error == 87:  # Process no longer exists.
            return False
        raise ct.WinError(error)
    try:
        if wait_handle(handle, 0) == 0:
            return False
        try:
            current = identity_from_handle(handle, record["pid"])
        except OSError:
            # Image lookup can fail while a job is terminating this process.
            # Do not treat access denied on a LIVE process as proof of exit.
            if wait_handle(handle, 100) == 0:
                return False
            raise
        if current != record:
            return False
        if terminate:
            checked(terminate_process(handle, 1))
            if wait_handle(handle, 5000) != 0:
                raise TimeoutError("Verified QA supervisor did not exit")
        return True
    finally:
        close_handle(handle)


class Job:
    """Non-inheritable job: owner exit also closes it and kills every descendant.

    Child workers wait on stdin before doing any work. If assignment fails or the
    owner dies before assignment, EOF makes the unassigned worker exit harmlessly.
    """

    def __init__(self):
        self.handle = checked(create_job(None, None))
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway.
        try:
            checked(set_job(self.handle, 9, ct.byref(limits), ct.sizeof(limits)))
        except BaseException:
            close_handle(self.handle)
            self.handle = None
            raise

    def start(self, command, *, cwd, log):
        process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE, stdout=log,
                                   stderr=subprocess.STDOUT, close_fds=True,
                                   creationflags=subprocess.CREATE_NO_WINDOW,
                                   env={**os.environ, "PYTHONIOENCODING": "utf-8"})
        try:
            handle = checked(open_process(0x100 | 1, False, process.pid))
            try:
                checked(assign_job(self.handle, handle))
            finally:
                close_handle(handle)
            process.stdin.write(b"start\n")
            process.stdin.flush()
            return process
        except BaseException:
            process.kill()
            process.wait(timeout=5)
            raise
        finally:
            process.stdin.close()

    def close(self):
        if self.handle is None:
            return
        try:
            checked(terminate_job(self.handle, 1))
            deadline = time.monotonic() + 5
            while True:
                accounting = Accounting()
                checked(query_job(self.handle, 1, ct.byref(accounting), ct.sizeof(accounting), None))
                if accounting.active == 0:
                    return
                if time.monotonic() >= deadline:
                    raise TimeoutError("QA job still has active processes")
                time.sleep(.05)
        finally:
            close_handle(self.handle)
            self.handle = None

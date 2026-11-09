"""Local Windows lifecycle checks; only disposable CPU workers and QA data."""

import json
import io
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import QAProcesses
import QASession
from QAProcesses import Job, identity, matches

PYTHON = [sys.executable, "-s", "-B"]


def wait_for(predicate, seconds=10):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(.05)
    raise TimeoutError("Lifecycle check did not reach the expected state")


class QASessionTests(unittest.TestCase):
    def setUp(self):
        local = QASession.SOURCE / ".local"
        local.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="qa-lifecycle-tests-", dir=local)
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.log = self.enterContext((self.base / "test.log").open("wb"))

    def owned_process(self, command, **kwargs):
        process = subprocess.Popen(command, stdout=self.log, stderr=subprocess.STDOUT,
                                   creationflags=subprocess.CREATE_NO_WINDOW, **kwargs)

        def cleanup():
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)

        self.addCleanup(cleanup)
        return process

    def tree_command(self, marker):
        code = ("import json,os,pathlib,subprocess,sys,time\n"
                "if sys.stdin.buffer.readline() != b'start\\n': sys.exit(2)\n"
                "child=subprocess.Popen([sys.executable,'-s','-B','-c','import time; time.sleep(90)'])\n"
                f"pathlib.Path({str(marker)!r}).write_text(json.dumps([os.getpid(),child.pid]))\n"
                "time.sleep(90)\n")
        return PYTHON + ["-c", code]

    def tree_records(self, marker):
        wait_for(lambda: marker.exists() and marker.stat().st_size)
        return [identity(pid) for pid in json.loads(marker.read_text())]

    def assert_exited(self, records):
        for record in records:
            wait_for(lambda: not matches(record), seconds=5)
            self.assertFalse(matches(record), f"Still alive: {record}")

    def test_worker_records_full_native_exit_status(self):
        manifest = self.base / "session.json"
        manifest.write_text(json.dumps({"commands": {"ui": ["owned-studio.exe"]}}))
        for code in (0, 0xC0000005):
            with self.subTest(code=code), patch.object(sys, "stdin", SimpleNamespace(buffer=io.BytesIO(b"start\n"))), \
                    patch.object(QASession.subprocess, "call", return_value=code):
                self.assertEqual(QASession.worker(manifest, "ui"), 0 if code == 0 else 1)
            result = json.loads((self.base / "ui-exit.json").read_text())
            self.assertEqual(result["exit_code"], code)
            self.assertEqual(result["exit_hex"], f"0x{code:08X}")
            self.assertEqual(result["executable"], "owned-studio.exe")

    def test_debugger_launch_preserves_target_arguments_and_local_dump_directory(self):
        command = ["studio.exe", "http://127.0.0.1:12345", "argentis-qa", "ffmpeg.exe"]
        prefix = QASession.debug_ui_command(command, self.base, Path("procdump64.exe"))
        self.assertEqual(prefix[:6], ["procdump64.exe", "-accepteula", "-ma", "-e", "-g", "-x"])
        self.assertEqual(Path(prefix[6]), self.base / "dumps")
        self.assertTrue(Path(prefix[6]).is_dir())
        self.assertEqual(prefix[7:], command)
        self.assertIs(QASession.debug_ui_command(command, self.base, None), command)

    def test_job_close_kills_descendants_preserves_unrelated_process(self):
        sentinel = self.owned_process(PYTHON + ["-c", "import time; time.sleep(90)"])
        marker = self.base / "tree.json"
        job = Job()
        self.addCleanup(job.close)
        child = job.start(self.tree_command(marker), cwd=self.base, log=self.log)
        records = self.tree_records(marker)
        job.close()
        child.wait(timeout=5)
        self.assert_exited(records)
        self.assertIsNone(sentinel.poll())

    def test_supervisor_crash_kills_descendants(self):
        marker = self.base / "crash-tree.json"
        code = (f"import sys; sys.path.insert(0,{str(Path(__file__).resolve().parent)!r})\n"
                "from QAProcesses import Job\nimport subprocess,time\n"
                "job=Job()\n"
                f"child=job.start({self.tree_command(marker)!r},cwd={str(self.base)!r},log=subprocess.DEVNULL)\n"
                "time.sleep(90)\n")
        owner = self.owned_process(PYTHON + ["-c", code])
        records = self.tree_records(marker)
        owner.kill()  # Abrupt exit: no Python finally or atexit can run.
        owner.wait(timeout=5)
        self.assert_exited(records)

    def test_eof_before_assignment_does_not_start_work(self):
        marker = self.base / "no-start.json"
        child = self.owned_process(self.tree_command(marker), stdin=subprocess.PIPE)
        child.stdin.close()
        self.assertEqual(child.wait(timeout=5), 2)
        self.assertFalse(marker.exists())

    def test_assignment_failure_stops_worker_before_work(self):
        marker = self.base / "not-owned.json"
        job = Job()
        self.addCleanup(job.close)
        children = []
        original = subprocess.Popen

        def capture(*args, **kwargs):
            process = original(*args, **kwargs)
            children.append(process)
            return process

        with patch.object(QAProcesses, "assign_job", return_value=0), patch.object(subprocess, "Popen", side_effect=capture):
            with self.assertRaises(OSError):
                job.start(self.tree_command(marker), cwd=self.base, log=self.log)
        self.assertEqual(len(children), 1)
        self.assertIsNotNone(children[0].poll())
        self.assertFalse(marker.exists())

    def test_stale_identity_cannot_stop_another_process(self):
        child = self.owned_process(PYTHON + ["-c", "import time; time.sleep(90)"])
        record = identity(child.pid)
        for field, value in (("created", record["created"] + 1), ("exe", record["exe"] + ".other")):
            with self.subTest(field=field):
                self.assertFalse(matches({**record, field: value}, terminate=True))
                self.assertIsNone(child.poll())
        self.assertTrue(matches(record, terminate=True))
        child.wait(timeout=5)
        self.assertFalse(matches(record))

    def session_result(self):
        path, = self.base.glob("*/session.json")
        return json.loads(path.read_text(encoding="utf-8"))

    def assert_endpoint_closed(self, data):
        port = int(data["endpoint"].rsplit(":", 1)[1])
        with self.assertRaises((ConnectionRefusedError, TimeoutError)):
            with socket.create_connection(("127.0.0.1", port), timeout=1):
                self.fail("QA HTTP socket is still accepting connections")

    def test_startup_timeout_stops_server_worker(self):
        with patch.object(QASession, "SESSIONS", self.base):
            self.assertEqual(QASession.run_session(seconds=20, startup_seconds=.001), 124)
        data = self.session_result()
        self.assertEqual((data["state"], data["reason"]), ("stopped", "timeout"))
        self.assertEqual(set(data["children"]), {"server"})
        self.assert_exited(data["children"].values())

    def test_ui_start_failure_stops_http_server(self):
        # Python rejects the endpoint as a script path, simulating a failed UI.
        with patch.object(QASession, "SESSIONS", self.base):
            self.assertEqual(QASession.run_session(ui=Path(sys.executable), seconds=20), 2)
        data = self.session_result()
        self.assertEqual((data["state"], data["reason"]), ("stopped", "ui-exit"))
        self.assertEqual(set(data["children"]), {"server", "ui"})
        self.assert_exited(data["children"].values())
        self.assert_endpoint_closed(data)

    def test_runtime_timeout_stops_ready_server(self):
        with patch.object(QASession, "SESSIONS", self.base):
            self.assertEqual(QASession.run_session(seconds=3), 124)
        data = self.session_result()
        self.assertEqual((data["state"], data["reason"]), ("stopped", "timeout"))
        self.assertIn("ready_at", data)  # Exercise runtime expiry, beyond startup.
        self.assert_exited(data["children"].values())
        self.assert_endpoint_closed(data)

    def test_launcher_exit_stops_session(self):
        sessions = self.base / "launcher-sessions"
        controller = (f"import sys; sys.path.insert(0,{str(Path(__file__).resolve().parent)!r})\n"
                      "from pathlib import Path\nimport QASession\n"
                      f"QASession.SESSIONS=Path({str(sessions)!r})\n"
                      "sys.exit(QASession.run_session(seconds=20))\n")
        launcher_code = ("import json,pathlib,subprocess,sys,time\n"
                         f"subprocess.Popen({(PYTHON + ['-c', controller])!r})\n"
                         "deadline=time.monotonic()+10\n"
                         "while time.monotonic()<deadline:\n"
                         f"    manifests=list(pathlib.Path({str(sessions)!r}).glob('*/session.json'))\n"
                         "    if manifests and json.loads(manifests[0].read_text(encoding='utf-8'))['state']=='running': sys.exit(0)\n"
                         "    time.sleep(.05)\n"
                         "sys.exit(1)\n")
        launcher = self.owned_process(PYTHON + ["-c", launcher_code])

        def cleanup_controller():
            for path in sessions.glob("*/session.json"):
                matches(json.loads(path.read_text(encoding="utf-8"))["owner"], terminate=True)
        self.addCleanup(cleanup_controller)
        self.assertEqual(launcher.wait(timeout=12), 0)
        path, = sessions.glob("*/session.json")

        def finished():
            data = json.loads(path.read_text(encoding="utf-8"))
            return data if data["state"] == "stopped" else None
        data = wait_for(finished)
        self.assertEqual(data["reason"], "launcher-exit")
        self.assert_exited([data["owner"], *data["children"].values()])
        self.assert_endpoint_closed(data)

    def test_stop_request_stops_registered_session(self):
        output_path = self.base / "session-output.log"
        with output_path.open("wb") as output:
            command = PYTHON + [str(QASession.SOURCE / "Verification/WindowMenusQA.py"), "--seconds", "30"]
            owner = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                     creationflags=subprocess.CREATE_NO_WINDOW)
        def cleanup_owner():
            if owner.poll() is None:
                owner.kill()
            owner.wait(timeout=5)
        self.addCleanup(cleanup_owner)

        def running_manifest():
            content = output_path.read_text(encoding="utf-8", errors="replace")
            lines = [line.removeprefix("Manifest: ") for line in content.splitlines() if line.startswith("Manifest: ")]
            if not lines:
                return None
            path = Path(lines[0])
            data = json.loads(path.read_text(encoding="utf-8"))
            return (path, data) if data["state"] == "running" else None

        path, data = wait_for(running_manifest, seconds=15)
        self.assertEqual(data["owner"]["pid"], owner.pid)
        QASession.stop_session(path, data)
        self.assertEqual(owner.wait(timeout=8), 0)
        result = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual((result["state"], result["reason"]), ("stopped", "requested"))
        self.assert_exited(result["children"].values())
        self.assert_endpoint_closed(result)


if __name__ == "__main__":
    unittest.main(verbosity=2)

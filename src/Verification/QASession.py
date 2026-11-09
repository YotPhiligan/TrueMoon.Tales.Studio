"""Bounded, registered native QA session. Run through WindowMenusQA.py."""

import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))  # Portable Python uses an isolated _pth.
from QAProcesses import Job, identity, matches

SOURCE = Path(__file__).resolve().parents[1]
SESSIONS = SOURCE / ".local/qa-sessions"


def save(path, data):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def now():
    return datetime.now(timezone.utc).isoformat()


def stopped(run, deadline, launcher=None):
    if (run / "stop.request").exists():
        return "requested"
    if time.monotonic() >= deadline:
        return "timeout"
    if launcher is not None and not matches(launcher):
        return "launcher-exit"
    return None


def wait_ready(run, process, deadline, launcher):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    while not stopped(run, deadline, launcher):
        if process.poll() is not None:
            raise RuntimeError(f"QA server exited ({process.returncode}); see {run / 'server.log'}")
        output = (run / "server.log").read_text(encoding="utf-8", errors="replace")
        found = re.search(r"^Local: (http://127\.0\.0\.1:[0-9]+)\s*$", output, re.MULTILINE)
        if found:
            endpoint = found[1]
            try:
                with opener.open(endpoint + "/api/projects/argentis-qa", timeout=.5) as response:
                    if response.status == 200:
                        return endpoint
            except (urllib.error.URLError, TimeoutError):
                pass
        time.sleep(.1)
    if stopped(run, deadline, launcher) in ("requested", "launcher-exit"):
        return None
    raise TimeoutError("QA server startup timed out")


def worker(manifest, role):
    # No imports of fixture code, descendants, files or sockets before ownership.
    if sys.stdin.buffer.readline() != b"start\n":
        return 2
    if role not in {"server", "ui"}:
        raise ValueError("Unknown QA worker")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    code = subprocess.call(data["commands"][role], cwd=SOURCE, stdin=subprocess.DEVNULL)
    save(manifest.parent / f"{role}-exit.json", {"exit_code": code, "exit_hex": f"0x{code & 0xffffffff:08X}",
                                               "executable": data["commands"][role][0], "exited_at": now()})
    # Preserve the full Windows status above; the Python gateway uses a portable failure code.
    return code if 0 <= code <= 255 else 1


def debug_ui_command(command, run, procdump):
    if procdump is None:
        return command
    dumps = run / "dumps"
    dumps.mkdir()
    return [str(procdump), "-accepteula", "-ma", "-e", "-g", "-x", str(dumps), *command]


def run_session(*, ui=None, seconds=900, startup_seconds=45, fixture_script=None, procdump=None):
    if procdump is not None and ui is None:
        raise ValueError("ProcDump requires a UI executable")
    SESSIONS.mkdir(parents=True, exist_ok=True)
    run = SESSIONS / uuid.uuid4().hex
    run.mkdir()
    manifest = run / "session.json"
    data = {"version": 1, "id": run.name, "created_at": now(), "owner": identity(os.getpid()),
            "launcher": identity(os.getppid()), "owner_argv": sys.argv,
            "state": "starting", "seconds": seconds, "cwd": str(SOURCE), "children": {},
            "commands": {"server": [sys.executable, "-s", "-B", str(SOURCE / "Verification/WindowMenusQA.py"),
                                    "--fixture", str(run)]}}
    if fixture_script is not None:
        fixture_script = Path(fixture_script).resolve()
        if fixture_script.parent != SOURCE / "Verification" or fixture_script.name not in {"WindowMenusQA.py", "PlaybackQA.py"}:
            raise ValueError("Expected a supported Studio QA fixture")
        data["commands"]["server"][3] = str(fixture_script)
    save(manifest, data)
    print(f"QA session: {run.name}\nManifest: {manifest}", flush=True)
    deadline = time.monotonic() + seconds
    job = None
    children = []
    reason = "error"
    try:
        job = Job()
        with (run / "server.log").open("wb") as log:
            server = job.start([sys.executable, "-s", "-B", str(Path(__file__)),
                                "--worker", str(manifest), "server"], cwd=SOURCE, log=log)
        children.append(server)
        data["children"]["server"] = identity(server.pid)
        save(manifest, data)
        endpoint = wait_ready(run, server, min(deadline, time.monotonic() + startup_seconds), data["launcher"])
        if endpoint is None:
            reason = stopped(run, deadline, data["launcher"]) or "requested"
            return 0
        data["endpoint"] = endpoint
        ui_process = None
        if ui:
            import imageio_ffmpeg
            command = [str(ui), endpoint, "argentis-qa", imageio_ffmpeg.get_ffmpeg_exe()]
            data["ui_application_command"] = command
            data["commands"]["ui"] = debug_ui_command(command, run, procdump)
            save(manifest, data)
            with (run / "ui.log").open("wb") as log:
                ui_process = job.start([sys.executable, "-s", "-B", str(Path(__file__)),
                                        "--worker", str(manifest), "ui"], cwd=SOURCE, log=log)
            children.append(ui_process)
            data["children"]["ui"] = identity(ui_process.pid)
        data.update(state="running", ready_at=now())
        save(manifest, data)
        print(f"Ready: {endpoint}\nProject: argentis-qa\nExpires after {seconds:g}s; Ctrl+C or --stop {run.name} cleans the session.", flush=True)
        while True:
            reason = stopped(run, deadline, data["launcher"])
            if reason:
                return 124 if reason == "timeout" else 0
            if server.poll() is not None:
                reason = "server-exit"
                return server.returncode or 1
            if ui_process is not None and ui_process.poll() is not None:
                reason = "ui-exit"
                return ui_process.returncode
            time.sleep(.1)
    except TimeoutError as error:
        reason = "timeout"
        data["error"] = str(error)
        return 124
    except KeyboardInterrupt:
        reason = "interrupt"
        return 130
    except BaseException as error:
        data["error"] = str(error)
        raise
    finally:
        try:
            if job is not None:
                job.close()  # Confirms zero live descendants before recording success.
            for process in children:
                process.wait(timeout=5)
            data.update(state="stopped", stopped_at=now(), reason=reason)
        except BaseException as error:
            data.update(state="cleanup-error", error=str(error))
            raise
        finally:
            save(manifest, data)
            print(f"QA {run.name}: {data['state']} ({reason})", flush=True)


def entries(session_id=None):
    if session_id is not None:
        if not re.fullmatch(r"[a-f0-9]{32}", session_id):
            raise ValueError("Expected the 32-character QA session ID")
        paths = [SESSIONS / session_id / "session.json"]
    else:
        paths = sorted(SESSIONS.glob("*/session.json"))
    for path in paths:
        data = json.loads(path.read_text(encoding="utf-8"))
        if data["version"] != 1 or data["id"] != path.parent.name:
            raise ValueError(f"Invalid QA manifest: {path}")
        yield path, data


def status():
    result = []
    for path, data in entries():
        alive = matches(data["owner"])
        result.append({"id": data["id"], "state": data["state"] if alive else "inactive",
                       "recorded_state": data["state"], "owner_alive": alive,
                       "endpoint": data.get("endpoint"), "manifest": str(path)})
    print(json.dumps(result, indent=2))


def stop_session(path, data):
    owner = data["owner"]
    if not matches(owner):
        print(f"QA {data['id']}: inactive (no matching owner)")
        return
    (path.parent / "stop.request").touch()
    deadline = time.monotonic() + 7
    while matches(owner):
        if time.monotonic() >= deadline:
            # Recheck creation time + image on a single handle before forced exit.
            # Closing the owner's non-inherited job handle stops only its QA tree.
            matches(owner, terminate=True)
            for child in data["children"].values():
                child_deadline = time.monotonic() + 5
                while matches(child):
                    if time.monotonic() >= child_deadline:
                        raise TimeoutError("QA child is still exiting after supervisor termination")
                    time.sleep(.05)
            print(f"QA {data['id']}: supervisor stopped; Windows closes its job")
            return
        time.sleep(.1)
    print(f"QA {data['id']}: stopped")


def positive(value):
    number = float(value)
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError("Expected a finite positive number of seconds")
    return number


def main(fixture_script=None):
    # Internal gateway; the parent owns the job before sending the start token.
    if len(sys.argv) == 4 and sys.argv[1] == "--worker":
        return worker(Path(sys.argv[2]), sys.argv[3])
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--status", action="store_true")
    action.add_argument("--stop", metavar="SESSION_ID")
    action.add_argument("--stop-all", action="store_true")
    parser.add_argument("--ui", type=Path, help="Own this UI as well; closing it also stops the QA server")
    parser.add_argument("--procdump", type=Path, help="Optional local ProcDump executable; launch only this UI under its native debugger")
    parser.add_argument("--seconds", type=positive, default=900, help="Maximum total lifetime, including startup (default: 900)")
    parser.add_argument("--startup-seconds", type=positive, default=45)
    args = parser.parse_args()
    if args.status:
        status()
    elif args.stop or args.stop_all:
        for path, data in entries(args.stop):
            stop_session(path, data)
    else:
        ui = args.ui.resolve() if args.ui else None
        if ui is not None and not ui.is_file():
            parser.error(f"UI executable does not exist: {ui}")
        procdump = args.procdump.resolve() if args.procdump else None
        if procdump is not None and (ui is None or not procdump.is_file() or procdump.name.lower() != "procdump64.exe"):
            parser.error("--procdump requires --ui and an existing procdump64.exe")
        return run_session(ui=ui, seconds=args.seconds, startup_seconds=args.startup_seconds, fixture_script=fixture_script, procdump=procdump)
    return 0


if __name__ == "__main__":
    sys.exit(main())

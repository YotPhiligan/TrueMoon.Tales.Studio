"""Explicit native Studio lifecycle check, confined to a registered isolated QA session."""

from pathlib import Path
import argparse
import json
import os
import re
import subprocess
import sys

SOURCE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SOURCE / "Verification"))
from QAProcesses import Job, matches


def worker(identifier):
    import imageio_ffmpeg
    run = SOURCE / ".local/qa-sessions" / identifier
    manifest = json.loads((run / "session.json").read_text(encoding="utf-8"))
    if manifest["state"] != "running" or not matches(manifest["owner"]):
        raise RuntimeError("Native check requires this live registered QA server")
    if not (SOURCE / ".local/diag-tools/procdump/procdump64.exe").is_file():
        raise FileNotFoundError("Expected local Microsoft ProcDump in .local/diag-tools/procdump")
    return subprocess.call(["dotnet", "test", "TrueMoon.Tales.Studio.slnx", "--no-restore", "--artifacts-path", ".local/studio-build",
        "--configuration", "Release", "--filter", "Category=NativeSmoke", "--blame-crash", "--blame-crash-dump-type", "full",
        "--results-directory", str(run / "test-results"), "-bl:.local/native-lifecycle-tests-{}.binlog"], cwd=SOURCE,
        env={**os.environ, "STUDIO_NATIVE_QA_ENDPOINT": manifest["endpoint"] + "/", "STUDIO_NATIVE_QA_FFMPEG": imageio_ffmpeg.get_ffmpeg_exe(),
             "STUDIO_NATIVE_QA_RUN": str(run), "PROCDUMP_PATH": str(SOURCE / ".local/diag-tools/procdump"), "DOTNET_CLI_UI_LANGUAGE": "en-US"})


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--worker":
        if sys.stdin.buffer.readline() != b"start\n": sys.exit(2)
        identifier = sys.argv[2]
        if not re.fullmatch(r"[0-9a-f]{32}", identifier): raise ValueError("Expected QA session ID")
        sys.exit(worker(identifier))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session_id")
    args = parser.parse_args()
    identifier = args.session_id
    if not re.fullmatch(r"[0-9a-f]{32}", identifier): parser.error("Expected registered 32-character QA session ID")
    run = SOURCE / ".local/qa-sessions" / identifier
    job = Job()
    child = None
    try:
        with (run / "native-lifecycle-test.log").open("wb") as log:
            child = job.start([sys.executable, "-s", "-B", str(Path(__file__)), "--worker", identifier], cwd=SOURCE, log=log)
        child.wait(timeout=90)
        print((run / "native-lifecycle-test.log").read_text(encoding="utf-8", errors="replace"))
        result = child.returncode
    finally:
        job.close()
        if child is not None: child.wait(timeout=5)
    sys.exit(result)

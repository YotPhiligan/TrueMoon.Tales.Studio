"""Check the saved three-gesture native QA run without starting a server or GPU."""

import argparse
import hashlib
import json
from pathlib import Path
import re

QA_ROOT = Path(__file__).resolve().parents[1] / ".local/qa-sessions"


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def check(run):
    run = run.resolve()
    run.relative_to(QA_ROOT.resolve())
    if not re.fullmatch(r"[0-9a-f]{32}", run.name):
        raise ValueError("Expected a registered QA session folder")
    relative = Path("projects/argentis-qa/Studio.project.json")
    original = read(run / "baseline" / relative)
    current = read(run / "copy" / relative)
    right = read(run / "project-after-right.json")
    left = read(run / "project-after-left.json")
    assert [original["revision"], right["revision"], left["revision"], current["revision"]] == [5, 6, 7, 8]
    clip = current["timeline"]["clips"][0]
    assert (clip["start_frame"], clip["duration_frames"], clip["source_in_frame"], clip["source_out_frame"]) == (2, 10, 2, 12)
    assert current["timeline"]["clips"][1:] == original["timeline"]["clips"][1:]
    assert current["timeline"]["segments"] == original["timeline"]["segments"]
    assert current["blocks"] == original["blocks"]
    assert current["assets"] == original["assets"]
    history = run / "copy/projects/argentis-qa/Production/History"
    original_snapshot, = history.glob("r000005-*.json")
    assert read(original_snapshot) == original
    assert sorted(path.name[:7] for path in history.glob("r00000[5-7]-*.json")) == ["r000005", "r000006", "r000007"]
    hashes = {}
    for baseline in (run / "baseline").rglob("*.mp4"):
        relative_media = baseline.relative_to(run / "baseline")
        expected = hashlib.sha256(baseline.read_bytes()).hexdigest()
        assert hashlib.sha256((run / "copy" / relative_media).read_bytes()).hexdigest() == expected
        hashes[relative_media.as_posix()] = expected
    assert len(hashes) == 2
    assert not list((run / "copy").rglob("Jobs"))
    posts = (run / "server.log").read_text(encoding="utf-8").count('"POST /api/projects/argentis-qa/timeline HTTP/1.1" 200')
    assert posts == 3
    result = {"ok": True, "revision": current["revision"], "timeline_start": clip["start_frame"],
              "duration": clip["duration_frames"], "source": [2, 12], "successful_posts": posts,
              "other_clips_unchanged": True, "history_retained": True, "media_sha256": hashes,
              "jobs_created": False}
    (run / "drag-trim-checks.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    check(parser.parse_args().run)

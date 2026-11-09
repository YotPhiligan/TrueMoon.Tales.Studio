"""Audit a PlaybackQA session; explicitly opt into integration/device or CPU-export checks."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

SOURCE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SOURCE / "Verification"))


def session(identifier):
    if not re.fullmatch(r"[0-9a-f]{32}", identifier):
        raise ValueError("Expected a registered QA session ID")
    run = (SOURCE / ".local/qa-sessions" / identifier).resolve()
    run.relative_to((SOURCE / ".local/qa-sessions").resolve())
    return run


def audit(run):
    relative = Path("projects/argentis-qa")
    baseline = run / "baseline" / relative
    current = run / "copy" / relative
    before = (baseline / "Studio.project.json").read_bytes()
    assert (current / "Studio.project.json").read_bytes() == before
    project = json.loads(before)
    assert project["name"] == "Playback QA"
    hashes = {}
    for file in baseline.rglob("*"):
        if file.is_file() and file.suffix in {".mp4", ".wav"}:
            expected = hashlib.sha256(file.read_bytes()).hexdigest()
            assert hashlib.sha256((current / file.relative_to(baseline)).read_bytes()).hexdigest() == expected
            hashes[file.relative_to(baseline).as_posix()] = expected
    assert len(hashes) == 4 and "POST " not in (run / "server.log").read_text(encoding="utf-8")
    assert not list(current.rglob("Jobs"))
    result = {"ok": True, "revision": project["revision"], "project_unchanged": True,
              "media_sha256": hashes, "posts": 0, "jobs_created": False}
    (run / "playback-audit.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


def integration(run):
    import imageio_ffmpeg
    manifest = json.loads((run / "session.json").read_text(encoding="utf-8"))
    return subprocess.call(["dotnet", "test", "TrueMoon.Tales.Studio.slnx", "--no-restore", "--artifacts-path", ".local/studio-build",
        "--configuration", "Debug", "--filter", "Category=PlaybackIntegration", "-bl:.local/playback-integration-{}.binlog"], cwd=SOURCE,
        env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US", "STUDIO_PLAYBACK_QA_ENDPOINT": manifest["endpoint"] + "/",
             "STUDIO_PLAYBACK_QA_RUN": str(run), "STUDIO_PLAYBACK_QA_FFMPEG": imageio_ffmpeg.get_ffmpeg_exe()})


def cpu(run):
    import av
    import numpy as np
    sys.path.insert(0, str(SOURCE.parent))
    from studio.paths import WorkspacePaths
    from studio.model import Store
    from studio.exports import Exports
    paths = WorkspacePaths.load(run / "copy/Studio.config.json", code_root=SOURCE.parent)
    store = Store(paths.legacy_root, paths.amv_root, paths.state_root, paths=paths)
    project_path = store.location("argentis-qa")
    before = project_path.read_bytes()
    project = json.loads(before)
    clip = project["timeline"]["clips"][0]
    assert clip["duration_frames"] == 144 and clip["source_in_frame"] == 24
    exports = Exports(store)
    try:
        completed = [row for row in exports.list("argentis-qa") if row["project_revision"] == project["revision"] and row["state"] == "completed"]
        row = completed[0] if completed else exports.create("argentis-qa", project["revision"], actor="qa:playback")
        deadline = time.monotonic() + 55
        while row["state"] in {"queued", "running", "stopping"}:
            if time.monotonic() >= deadline:
                raise TimeoutError("CPU export deadline")
            time.sleep(.1)
            row = exports.get("argentis-qa", row["id"])
        assert row["state"] == "completed", row
        output = exports.video("argentis-qa", row["id"])
        with av.open(str(output)) as container:
            audio = np.concatenate([frame.to_ndarray() for frame in container.decode(audio=0)], axis=1).T
        start = round(clip["start_frame"] / project["fps"] * 48000)
        preview_audio = np.fromfile(run / "preview-audio.f32", dtype="<f4").reshape(-1, 2)
        reference = audio[start:start + len(preview_audio)]
        assert preview_audio.shape == reference.shape
        rmse = float(np.sqrt(np.mean((preview_audio - reference) ** 2)))
        assert rmse < .01, rmse
        def amplitude(data, hz):
            signal = data.mean(axis=1)
            phase = np.exp(-2j * np.pi * hz * np.arange(len(signal)) / 48000)
            return float(abs((signal * phase).mean()) * 2)
        event = [amplitude(preview_audio[48000:96000], 880), amplitude(preview_audio[96000:144000], 880), amplitude(preview_audio[144000:192000], 880)]
        assert event[1] > .04 and event[0] < .001 and event[2] < .001
        with av.open(str(output)) as container:
            images = [frame.reformat(width=480, height=270, format="bgra").to_ndarray() for frame in container.decode(video=0)]
        samples = sorted(run.glob("video-*.bgra"))
        expected_sources = {24 + int(path.stem.split("-")[1]) for path in samples}
        candidates = {value for frame in expected_sources for value in (frame - 1, frame, frame + 1)}
        source_frames = {}
        with av.open(str(project_path.parent / "video/take-A.mp4")) as container:
            for index, frame in enumerate(container.decode(video=0)):
                if index in candidates:
                    source_frames[index] = frame.reformat(width=480, height=270, format="bgra").to_ndarray()[110:, :, :3].astype(np.int16)
        checks = []
        for path in samples:
            local = int(path.stem.split("-")[1])
            preview = np.fromfile(path, dtype=np.uint8).reshape(270, 480, 4)
            frame = images[clip["start_frame"] + local]
            error = float(np.abs(preview[110:, :, :3].astype(np.int16) - frame[110:, :, :3].astype(np.int16)).mean())
            assert error < 8, (local, error)
            expected = 24 + local
            errors = {index: float(np.abs(preview[110:, :, :3].astype(np.int16) - source_frames[index]).mean())
                      for index in (expected - 1, expected, expected + 1)}
            assert min(errors, key=errors.get) == expected, (local, errors)
            checks.append({"local_frame": local, "source_frame": 24 + local, "label_mae": error})
        assert len(checks) == 4 and project_path.read_bytes() == before
        result = {"ok": True, "output": str(output), "project_unchanged": True, "pcm_vs_aac_rmse": rmse,
                  "event_880hz_amplitude": event, "video_checks": checks, "fps": 24, "export_frames": len(images)}
        (run / "playback-cpu-checks.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps(result, indent=2))
    finally:
        exports.close()
    return 0


def bounded(run, mode):
    from QAProcesses import Job
    job = Job()
    child = None
    try:
        with (run / f"check-{mode}.log").open("wb") as log:
            child = job.start([sys.executable, "-s", "-B", str(Path(__file__)), "--worker", run.name, mode], cwd=SOURCE, log=log)
        child.wait(timeout=90)
        print((run / f"check-{mode}.log").read_text(encoding="utf-8", errors="replace"))
        return child.returncode
    finally:
        job.close()
        if child is not None:
            child.wait(timeout=5)


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "--worker":
        if sys.stdin.buffer.readline() != b"start\n": return 2
        run = session(sys.argv[2])
        if sys.argv[3] not in {"integration", "cpu"}: raise ValueError("Unknown check")
        return integration(run) if sys.argv[3] == "integration" else cpu(run)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session_id")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--integration", action="store_true", help="Live short fixture, FFmpeg and short audible device check")
    group.add_argument("--cpu", action="store_true", help="Explicit CPU export/reference comparison after integration")
    args = parser.parse_args()
    run = session(args.session_id)
    if args.integration or args.cpu:
        return bounded(run, "integration" if args.integration else "cpu")
    audit(run)
    return 0


if __name__ == "__main__": sys.exit(main())

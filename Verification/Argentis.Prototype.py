"""Start the existing Studio HTTP server with a fresh copy of a numbered-frame QA scene."""

import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import app
from studio import scenario
from studio.model import Store, atomic
from studio.paths import WorkspacePaths


def make_take(path, ffmpeg, label, color):
    font = ImageFont.load_default(size=44)
    frames = []
    for index in range(12):
        image = Image.new("RGB", (320, 180), color)
        draw = ImageDraw.Draw(image)
        draw.text((18, 22), f"TAKE {label}", font=font, fill="white")
        draw.text((18, 87), f"FRAME {index:02}", font=font, fill="white")
        frames.append(image.tobytes())
    subprocess.run(
        [ffmpeg, "-nostdin", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
         "-s", "320x180", "-r", "24", "-i", "pipe:0", "-c:v", "libx264", "-pix_fmt",
         "yuv420p", str(path)],
        input=b"".join(frames), check=True, timeout=20,
    )


def create_scene(config, ffmpeg):
    atomic(config, {
        "version": 1,
        "legacy_read_enabled": False,
        "legacy_root": str(ROOT.parent / ".argentis-qa-disabled-legacy"),
        "amv_root": "amv",
        "projects_root": "projects",
        "state_root": "state",
        "comfy_root": "disabled-comfy",
        "python_executable": sys.executable,
        "mounts": {},
    })
    paths = WorkspacePaths.load(config, code_root=ROOT)
    store = Store(paths.legacy_root, paths.amv_root, paths.state_root, paths=paths)
    project = store.create("Argentis QA", project_id="argentis-qa")
    project = store.operation(project["id"], project["revision"], "add_block", {
        "segment_id": project["segments"][0]["id"], "label": "QA-01",
    }, "fixture")
    block = project["blocks"][0]
    video = store.location(project["id"]).parent / "video"
    video.mkdir()
    for label, color in [("A", "#234985"), ("B", "#8d352e")]:
        file = video / f"take-{label}.mp4"
        make_take(file, ffmpeg, label, color)
        block["takes"].append({
            "id": f"take-{label}",
            "name": f"Дубль {label}",
            "video_asset_id": store.asset(project, file),
            "video_info": store.probe_video(file),
            "technical_status": "success",
            "review_state": "unreviewed",
        })
    project = scenario.apply(store, project, "add_segment", {
        "name": "Тестовая сцена", "start_frame": 0, "duration_frames": 12,
    })
    segment = project["timeline"]["scenario"]["segments"][0]
    project = scenario.apply(store, project, "add_item", {
        "segment_id": segment["id"], "block_id": block["id"], "title": "Клип QA-01",
        "take_id": "take-A", "source_in_frame": 0, "duration_frames": 8,
    })
    store.commit(project, project["revision"], "fixture", "Тестовая сцена с двумя дублями")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=0)
    args = parser.parse_args()
    local = ROOT / ".local"
    local.mkdir(exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="argentis-qa-", dir=local))
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    baseline = run / "baseline"
    create_scene(baseline / "Studio.config.json", ffmpeg)
    copy = run / "copy"
    shutil.copytree(baseline, copy)
    print(f"QA copy: {copy}\nProject: argentis-qa\nFFmpeg: {ffmpeg}", flush=True)
    sys.argv[1:] = ["--port", str(args.port), "--config", str(copy / "Studio.config.json")]
    app.main()


if __name__ == "__main__":
    main()

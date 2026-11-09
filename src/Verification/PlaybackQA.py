"""An audible 24-fps fixture for continuous playback and CPU-export comparison."""

import array
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import wave

SOURCE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SOURCE / "Verification"))


def tone(path, seconds, hz, amplitude, pulses=False):
    samples = array.array("h")
    for i in range(round(seconds * 48000)):
        time = i / 48000
        envelope = (1 if time % 1 < .3 else .15) if pulses else 1
        value = int(math.sin(time * hz * 2 * math.pi) * amplitude * envelope * 32767)
        samples.extend([value, value])
    with wave.open(str(path), "wb") as stream:
        stream.setparams((2, 2, 48000, 0, "NONE", "not compressed"))
        stream.writeframes(samples.tobytes())


def serve(run):
    import imageio_ffmpeg
    from PIL import Image, ImageDraw, ImageFont
    sys.path.insert(0, str(SOURCE.parent))
    spec = importlib.util.spec_from_file_location("playback_fixture", SOURCE.parent / "Verification/Argentis.Prototype.py")
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    config = run / "baseline/Studio.config.json"
    fixture.atomic(config, {"version": 1, "legacy_read_enabled": False,
        "legacy_root": str(SOURCE.parent.parent / ".playback-qa-disabled-legacy"), "amv_root": "amv",
        "projects_root": "projects", "state_root": "state", "comfy_root": "disabled-comfy",
        "python_executable": sys.executable, "mounts": {}})
    paths = fixture.WorkspacePaths.load(config, code_root=SOURCE.parent)
    store = fixture.Store(paths.legacy_root, paths.amv_root, paths.state_root, paths=paths)
    project = store.create("Playback QA", project_id="argentis-qa")
    project = store.operation(project["id"], project["revision"], "add_block", {"segment_id": project["segments"][0]["id"], "label": "QA-PLAY"}, "fixture")
    home = store.location(project["id"]).parent
    video = home / "video"; video.mkdir()
    audio = home / "audio"; audio.mkdir()
    long_mode = os.environ.get("STUDIO_PLAYBACK_QA_LONG") == "1"
    source_seconds = 28 if long_mode else 8
    duration = 600 if long_mode else 144
    segment_frames = 672 if long_mode else 216
    tone(audio / "native.wav", source_seconds, 220, .10)
    tone(audio / "music.wav", source_seconds + 2, 440, .15, pulses=True)
    tone(audio / "event.wav", 2, 880, .12)
    font = ImageFont.load_default(size=40)
    frames = []
    for index in range(source_seconds * 24):
        image = Image.new("RGB", (320, 180), "#234985")
        draw = ImageDraw.Draw(image)
        draw.text((16, 20), "TAKE A", font=font, fill="white")
        draw.text((16, 87), f"FRAME {index:03}", font=font, fill="white")
        frames.append(image.tobytes())
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    file = video / "take-A.mp4"
    subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
        "-s", "320x180", "-r", "24", "-i", "pipe:0", "-i", str(audio / "native.wav"),
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", str(file)],
        input=b"".join(frames), timeout=30, check=True)
    project["blocks"][0]["takes"].append({"id": "take-A", "name": "Дубль A · звук 220 Гц",
        "video_asset_id": store.asset(project, file), "video_info": store.probe_video(file),
        "technical_status": "success", "review_state": "unreviewed"})
    project = fixture.scenario.apply(store, project, "add_segment", {"name": "Просмотр и звук", "start_frame": 0, "duration_frames": segment_frames})
    segment = project["timeline"]["scenario"]["segments"][0]
    project = fixture.scenario.apply(store, project, "add_item", {"segment_id": segment["id"], "block_id": project["blocks"][0]["id"],
        "title": "Клип со звуком", "take_id": "take-A", "source_in_frame": 24, "source_out_frame": 24 + duration,
        "duration_frames": duration, "gap_before_frames": 12, "speed": 1})
    clip = project["timeline"]["clips"][0]
    project["timeline"]["clip_audio"] = True
    project["timeline"]["music"] = {"id": "music-qa", "asset_id": store.asset(project, audio / "music.wav"),
        "start_frame": 0, "duration_frames": segment_frames, "source_in_frame": 0, "gain": .5, "muted": False}
    with wave.open(str(audio / "music.wav"), "rb") as source:
        project["assets"][project["timeline"]["music"]["asset_id"]]["audio_info"] = {
            "seconds": source.getnframes() / source.getframerate(), "sample_rate": source.getframerate(),
            "channels": source.getnchannels(), "source_sha256": hashlib.sha256((audio / "music.wav").read_bytes()).hexdigest()}
    project["timeline"]["sound_events"] = [{"id": "sound-qa", "name": "Тон 880 Гц", "lane": "sfx", "segment_id": segment["id"],
        "clip_id": clip["id"], "asset_id": store.asset(project, audio / "event.wav"), "stream": 0, "offset_frames": 48,
        "source_in_frame": 0, "duration_frames": 24, "source_seconds": 2, "gain": .5, "muted": False}]
    from studio import timeline
    timeline.validate(project, project["timeline"])
    store.commit(project, project["revision"], "fixture", "Звуковая QA-сцена")
    shutil.copytree(run / "baseline", run / "copy")
    print(f"QA run: {run}\nProject: argentis-qa\nFFmpeg: {ffmpeg}", flush=True)
    sys.argv[1:] = ["--port", "0", "--config", str(run / "copy/Studio.config.json")]
    fixture.app.main()


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--fixture":
        serve(Path(sys.argv[2]))
    else:
        if "--long" in sys.argv:
            os.environ["STUDIO_PLAYBACK_QA_LONG"] = "1"
            sys.argv.remove("--long")
        from QASession import main
        sys.exit(main(fixture_script=Path(__file__)))

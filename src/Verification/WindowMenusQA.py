"""Start an isolated two-clip HTTP scene for Studio's title bar and editing workflows."""

import copy
import importlib.util
import json
from pathlib import Path
import shutil
import sys

SOURCE = Path(__file__).resolve().parents[1]
STUDIO = SOURCE.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))


def serve_fixture(run):
    import imageio_ffmpeg
    sys.path.insert(0, str(STUDIO))
    spec = importlib.util.spec_from_file_location("argentis_fixture", STUDIO / "Verification/Argentis.Prototype.py")
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    config = run / "baseline/Studio.config.json"
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    fixture.create_scene(config, ffmpeg)
    paths = fixture.WorkspacePaths.load(config, code_root=STUDIO)
    store = fixture.Store(paths.legacy_root, paths.amv_root, paths.state_root, paths=paths)
    project = json.loads(store.location("argentis-qa").read_text(encoding="utf-8"))
    project = store.operation(project["id"], project["revision"], "add_block", {
        "segment_id": project["segments"][0]["id"], "label": "QA-02",
    }, "fixture")
    block = project["blocks"][-1]
    take = copy.deepcopy(project["blocks"][0]["takes"][0])
    take.update(id="take-C", name="Дубль C")
    block["takes"] = [take]
    project = fixture.scenario.apply(store, project, "add_segment", {
        "name": "Вторая сцена", "start_frame": 12, "duration_frames": 12,
    })
    segment = project["timeline"]["scenario"]["segments"][-1]
    project = fixture.scenario.apply(store, project, "add_item", {
        "segment_id": segment["id"], "block_id": block["id"], "title": "Клип QA-02",
        "take_id": "take-C", "source_in_frame": 0, "duration_frames": 6,
    })
    store.commit(project, project["revision"], "fixture", "Второй клип с единственным дублем")
    shutil.copytree(run / "baseline", run / "copy")
    print(f"QA run: {run}\nProject: argentis-qa\nFFmpeg: {ffmpeg}", flush=True)
    sys.argv[1:] = ["--port", "0", "--config", str(run / "copy/Studio.config.json")]
    fixture.app.main()


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--fixture":
        serve_fixture(Path(sys.argv[2]))
    else:
        from QASession import main
        sys.exit(main())

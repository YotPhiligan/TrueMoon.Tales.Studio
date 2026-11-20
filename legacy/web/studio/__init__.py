"""Local True Moon Studio. No model or server starts on import."""
from pathlib import Path

# Web and the CPU worker share the same model and renderer sources.
__path__.append(str(Path(__file__).resolve().parents[3] / 'workers/export/studio'))

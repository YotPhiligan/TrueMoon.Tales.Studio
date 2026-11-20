"""Compatibility entry point for the Studio agent CLI."""
from pathlib import Path
from runpy import run_path

if __name__ == '__main__':
    run_path(str(Path(__file__).resolve().parent / 'legacy/web/studio_agent.py'), run_name='__main__')

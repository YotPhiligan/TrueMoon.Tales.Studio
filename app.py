"""Compatibility entry point for the legacy Web launcher."""
from pathlib import Path
from runpy import run_path

if __name__ == '__main__':
    run_path(str(Path(__file__).resolve().parent / 'legacy/web/app.py'), run_name='__main__')

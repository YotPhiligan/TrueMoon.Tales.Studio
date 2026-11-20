"""Compatibility entry point for the managed QA session."""
from pathlib import Path
from runpy import run_path

if __name__ == '__main__':
    run_path(str(Path(__file__).resolve().parents[2] / 'tools/qa/WindowMenusQA.py'), run_name='__main__')

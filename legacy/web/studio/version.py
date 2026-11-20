"""Product version shared with the managed Studio build and distribution."""
from pathlib import Path
from xml.etree import ElementTree

# Distributions retain src/Directory.Build.props as the common version source.
VERSION = ElementTree.parse(Path(__file__).resolve().parents[3] / 'src/Directory.Build.props').getroot().findtext('PropertyGroup/Version')
if not VERSION:
    raise RuntimeError('Studio product Version is missing from src/Directory.Build.props')

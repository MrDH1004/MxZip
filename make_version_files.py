"""Version resources from version.py, so every binary carries ProductName "MxZip" and the same ProductVersion
(SignPath enforces both): build/version_info.txt for PyInstaller (--version-file) and shell/build/version.h
for the shell extension's .rc."""
from pathlib import Path

from version import __version__
from version_contract import write_version_resources

write_version_resources(Path(__file__).resolve().parent, __version__)
print(__version__)

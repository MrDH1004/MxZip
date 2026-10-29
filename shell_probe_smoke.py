"""Minimal smoke for the shell DLL independent of Explorer handler discovery.

The caller must build shell/build/probe.exe and shell/build/yzipshell.dll first.
"""
from __future__ import annotations

import os
import subprocess
import tempfile
import winreg


ROOT = os.path.dirname(os.path.abspath(__file__))
PROBE = os.path.join(ROOT, "shell", "build", "probe.exe")
DLL = os.path.join(ROOT, "shell", "build", "yzipshell.dll")
SETTINGS = r"Software\yzip"
VALUES = {
    "Command": (winreg.REG_SZ, '"C:\\yzip-smoke\\yzip.exe"'),
    "ArchiveExts": (winreg.REG_SZ, ".zip;.7z"),
    "ImageExts": (winreg.REG_SZ, ".iso"),
    "MenuMask": (winreg.REG_DWORD, 7),
    "Language": (winreg.REG_SZ, "en"),
}


def snapshot_values():
    saved = {}
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, SETTINGS)
    except FileNotFoundError:
        return saved
    with key:
        for name in VALUES:
            try:
                saved[name] = winreg.QueryValueEx(key, name)
            except FileNotFoundError:
                saved[name] = None
    return saved


def restore_values(saved):
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, SETTINGS) as key:
        for name in VALUES:
            old = saved.get(name)
            if old is None:
                try:
                    winreg.DeleteValue(key, name)
                except FileNotFoundError:
                    pass
            else:
                value, kind = old
                winreg.SetValueEx(key, name, 0, kind, value)


def main() -> int:
    if not os.path.isfile(PROBE) or not os.path.isfile(DLL):
        raise SystemExit("build shell/build/probe.exe and yzipshell.dll first")
    saved = snapshot_values()
    try:
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, SETTINGS) as key:
            for name, (kind, value) in VALUES.items():
                winreg.SetValueEx(key, name, 0, kind, value)
        with tempfile.TemporaryDirectory(prefix="yzip-shell-probe-") as folder:
            path = os.path.join(folder, "sample.txt")
            open(path, "wb").close()
            result = subprocess.run(
                [PROBE, "direct-list", DLL, path], capture_output=True, text=True, encoding="utf-8"
            )
        expected = "\tyzip.compress"
        if result.returncode or "DIRECT_DLL" not in result.stdout or expected not in result.stdout:
            print(result.stdout, end="")
            print(result.stderr, end="")
            return 1
        print("PASS direct DLL context-menu probe")
        return 0
    finally:
        restore_values(saved)


if __name__ == "__main__":
    raise SystemExit(main())

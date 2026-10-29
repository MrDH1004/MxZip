from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


ROOT = Path(__file__).resolve().parent
DRIVER = r"""
import builtins
import json
import os
import sys

real_import = builtins.__import__
def no_qt(name, *args, **kwargs):
    if name == "yzip_gui" or name.startswith("PySide6") or name.startswith("shiboken"):
        raise AssertionError(f"noninteractive --archive imported Qt: {name}")
    return real_import(name, *args, **kwargs)
builtins.__import__ = no_qt
sys.argv = ["yzip"] + json.loads(os.environ["YZIP_TEST_ARGS"])
import yzip_main
code = yzip_main.main()
print("RESULT " + json.dumps({
    "code": code,
    "pyside": any(name.startswith("PySide6") for name in sys.modules),
    "shiboken": any(name.startswith("shiboken") for name in sys.modules),
}))
"""


class ArchiveFastPathTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="yzip_archive_fastpath_")
        self.root = Path(self.temp.name)
        runtime_temp = self.root / "runtime-temp"
        runtime_temp.mkdir()
        settings_path = self.root / "settings.json"
        settings_path.write_text(json.dumps({"temp_dir": str(runtime_temp)}), encoding="utf-8")
        self.env = dict(
            os.environ,
            YZIP_NO_OPEN="1",
            YZIP_GATHER_SCALE="0.05",
            YZIP_SETTINGS=str(settings_path),
            LOCALAPPDATA=str(self.root / "local"),
        )

    def tearDown(self):
        self.temp.cleanup()

    def _start(self, path: Path) -> subprocess.Popen:
        env = dict(self.env, YZIP_TEST_ARGS=json.dumps(["--archive", str(path)]))
        return subprocess.Popen(
            [sys.executable, "-W", "ignore", "-c", DRIVER],
            cwd=ROOT,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )

    def _finish(self, process: subprocess.Popen) -> dict:
        stdout, stderr = process.communicate(timeout=60)
        self.assertEqual(0, process.returncode, stdout + stderr)
        line = next((line for line in stdout.splitlines() if line.startswith("RESULT ")), None)
        self.assertIsNotNone(line, stdout + stderr)
        result = json.loads(line[7:])
        self.assertEqual(0, result["code"])
        self.assertFalse(result["pyside"])
        self.assertFalse(result["shiboken"])
        return result

    def test_repeated_solo_archive_never_imports_qt_and_numbers_existing_output(self):
        source = self.root / "memo.txt"
        source.write_text("hello", encoding="utf-8")

        for _ in range(10):
            self._finish(self._start(source))

        archives = sorted(self.root.glob("memo*.zip"))
        self.assertEqual(10, len(archives))
        for archive in archives:
            with zipfile.ZipFile(archive) as opened:
                self.assertEqual(["memo.txt"], opened.namelist())
                self.assertEqual(b"hello", opened.read("memo.txt"))

    def test_repeated_three_process_selection_coalesces_into_one_archive_without_qt(self):
        for round_number in range(8):
            folder = self.root / f"round-{round_number}"
            folder.mkdir()
            sources = []
            for index in range(3):
                source = folder / f"f{index}.txt"
                source.write_text(f"round {round_number} file {index}", encoding="utf-8")
                sources.append(source)

            processes = [self._start(source) for source in sources]
            for process in processes:
                self._finish(process)

            archives = list(folder.glob("*.zip"))
            self.assertEqual(1, len(archives), archives)
            with zipfile.ZipFile(archives[0]) as opened:
                self.assertEqual(["f0.txt", "f1.txt", "f2.txt"], sorted(opened.namelist()))

    def test_stream_format_and_separate_mode_keep_each_source_filename(self):
        import appcore

        sources = [self.root / "a.txt", self.root / "b.txt"]
        for source in sources:
            source.write_text(source.name, encoding="utf-8")
        appcore.save_settings({"compress_format": "xz", "shell_multi": "separate"})
        seen = []

        def create(_format, group, target, **options):
            seen.append((list(group), target, options))
            return [target]

        progress = mock.Mock()
        cancel = mock.Mock(return_value=False)
        with mock.patch("formats.create", side_effect=create):
            outputs = appcore.shell_archive([str(source) for source in sources], progress, cancel)

        self.assertEqual([str(self.root / "a.txt.xz"), str(self.root / "b.txt.xz")], outputs)
        self.assertEqual([[str(sources[0])], [str(sources[1])]], [entry[0] for entry in seen])
        self.assertTrue(all(entry[2]["progress"] is progress for entry in seen))
        self.assertTrue(all(entry[2]["cancel"] is cancel for entry in seen))
        self.assertTrue(all(entry[2]["exclude"] == [".tmp", "Thumbs.db", ".DS_Store"] for entry in seen))


if __name__ == "__main__":
    unittest.main()

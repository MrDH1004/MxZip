from __future__ import annotations

import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

import version


ROOT = pathlib.Path(__file__).resolve().parent


class ReleaseVersionContractTests(unittest.TestCase):
    def test_101_is_the_single_release_version(self):
        self.assertEqual("1.0.1", version.__version__)
        notes = ROOT / ".github" / f"release-notes-{version.__version__}.md"
        self.assertTrue(notes.is_file(), notes)
        self.assertEqual(f"# MxZip {version.__version__}", notes.read_text(encoding="utf-8").splitlines()[0])

    def test_make_version_files_executes_from_version_py_into_temp_outputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            shutil.copy2(ROOT / "make_version_files.py", root)
            shutil.copy2(ROOT / "version_contract.py", root)
            (root / "version.py").write_text('__version__ = "9.8.7"\n', encoding="utf-8")
            result = subprocess.run(
                [sys.executable, "make_version_files.py"], cwd=root, capture_output=True, text=True,
                encoding="utf-8", check=True,
            )
            exe_resource = (root / "build" / "version_info.txt").read_text(encoding="utf-8")
            shell_resource = (root / "shell" / "build" / "version.h").read_text(encoding="ascii")

        self.assertEqual("9.8.7", result.stdout.strip())
        self.assertIn("filevers=(9, 8, 7, 0)", exe_resource)
        self.assertIn("prodvers=(9, 8, 7, 0)", exe_resource)
        self.assertIn("StringStruct('FileVersion', '9.8.7')", exe_resource)
        self.assertIn("StringStruct('ProductVersion', '9.8.7')", exe_resource)
        self.assertEqual('#define YZ_VER 9,8,7,0\n#define YZ_VER_STR "9.8.7"\n', shell_resource)

    def test_installer_contract_expands_app_version_and_output_filename(self):
        from version_contract import installer_setup_contract

        contract = installer_setup_contract(ROOT / "installer" / "MxZip.iss", version.__version__)
        self.assertEqual(version.__version__, contract["AppVersion"])
        self.assertEqual(f"MxZip {version.__version__}", contract["AppVerName"])
        self.assertEqual(version.__version__, contract["VersionInfoVersion"])
        self.assertEqual(version.__version__, contract["VersionInfoProductVersion"])
        self.assertEqual(f"MxZip-setup-{version.__version__}", contract["OutputBaseFilename"])

    def test_release_workflow_runs_version_contract_suite(self):
        workflow = (ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")
        tests_step = re.search(r"(?ms)^      - name: Tests\s+.*?^      - name:", workflow)
        self.assertIsNotNone(tests_step)
        modules = re.findall(r"\btest_[a-z0-9_]+\b", tests_step.group(0))
        self.assertIn("test_release_version", modules)

    def test_built_installer_filename_matches_version_when_present(self):
        installers = sorted((ROOT / "dist").glob("MxZip-setup-*.exe")) if (ROOT / "dist").is_dir() else []
        if not installers:
            self.skipTest("built installer is not present")
        self.assertIn(ROOT / "dist" / f"MxZip-setup-{version.__version__}.exe", installers)


if __name__ == "__main__":
    unittest.main()

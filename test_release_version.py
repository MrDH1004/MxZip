from __future__ import annotations

import pathlib
import unittest

import version


ROOT = pathlib.Path(__file__).resolve().parent


class ReleaseVersionContractTests(unittest.TestCase):
    def test_1022_is_the_single_release_version(self):
        self.assertEqual("1.0.22", version.__version__)
        notes = ROOT / ".github" / f"release-notes-{version.__version__}.md"
        self.assertTrue(notes.is_file(), notes)
        self.assertEqual(f"# yzip {version.__version__}", notes.read_text(encoding="utf-8").splitlines()[0])

    def test_build_installer_and_workflow_derive_version_from_version_py(self):
        build = (ROOT / "build.ps1").read_text(encoding="utf-8")
        installer = (ROOT / "installer" / "yzip.iss").read_text(encoding="utf-8")
        workflow = (ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")

        self.assertIn("$version = (python make_version_files.py).Trim()", build)
        self.assertIn('"/DAppVersion=$version"', build)
        self.assertIn("yzip-setup-$version.exe", build)
        self.assertIn("AppVersion={#AppVersion}", installer)
        self.assertIn("OutputBaseFilename=yzip-setup-{#AppVersion}", installer)
        self.assertIn("version.__version__ == '${{ github.ref_name }}'.lstrip('v')", workflow)
        self.assertIn('$notes = ".github\\release-notes-$v.md"', workflow)


if __name__ == "__main__":
    unittest.main()

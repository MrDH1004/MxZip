import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import packaged_native_smoke


ROOT = Path(__file__).resolve().parent
BINARIES = ("7z.exe", "7z.dll", "libarchive.dll", "yzip_native.dll", "zpaq.exe")
LICENSES = {
    "7-Zip License.txt": "7-Zip",
    "libarchive COPYING.txt": "libarchive",
    "libarchive-prebuilt LICENSE.txt": "libarchive",
    "ZPAQ COPYING.txt": "zpaq.cpp",
    "ZPAQ manual.pod": "zpaq.pod",
    "unegg LICENSE.txt": "MIT License",
    "unalz LICENSE.txt": "MIT License",
}


class NativePackageContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="yzip-package-contract-")
        self.tools = Path(self.temp.name)
        (self.tools / "licenses").mkdir()
        for name in BINARIES:
            (self.tools / name).write_bytes(b"binary")
        for name, marker in LICENSES.items():
            (self.tools / "licenses" / name).write_text(marker + "\n", encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def check_cache(self):
        return subprocess.run(
            [
                "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", str(ROOT / "fetch_tools.ps1"),
                "-ToolsDirectory", str(self.tools), "-CheckCacheOnly",
            ],
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",  # PowerShell's localized error text is in the ANSI code page (cp949 on Korean Windows)
            check=False,
        )

    def test_valid_cache_returns_without_download_work(self):
        result = self.check_cache()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("tools: present", result.stdout)
        self.assertFalse((self.tools / "7zip-src").exists())

    def test_binary_cache_requires_nonempty_leaf(self):
        target = self.tools / "7z.dll"
        for invalid in ("missing", "directory", "empty"):
            with self.subTest(invalid=invalid):
                if target.is_dir():
                    target.rmdir()
                else:
                    target.unlink(missing_ok=True)
                if invalid == "directory":
                    target.mkdir()
                elif invalid == "empty":
                    target.write_bytes(b"")
                result = self.check_cache()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("cache invalid", result.stdout + result.stderr)
                if target.is_dir():
                    target.rmdir()
                else:
                    target.unlink(missing_ok=True)
                target.write_bytes(b"binary")

    def test_license_cache_requires_present_nonempty_leaf_with_marker(self):
        target = self.tools / "licenses" / "7-Zip License.txt"
        for invalid in ("missing", "directory", "empty", "wrong-marker"):
            with self.subTest(invalid=invalid):
                if target.is_dir():
                    target.rmdir()
                else:
                    target.unlink(missing_ok=True)
                if invalid == "directory":
                    target.mkdir()
                elif invalid == "empty":
                    target.write_bytes(b"")
                elif invalid == "wrong-marker":
                    target.write_text("unrelated text", encoding="utf-8")
                result = self.check_cache()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("cache invalid", result.stdout + result.stderr)
                if target.is_dir():
                    target.rmdir()
                else:
                    target.unlink(missing_ok=True)
                target.write_text("7-Zip\n", encoding="utf-8")

    def test_staged_payload_validator_checks_manifest_runtime_and_license(self):
        runtime = self.tools / "7z.dll"
        runtime.write_bytes(b"pinned runtime")
        (self.tools / "yzip_accel.dll").write_bytes(b"bridge")
        digest = hashlib.sha256(runtime.read_bytes()).hexdigest()
        (self.tools / "7zip-pins.json").write_text(json.dumps({
            "binary_sha256": {"7z.dll": digest},
        }), encoding="utf-8")

        payload = packaged_native_smoke.validate_packaged_payload(self.tools)
        self.assertEqual(runtime, payload.runtime_dll)

        (self.tools / "licenses" / "7-Zip License.txt").write_bytes(b"")
        with self.assertRaisesRegex(RuntimeError, "license"):
            packaged_native_smoke.validate_packaged_payload(self.tools)

    def test_built_staged_smoke_subprocess_when_app_is_available(self):
        staged = ROOT / "dist" / "yzip" / "_internal" / "tools" / "yzip_accel.dll"
        if not staged.is_file():
            self.skipTest("run build.ps1 -Stage app before the staged smoke")
        result = subprocess.run(
            [sys.executable, str(ROOT / "packaged_native_smoke.py")],
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("packaged native ZIP smoke: OK", result.stdout)


if __name__ == "__main__":
    unittest.main()

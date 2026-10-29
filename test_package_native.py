import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent


class NativePackageContractTests(unittest.TestCase):
    def test_cached_fetch_requires_every_packaged_native_license(self):
        script = (ROOT / "fetch_tools.ps1").read_text(encoding="utf-8")
        need = next(line for line in script.splitlines() if line.startswith("$need ="))
        for required in (
            "licenses\\7-Zip License.txt",
            "licenses\\libarchive COPYING.txt",
            "licenses\\libarchive-prebuilt LICENSE.txt",
            "licenses\\ZPAQ COPYING.txt",
            "licenses\\ZPAQ manual.pod",
            "licenses\\unegg LICENSE.txt",
            "licenses\\unalz LICENSE.txt",
        ):
            self.assertIn(required, need)

    def test_build_packages_bridge_runtime_pins_and_licenses(self):
        script = (ROOT / "build.ps1").read_text(encoding="utf-8")
        for required in (
            "tools\\yzip_accel.dll;tools",
            "tools\\7z.dll;tools",
            "tools\\7zip-pins.json;tools",
            "tools\\licenses;tools\\licenses",
        ):
            self.assertIn(required, script)

    def test_packaged_smoke_covers_payload_abi_all_capabilities_and_kill_switch(self):
        script = (ROOT / "packaged_native_smoke.py").read_text(encoding="utf-8")
        for required in (
            "7-Zip License.txt",
            "ABI_VERSION",
            "CAP_ZIP_EXTRACT",
            "CAP_7Z_EXTRACT",
            "CAP_ZIP_CREATE",
            "YZIP_DISABLE_ACCEL",
            "subprocess.run",
        ):
            self.assertIn(required, script)


if __name__ == "__main__":
    unittest.main()

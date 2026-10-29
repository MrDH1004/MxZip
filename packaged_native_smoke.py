"""Exercise the packaged native ZIP bridge against its bundled pinned runtime without installing yzip."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest import mock
import zipfile
import zlib

import archive_accel


def main() -> int:
    tools = Path(__file__).resolve().parent / "dist" / "yzip" / "_internal" / "tools"
    accelerator_dll = tools / "yzip_accel.dll"
    runtime_dll = tools / "7z.dll"
    manifest_path = tools / "7zip-pins.json"
    license_path = tools / "licenses" / "7-Zip License.txt"
    for required in (accelerator_dll, runtime_dll, manifest_path, license_path):
        if not required.is_file():
            raise FileNotFoundError(f"packaged native payload is missing: {required}")
    if not license_path.read_text(encoding="utf-8", errors="replace").strip():
        raise RuntimeError("packaged 7-Zip license is empty")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    runtime_hash = hashlib.sha256(runtime_dll.read_bytes()).hexdigest()
    if runtime_hash != manifest["binary_sha256"]["7z.dll"]:
        raise RuntimeError("packaged 7z.dll does not match 7zip-pins.json")

    packaged_module = tools.parent / "archive_accel.py"
    with mock.patch.object(archive_accel, "__file__", str(packaged_module)), \
         mock.patch.object(sys, "frozen", True, create=True):
        archive_accel._runtime_7z_matches_pin.cache_clear()
        archive_accel._expected_7z_sha256.cache_clear()
        accelerator = archive_accel.load_accelerator()
        expected_capabilities = (
            archive_accel.CAP_ZIP_EXTRACT |
            archive_accel.CAP_7Z_EXTRACT |
            archive_accel.CAP_ZIP_CREATE
        )
        if accelerator is None or accelerator.abi_version != archive_accel.ABI_VERSION:
            raise RuntimeError(archive_accel.disabled_reason() or "packaged accelerator unavailable")
        if accelerator.capabilities & expected_capabilities != expected_capabilities:
            raise RuntimeError(f"packaged accelerator capability mismatch: {accelerator.capabilities:#x}")
        with tempfile.TemporaryDirectory(prefix="yzip-packaged-native-") as folder:
            root = Path(folder)
            source = root / "source.zip"
            staging = root / "payload.part"
            payload = b"packaged native payload" * 1000
            with zipfile.ZipFile(source, "w", zipfile.ZIP_DEFLATED) as archive:
                archive.writestr("payload.bin", payload)
            job = archive_accel.NativeZipJob(0, str(staging), len(payload), zlib.crc32(payload))
            result = accelerator.extract_zip(str(source), [job], None, None)
            if result.status != archive_accel.AccelStatus.OK:
                raise RuntimeError(
                    f"packaged native extraction failed: {result.status.name}: "
                    f"{accelerator.last_error()}"
                )
            if staging.read_bytes() != payload:
                raise RuntimeError("packaged native extraction content mismatch")

    environment = os.environ.copy()
    environment["YZIP_DISABLE_ACCEL"] = "1"
    disabled = subprocess.run(
        [
            sys.executable,
            "-c",
            "import archive_accel, pathlib; "
            f"assert archive_accel.load_accelerator(pathlib.Path({str(accelerator_dll)!r})) is None; "
            "assert 'YZIP_DISABLE_ACCEL=1' in archive_accel.disabled_reason()",
        ],
        cwd=Path(__file__).resolve().parent,
        env=environment,
        capture_output=True,
        text=True,
        check=False,
    )
    if disabled.returncode:
        raise RuntimeError(f"packaged kill-switch smoke failed: {disabled.stdout}{disabled.stderr}")
    print("packaged native ZIP smoke: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

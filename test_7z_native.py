import os
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from unittest import mock

import py7zr

import archive_accel
import formats


class Native7zBindingTests(unittest.TestCase):
    def test_capability_requires_both_7z_exports(self):
        class Function:
            def __init__(self, result=0):
                self.result = result

            def __call__(self, *args):
                return self.result

        class Library:
            yz_accel_abi_version = Function(archive_accel.ABI_VERSION)
            yz_accel_capabilities = Function(archive_accel.CAP_7Z_EXTRACT)
            yz_accel_last_error = Function(1)
            yz_accel_cancel = Function()

        with self.assertRaisesRegex(AttributeError, "yz_accel_inspect_7z"):
            archive_accel.Accelerator(Library(), Path("fake.dll"))


class Native7zIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.dll = Path("nativeaccel/build/yzip_accel.dll").resolve()
        if not self.dll.exists():
            self.fail("native DLL must be built before Task 4 integration tests")
        self.accel = archive_accel.load_accelerator(self.dll)
        self.assertIsNotNone(self.accel)

    def tearDown(self):
        archive_accel.load_accelerator.cache_clear() if hasattr(archive_accel.load_accelerator, "cache_clear") else None
        self.temp.cleanup()

    def make_archive(self, name="plain.7z", filters=None, password=None):
        source = self.root / "source"
        source.mkdir(exist_ok=True)
        (source / "a.txt").write_bytes(b"alpha" * 1000)
        (source / "empty.bin").write_bytes(b"")
        archive = self.root / name
        kwargs = {"mode": "w"}
        kwargs["filters"] = filters if filters is not None else [{"id": py7zr.FILTER_LZMA2}]
        if password is not None:
            kwargs["password"] = password
        with py7zr.SevenZipFile(archive, **kwargs) as out:
            out.write(source / "a.txt", "a.txt")
            out.write(source / "empty.bin", "empty.bin")
        return archive

    def test_plain_lzma2_inspection_and_selected_native_extract(self):
        archive = self.make_archive(filters=[{"id": py7zr.FILTER_LZMA2}])
        with py7zr.SevenZipFile(archive) as opened:
            crc = opened.list()[0].crc32
        info = self.accel.inspect_7z(archive)
        self.assertTrue(info.supported)
        self.assertFalse(info.encrypted)
        stage = self.root / "stage-a"
        job = archive_accel.Native7zJob(0, str(stage), 5000, crc)
        result = self.accel.extract_7z(archive, [job], None, None)
        self.assertEqual(archive_accel.AccelStatus.OK, result.status)
        self.assertEqual(b"alpha" * 1000, stage.read_bytes())

    def test_unsupported_coder_and_aes_are_rejected_before_writes(self):
        for archive in (
            self.make_archive("bzip2.7z", [{"id": py7zr.FILTER_BZIP2}]),
            self.make_archive("bcj.7z", [
                {"id": py7zr.FILTER_X86},
                {"id": py7zr.FILTER_LZMA2},
            ]),
            self.make_archive("aes.7z", [
                {"id": py7zr.FILTER_LZMA2},
                {"id": py7zr.FILTER_CRYPTO_AES256_SHA256},
            ], "secret"),
        ):
            info = self.accel.inspect_7z(archive)
            self.assertFalse(info.supported)
            stage = self.root / (archive.stem + ".stage")
            result = self.accel.extract_7z(
                archive, [archive_accel.Native7zJob(0, str(stage), 5000, 0)], None, None
            )
            self.assertEqual(archive_accel.AccelStatus.UNSUPPORTED, result.status)
            self.assertFalse(stage.exists())

    def test_formats_native_failure_preserves_existing_output(self):
        archive = self.make_archive()
        dest = self.root / "out"
        dest.mkdir()
        existing = dest / "a.txt"
        existing.write_bytes(b"old")
        fake = mock.Mock()
        fake.capabilities = archive_accel.CAP_7Z_EXTRACT
        fake.inspect_7z.return_value = archive_accel.NativeArchiveInfo(True, False, False, 2)
        fake.extract_7z.return_value = archive_accel.NativeResult(archive_accel.AccelStatus.CORRUPT, 0)
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
             mock.patch.object(formats, "load_accelerator", return_value=fake):
            with formats.SevenZArchive(str(archive)) as opened:
                with self.assertRaises(ValueError):
                    opened.extract(str(dest))
        self.assertEqual(b"old", existing.read_bytes())
        self.assertFalse(any("partial" in p.name for p in dest.iterdir()))

    def test_formats_selected_member_uses_native_and_publishes_after_validation(self):
        archive = self.make_archive()
        dest = self.root / "selected"
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
             mock.patch.object(formats, "load_accelerator", return_value=self.accel):
            with formats.SevenZArchive(str(archive)) as opened:
                opened.extract(str(dest), names=["a.txt"])
        self.assertEqual(b"alpha" * 1000, (dest / "a.txt").read_bytes())
        self.assertFalse((dest / "empty.bin").exists())

    def test_native_cancel_and_size_limit_leave_no_staging_output(self):
        archive = self.make_archive()
        stage = self.root / "cancelled.stage"
        cancelled = self.accel.extract_7z(
            archive,
            [archive_accel.Native7zJob(0, str(stage), 5000, 0)],
            None,
            lambda: True,
        )
        self.assertEqual(archive_accel.AccelStatus.CANCELLED, cancelled.status)
        self.assertFalse(stage.exists())
        limited = self.accel.extract_7z(
            archive,
            [archive_accel.Native7zJob(0, str(stage), 1, 0)],
            None,
            None,
        )
        self.assertEqual(archive_accel.AccelStatus.CORRUPT, limited.status)

    def test_unsupported_after_a_staging_file_exists_never_falls_back(self):
        archive = self.make_archive()
        dest = self.root / "partial"
        fake = mock.Mock(capabilities=archive_accel.CAP_7Z_EXTRACT)
        fake.inspect_7z.return_value = archive_accel.NativeArchiveInfo(True, False, False, 2)

        def partial(_source, jobs, _progress, _cancel):
            Path(jobs[0].staging_path).parent.mkdir(parents=True, exist_ok=True)
            Path(jobs[0].staging_path).write_bytes(b"partial")
            return archive_accel.NativeResult(archive_accel.AccelStatus.UNSUPPORTED, 7)

        fake.extract_7z.side_effect = partial
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
             mock.patch.object(formats, "load_accelerator", return_value=fake):
            with formats.SevenZArchive(str(archive)) as opened:
                with self.assertRaises(RuntimeError):
                    opened.extract(str(dest))
        self.assertFalse((dest / "a.txt").exists())
        self.assertFalse(any(dest.rglob("*.partial")))

    def test_corrupt_archive_does_not_replace_existing_output(self):
        archive = self.make_archive()
        data = bytearray(archive.read_bytes())
        data[len(data) // 2] ^= 0x5A
        archive.write_bytes(data)
        dest = self.root / "corrupt-out"
        dest.mkdir()
        (dest / "a.txt").write_bytes(b"old")
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
             mock.patch.object(formats, "load_accelerator", return_value=self.accel):
            with self.assertRaises(Exception):
                with formats.SevenZArchive(str(archive)) as opened:
                    opened.extract(str(dest))
        self.assertEqual(b"old", (dest / "a.txt").read_bytes())

    def test_duplicate_and_symlink_metadata_select_whole_python_fallback(self):
        archive = self.make_archive()
        for mutate in (
            lambda infos: [infos[0], replace(infos[1], filename=infos[0].filename)],
            lambda infos: [replace(infos[0], is_file=False, is_symlink=True), infos[1]],
        ):
            fake = mock.Mock(capabilities=archive_accel.CAP_7Z_EXTRACT)
            with formats.SevenZArchive(str(archive)) as opened:
                opened.infos = mutate(opened.infos)
                with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
                     mock.patch.object(formats, "load_accelerator", return_value=fake), \
                     mock.patch.object(opened, "_run", side_effect=RuntimeError("python fallback")):
                    with self.assertRaisesRegex(RuntimeError, "python fallback"):
                        opened.extract(str(self.root / "fallback"))
            fake.inspect_7z.assert_not_called()

    def test_native_publish_supports_long_destination_path(self):
        archive = self.make_archive()
        dest = self.root
        while len(str(dest)) < 275:
            dest = dest / ("long-segment-" + "x" * 24)
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
             mock.patch.object(formats, "load_accelerator", return_value=self.accel):
            with formats.SevenZArchive(str(archive)) as opened:
                opened.extract(str(dest), names=["a.txt"])
        self.assertEqual(b"alpha" * 1000, (dest / "a.txt").read_bytes())

    def test_native_extracts_nested_member_and_empty_directory(self):
        source = self.root / "tree-source"
        (source / "nested").mkdir(parents=True)
        (source / "nested" / "data.txt").write_bytes(b"nested-data")
        (source / "empty").mkdir()
        archive = self.root / "tree.7z"
        with py7zr.SevenZipFile(archive, "w", filters=[{"id": py7zr.FILTER_LZMA2}]) as out:
            out.writeall(source, "")
        dest = self.root / "tree-out"
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_7Z_EXTRACT": "1"}), \
             mock.patch.object(formats, "load_accelerator", return_value=self.accel):
            with formats.SevenZArchive(str(archive)) as opened:
                opened.extract(str(dest))
        self.assertEqual(b"nested-data", (dest / "nested" / "data.txt").read_bytes())
        self.assertTrue((dest / "empty").is_dir())


if __name__ == "__main__":
    unittest.main()

import importlib.util
import json
import os
import tempfile
from types import SimpleNamespace
import unittest
import zipfile
from pathlib import Path
from unittest import mock

import archive_accel
import ziplib
import zipwriter


class RecordingZipCreator:
    capabilities = getattr(archive_accel, "CAP_ZIP_CREATE", 0)

    def __init__(self, status=archive_accel.AccelStatus.OK):
        self.status = status
        self.calls = []

    def create_zip(self, items, output_part, level, threads, progress, cancel):
        items = list(items)
        self.calls.append((items, output_part, level, threads))
        if self.status != archive_accel.AccelStatus.OK:
            Path(output_part).write_bytes(b"partial native output")
            return archive_accel.NativeResult(self.status, 0)
        compression = zipfile.ZIP_STORED if level == 0 else zipfile.ZIP_DEFLATED
        with zipfile.ZipFile(output_part, "w", compression=compression, allowZip64=True) as archive:
            for item in items:
                if item.is_dir:
                    archive.writestr(item.archive_name.rstrip("/") + "/", b"")
                else:
                    archive.write(item.source_path, item.archive_name)
        completed = sum(item.size for item in items if not item.is_dir)
        if progress:
            progress(completed, completed, "")
        return archive_accel.NativeResult(self.status, completed)

    def last_error(self):
        return "injected native create failure"


class CorruptSuccessfulCreator(RecordingZipCreator):
    def create_zip(self, items, output_part, level, threads, progress, cancel):
        items = list(items)
        self.calls.append((items, output_part, level, threads))
        Path(output_part).write_bytes(b"not a ZIP despite OK status")
        return archive_accel.NativeResult(archive_accel.AccelStatus.OK, 0)


class NativeZipCompressionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.source.mkdir()
        (self.source / "한글.txt").write_text("native zip payload", encoding="utf-8")
        (self.source / "empty.bin").write_bytes(b"")
        (self.source / "empty-dir").mkdir()
        self.output = self.root / "result.zip"

    def tearDown(self):
        self.temp.cleanup()

    def _create(self, accelerator, **kwargs):
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}, clear=False), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
            return ziplib.create_zip([str(self.source)], str(self.output), level=5, **kwargs)

    def test_plain_new_archive_uses_native_and_cross_reads_with_python(self):
        accelerator = RecordingZipCreator()

        self._create(accelerator)

        self.assertEqual(1, len(accelerator.calls))
        items, output_part, level, threads = accelerator.calls[0]
        self.assertNotEqual(os.path.realpath(self.output), os.path.realpath(output_part))
        self.assertIn(".part", Path(output_part).name)
        self.assertEqual(5, level)
        self.assertGreaterEqual(threads, 1)
        self.assertEqual(
            ["source/", "source/empty-dir/", "source/empty.bin", "source/한글.txt"],
            sorted(item.archive_name for item in items),
        )
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(b"native zip payload", archive.read("source/한글.txt"))
            self.assertEqual(b"", archive.read("source/empty.bin"))
            self.assertIn("source/empty-dir/", archive.namelist())

    def test_native_is_opt_in_until_benchmark_gate_passes(self):
        accelerator = RecordingZipCreator()
        with mock.patch.dict(os.environ, {}, clear=False), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
            os.environ.pop("YZIP_NATIVE_ZIP_CREATE", None)
            ziplib.create_zip([str(self.source)], str(self.output), level=5)
        self.assertEqual([], accelerator.calls)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(b"native zip payload", archive.read("source/한글.txt"))

    def test_global_kill_switch_overrides_native_create_opt_in(self):
        with mock.patch.dict(os.environ, {
            "YZIP_NATIVE_ZIP_CREATE": "1", "YZIP_DISABLE_ACCEL": "1",
        }, clear=False), mock.patch.object(
            zipwriter, "load_accelerator", wraps=archive_accel.load_accelerator
        ), mock.patch.object(archive_accel.ctypes, "WinDLL") as load:
            ziplib.create_zip([str(self.source)], str(self.output), level=5)
        load.assert_not_called()
        with zipfile.ZipFile(self.output) as archive:
            self.assertTrue(archive.namelist())

    def test_aes_copy_and_split_use_existing_writer_for_whole_archive(self):
        accelerator = RecordingZipCreator()
        self._create(accelerator, password="secret")
        self.assertEqual([], accelerator.calls)

        self.output.unlink()
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}, clear=False), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
            parts = ziplib.create_zip(
                [str(self.source)], str(self.output), level=5, split_size=256
            )
        self.assertEqual([], accelerator.calls)
        self.assertGreater(len(parts), 1)

        existing = self.root / "existing.zip"
        ziplib.create_zip([str(self.source / "한글.txt")], str(existing))
        with zipfile.ZipFile(existing) as opened:
            keep = [(opened.infolist()[0], "renamed.txt")]
            part = self.root / "copy.part"
            with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}, clear=False), \
                 mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
                zipwriter.write_zip([], str(part), copy=(str(existing), keep))
        self.assertEqual([], accelerator.calls)

    def test_unavailable_accelerator_falls_back_before_output(self):
        accelerator = RecordingZipCreator(archive_accel.AccelStatus.UNAVAILABLE)
        self._create(accelerator)
        self.assertEqual(1, len(accelerator.calls))
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(b"native zip payload", archive.read("source/한글.txt"))

    def test_empty_archive_uses_existing_writer(self):
        accelerator = RecordingZipCreator()
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
            zipwriter.write_zip([], str(self.output), level=5)
        self.assertEqual([], accelerator.calls)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual([], archive.infolist())

    def test_cancel_error_and_corrupt_result_preserve_existing_archive(self):
        for status in (
            archive_accel.AccelStatus.CANCELLED,
            archive_accel.AccelStatus.ERROR,
            archive_accel.AccelStatus.CORRUPT,
            archive_accel.AccelStatus.IO_ERROR,
        ):
            with self.subTest(status=status):
                self.output.write_bytes(b"existing archive")
                accelerator = RecordingZipCreator(status)
                with self.assertRaises(Exception):
                    self._create(accelerator)
                self.assertEqual(b"existing archive", self.output.read_bytes())
                self.assertFalse(Path(str(self.output) + ".partial").exists())

    def test_output_publish_failure_preserves_existing_archive_and_removes_part(self):
        self.output.write_bytes(b"existing archive")
        accelerator = RecordingZipCreator()
        real_replace = ziplib.os.replace

        def fail_publish(source, target):
            if os.path.realpath(target) == os.path.realpath(self.output):
                raise PermissionError("injected publish failure")
            return real_replace(source, target)

        with mock.patch.object(ziplib.os, "replace", side_effect=fail_publish):
            with self.assertRaisesRegex(PermissionError, "publish failure"):
                self._create(accelerator)
        self.assertEqual(b"existing archive", self.output.read_bytes())
        self.assertFalse(Path(str(self.output) + ".partial").exists())

    def test_ok_status_with_corrupt_output_is_rejected_before_publish(self):
        self.output.write_bytes(b"existing archive")
        accelerator = CorruptSuccessfulCreator()
        with self.assertRaises((ValueError, zipfile.BadZipFile)):
            self._create(accelerator)
        self.assertEqual(b"existing archive", self.output.read_bytes())
        self.assertFalse(Path(str(self.output) + ".partial").exists())

    def test_same_size_same_mtime_content_swap_is_rejected_before_publish(self):
        source = self.source / "한글.txt"
        original = source.read_bytes()
        replacement = b"X" * len(original)
        original_mtime = source.stat().st_mtime_ns
        self.output.write_bytes(b"existing archive")
        accelerator = RecordingZipCreator()

        def mutate_after_snapshot():
            source.write_bytes(replacement)
            os.utime(source, ns=(original_mtime, original_mtime))
            return accelerator

        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}), \
             mock.patch.object(zipwriter, "load_accelerator", side_effect=mutate_after_snapshot):
            with self.assertRaisesRegex(ValueError, "changed"):
                ziplib.create_zip([str(self.source)], str(self.output), level=5)
        self.assertEqual(b"existing archive", self.output.read_bytes())
        self.assertFalse(Path(str(self.output) + ".partial").exists())

    def test_zip64_boundary_is_whole_operation_python_fallback(self):
        accelerator = RecordingZipCreator()
        source = self.source / "한글.txt"
        stat = source.stat()
        huge = SimpleNamespace(
            st_size=zipwriter.ZIP64_LIMIT,
            st_mode=stat.st_mode,
            st_mtime_ns=stat.st_mtime_ns,
        )
        metadata = [(str(source), "huge.bin", huge)]
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
            selected = zipwriter._try_native_zip(
                metadata, str(self.output), 5, 1, None, None, None, None, True
            )
        self.assertFalse(selected)
        self.assertEqual([], accelerator.calls)

    def test_symlink_input_uses_existing_writer_policy(self):
        target = self.root / "target.txt"
        target.write_bytes(b"target")
        link = self.root / "link.txt"
        try:
            os.symlink(target, link)
        except OSError as error:
            self.skipTest(f"symlink unavailable: {error}")
        accelerator = RecordingZipCreator()
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}, clear=False), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=accelerator):
            ziplib.create_zip([str(link)], str(self.output), level=5)
        self.assertEqual([], accelerator.calls)


class NativeZipCreateBindingTests(unittest.TestCase):
    def test_capability_requires_create_export(self):
        class Function:
            def __init__(self, value=0):
                self.value = value
                self.argtypes = None
                self.restype = None

            def __call__(self, *args):
                return self.value

        class Library:
            yz_accel_abi_version = Function(archive_accel.ABI_VERSION)
            yz_accel_capabilities = Function(getattr(archive_accel, "CAP_ZIP_CREATE", 1 << 2))
            yz_accel_last_error = Function(1)
            yz_accel_cancel = Function()

        with self.assertRaisesRegex(AttributeError, "yz_accel_create_zip"):
            archive_accel.Accelerator(Library(), Path("fake.dll"))


class BuiltNativeZipCreateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dll = Path("nativeaccel/build/yzip_accel.dll").resolve()
        if not cls.dll.exists():
            raise unittest.SkipTest("native DLL must be built")
        cls.accelerator = archive_accel.load_accelerator(cls.dll)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()
        archive_accel._reset_for_tests()

    def _item(self, path, name, *, size=None, is_dir=False):
        stat = path.stat()
        return archive_accel.NativeZipCreateItem(
            str(path), name, stat.st_size if size is None else size,
            stat.st_mtime_ns, (stat.st_mode & 0xFFFF) << 16, is_dir,
        )

    def test_real_bridge_creates_store_and_deflate_zip_with_unicode_and_directories(self):
        source = self.root / "자료.txt"
        source.write_bytes(("압축 자료\n" * 1000).encode())
        empty = self.root / "empty.bin"
        empty.write_bytes(b"")
        directory = self.root / "빈폴더"
        directory.mkdir()
        for level in (0, 5):
            with self.subTest(level=level):
                output = self.root / f"level-{level}.zip"
                items = [
                    self._item(directory, "빈폴더/", is_dir=True),
                    self._item(empty, "empty.bin"),
                    self._item(source, "하위/자료.txt"),
                ]
                result = self.accelerator.create_zip(items, output, level, 8, None, None)
                self.assertEqual(archive_accel.AccelStatus.OK, result.status)
                with zipfile.ZipFile(output) as archive:
                    self.assertEqual(source.read_bytes(), archive.read("하위/자료.txt"))
                    self.assertEqual(b"", archive.read("empty.bin"))
                    self.assertIn("빈폴더/", archive.namelist())
                    self.assertEqual([], archive.testzip() and [archive.testzip()] or [])

    def test_declared_size_mismatch_is_corrupt_and_does_not_publish_valid_zip(self):
        source = self.root / "changing.bin"
        source.write_bytes(b"original")
        output = self.root / "changed.zip"
        result = self.accelerator.create_zip(
            [self._item(source, "changing.bin", size=(1 << 32) + 8)],
            output, 5, 1, None, None,
        )
        self.assertEqual(archive_accel.AccelStatus.CORRUPT, result.status)

    def test_cancel_before_create_leaves_no_output(self):
        source = self.root / "cancel.bin"
        source.write_bytes(b"cancel" * 1000)
        output = self.root / "cancel.zip"
        result = self.accelerator.create_zip(
            [self._item(source, "cancel.bin")], output, 5, 1, None, lambda: True,
        )
        self.assertEqual(archive_accel.AccelStatus.CANCELLED, result.status)
        self.assertFalse(output.exists())

    def test_native_output_limit_is_enforced_and_partial_is_removed(self):
        source = self.root / "limit.bin"
        source.write_bytes(os.urandom(64 * 1024))
        output = self.root / "limited.zip"
        with mock.patch.dict(os.environ, {"YZIP_NATIVE_TEST_CREATE_LIMIT": "32"}):
            result = self.accelerator.create_zip(
                [self._item(source, "limit.bin")], output, 5, 1, None, None,
            )
        self.assertEqual(archive_accel.AccelStatus.IO_ERROR, result.status)
        self.assertFalse(output.exists())

    def test_source_mutation_and_read_failure_are_corrupt_and_remove_output_part(self):
        source = self.root / "source.bin"
        source.write_bytes(b"first payload")
        item = self._item(source, "source.bin")
        source.write_bytes(b"mutated payload with a different size")
        output = self.root / "mutated.zip"
        result = self.accelerator.create_zip([item], output, 5, 1, None, None)
        self.assertEqual(archive_accel.AccelStatus.CORRUPT, result.status)
        self.assertFalse(output.exists())

    def test_real_dll_same_size_same_mtime_mutation_fails_strong_verification(self):
        source = self.root / "same-size.bin"
        source.write_bytes(b"original")
        original_mtime = source.stat().st_mtime_ns
        metadata = [(str(source), "same-size.bin", source.stat())]
        output = self.root / "same-size.zip"
        accelerator = self.accelerator

        class MutatingAccelerator:
            capabilities = accelerator.capabilities

            @staticmethod
            def create_zip(*args, **kwargs):
                source.write_bytes(b"replaced")
                os.utime(source, ns=(original_mtime, original_mtime))
                return accelerator.create_zip(*args, **kwargs)

            @staticmethod
            def last_error():
                return accelerator.last_error()

        with mock.patch.dict(os.environ, {"YZIP_NATIVE_ZIP_CREATE": "1"}), \
             mock.patch.object(zipwriter, "load_accelerator", return_value=MutatingAccelerator()):
            with self.assertRaisesRegex(ValueError, "changed"):
                zipwriter._try_native_zip(
                    metadata, str(output), 5, 1, None, None, None, None, True
                )

        missing_item = self._item(source, "missing.bin")
        source.unlink()
        output = self.root / "missing.zip"
        result = self.accelerator.create_zip([missing_item], output, 5, 1, None, None)
        self.assertEqual(archive_accel.AccelStatus.CORRUPT, result.status)
        self.assertFalse(output.exists())

    def test_duplicate_names_long_unicode_path_timestamp_and_permissions_round_trip(self):
        first = self.root / "first.txt"
        second = self.root / "second.txt"
        first.write_bytes(b"first")
        second.write_bytes(b"second")
        os.chmod(second, 0o600)
        long_name = "긴이름-" + ("x" * 260) + ".txt"
        output = self.root / "metadata.zip"
        items = [
            self._item(first, "duplicate.txt"),
            self._item(second, "duplicate.txt"),
            self._item(second, long_name),
        ]
        result = self.accelerator.create_zip(items, output, 5, 8, None, None)
        self.assertEqual(archive_accel.AccelStatus.OK, result.status)
        zipwriter._normalize_native_zip_metadata(
            output,
            [(item.source_path, item.archive_name, Path(item.source_path).stat())
             for item in items],
        )
        with zipfile.ZipFile(output) as archive:
            duplicates = [info for info in archive.infolist() if info.filename == "duplicate.txt"]
            self.assertEqual(2, len(duplicates))
            self.assertEqual(b"second", archive.read(duplicates[-1]))
            self.assertEqual(b"second", archive.read(long_name))
            self.assertEqual(3, duplicates[-1].create_system)
            self.assertEqual(0o100644, duplicates[-1].external_attr >> 16)
            self.assertGreaterEqual(duplicates[-1].date_time[0], 1980)

    def test_directory_metadata_matches_existing_writer_policy(self):
        directory = self.root / "directory"
        directory.mkdir()
        output = self.root / "directory.zip"
        result = self.accelerator.create_zip(
            [self._item(directory, "directory/", is_dir=True)], output, 5, 1, None, None
        )
        self.assertEqual(archive_accel.AccelStatus.OK, result.status)
        zipwriter._normalize_native_zip_metadata(output, [(str(directory), "directory/", directory.stat())])
        with zipfile.ZipFile(output) as archive:
            info = archive.getinfo("directory/")
            self.assertEqual(3, info.create_system)
            self.assertEqual((0o40755 << 16) | 0x10, info.external_attr)

    def test_noexcept_error_buffer_survives_repeated_invalid_calls(self):
        library = self.accelerator._library
        for _ in range(100):
            status = library.yz_accel_create_zip(None, None, 0, None, 5, 1, 1, None, None)
            self.assertEqual(archive_accel.AccelStatus.ERROR, status)
            self.assertIn("invalid", self.accelerator.last_error())
            library.yz_accel_clear_error()
            self.assertEqual("", self.accelerator.last_error())


class Task5BenchmarkEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(".superpowers/sdd/2026-10-28-native-performance-hybrid/task5_bench.py")
        spec = importlib.util.spec_from_file_location("task5_bench", path)
        cls.benchmark = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.benchmark)

    def test_report_requires_run_hashes_and_native_selection(self):
        valid_run = {
            "seconds": 1.0,
            "archive_size": 10,
            "source_tree_sha256": "a" * 64,
            "destination_tree_sha256": "a" * 64,
            "actual_selection": "native",
        }
        native_run = dict(valid_run)
        python_run = dict(valid_run, actual_selection="python")
        bandizip_run = dict(valid_run, actual_selection="bandizip")
        report = {"results": [{"tools": {
            "native-yzip": {"runs": [native_run]},
            "existing-yzip": {"runs": [python_run]},
            "bandizip": {"runs": [bandizip_run]},
        }}]}
        self.benchmark.validate_evidence(report)
        fallback = json.loads(json.dumps(report))
        fallback["results"][0]["tools"]["native-yzip"]["runs"][0]["actual_selection"] = "python"
        with self.assertRaisesRegex(RuntimeError, "fallback"):
            self.benchmark.validate_evidence(fallback)


if __name__ == "__main__":
    unittest.main()

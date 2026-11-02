import hashlib
import io
import json
import sys
import tempfile
import unittest
import zlib
from pathlib import Path
from unittest import mock

import benchmark_native as benchmark


class BenchmarkNativeTests(unittest.TestCase):
    def setUp(self):
        # the reference CLI comes from MXZIP_REF_ARCHIVER; commands are mocked here, so any existing file will do
        patcher = mock.patch.object(benchmark, "BZ_EXE", Path(sys.executable))
        patcher.start()
        self.addCleanup(patcher.stop)

    def _case(self, **overrides):
        values = dict(
            dataset="large-text", tool="reference", format="zip", operation="compress",
            threads=4, level=5, source=Path("source"), archive=Path("archive.zip"),
        )
        values.update(overrides)
        return benchmark.BenchmarkCase(**values)

    def test_expand_cases_is_deterministic(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            datasets = {
                name: root / name
                for name in ("large-text", "incompressible", "small-files", "mixed-project", "large-file")
            }
            cases = benchmark.expand_cases(datasets, root / "archives")

        self.assertEqual(
            [(case.dataset, case.tool, case.format, case.operation, case.threads) for case in cases],
            [
                (dataset, tool, archive_format, operation, threads)
                for dataset in datasets
                for tool in ("reference", "MxZip")
                for archive_format in ("zip", "7z")
                for operation in ("compress", "extract")
                for threads in (1, 4, 8, 16)
            ],
        )
        self.assertEqual(len(cases), 160)
        self.assertTrue(all(case.level == 5 for case in cases))

    def test_make_datasets_creates_all_required_independent_classes(self):
        with tempfile.TemporaryDirectory() as temp:
            datasets = benchmark.make_datasets(Path(temp), project_source=benchmark.ROOT)

            self.assertEqual(
                list(datasets),
                ["large-text", "incompressible", "small-files", "mixed-project", "large-file"],
            )
            self.assertGreaterEqual(sum(1 for path in datasets["small-files"].rglob("*") if path.is_file()), 1500)
            incompressible = (datasets["incompressible"] / "deterministic.bin").read_bytes()
            self.assertGreater(len(zlib.compress(incompressible, level=9)) / len(incompressible), 0.99)
            self.assertTrue((datasets["mixed-project"] / "mxzip_cli.py").is_file())
            self.assertEqual(sum(path.stat().st_size for path in datasets["large-file"].rglob("*") if path.is_file()),
                             benchmark.LARGE_FILE_BYTES)

    def test_run_case_excludes_warmup_and_calculates_median_and_p95(self):
        case = self._case()
        samples = [
            benchmark.ProcessSample(99.0, 99.0, 99, 0, tree_sha256="a" * 64),
            benchmark.ProcessSample(1.0, 10.0, 100, 0, tree_sha256="a" * 64),
            benchmark.ProcessSample(2.0, 20.0, 200, 0, tree_sha256="a" * 64),
            benchmark.ProcessSample(3.0, 30.0, 300, 0, tree_sha256="a" * 64),
            benchmark.ProcessSample(4.0, 40.0, 400, 0, tree_sha256="a" * 64),
            benchmark.ProcessSample(100.0, 50.0, 500, 0, tree_sha256="a" * 64),
        ]
        with mock.patch.object(benchmark, "_run_iteration", side_effect=samples) as run:
            with mock.patch.object(benchmark, "tree_sha256", return_value="a" * 64):
                result = benchmark.run_case(case, runs=5)

        self.assertEqual(run.call_count, 6)
        self.assertEqual(result.wall_median_seconds, 3.0)
        self.assertEqual(result.wall_p95_seconds, 100.0)
        self.assertEqual(result.cpu_median_seconds, 30.0)
        self.assertEqual(result.peak_rss_bytes, 500)

    def test_commands_propagate_requested_threads(self):
        reference = benchmark.build_command(self._case(tool="reference", format="7z", threads=8), Path("out"))
        mxzip = benchmark.build_command(self._case(tool="MxZip", format="7z", threads=16), Path("out"))

        self.assertIn("-t:8", reference)
        self.assertIn("runtimeconfig.configure(16", mxzip[2])

    def test_run_case_fails_when_extracted_tree_hash_mismatches_source(self):
        case = self._case(tool="MxZip", operation="extract", threads=1)
        with mock.patch.object(
            benchmark, "_run_iteration",
            return_value=benchmark.ProcessSample(1.0, 1.0, 1, 0, tree_sha256="different-hash"),
        ):
            with mock.patch.object(benchmark, "tree_sha256", return_value="source-hash"):
                with self.assertRaisesRegex(RuntimeError, "SHA-256 tree mismatch"):
                    benchmark.run_case(case, runs=1)

    def test_run_case_verifies_warmup_hash_before_discarding_timing(self):
        case = self._case(operation="extract")
        corrupt_warmup = benchmark.ProcessSample(99.0, 99.0, 99, 0, tree_sha256="corrupt")
        measured = benchmark.ProcessSample(1.0, 1.0, 1, 0, tree_sha256="source-hash")
        with mock.patch.object(benchmark, "_run_iteration", side_effect=[corrupt_warmup, measured]):
            with mock.patch.object(benchmark, "tree_sha256", return_value="source-hash"):
                with self.assertRaisesRegex(RuntimeError, "warm-up SHA-256 tree mismatch"):
                    benchmark.run_case(case, runs=1)

    def test_compression_cross_extracts_with_opposite_tool_and_rejects_corrupt_result(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "source"
            source.mkdir()
            (source / "payload.txt").write_text("expected", encoding="utf-8")
            case = self._case(tool="MxZip", source=source)
            archive = root / "result.zip"
            archive.write_bytes(b"not-a-real-archive")

            def fake_extract(command):
                self.assertEqual(command[0], str(benchmark.BZ_EXE))
                extracted = root / "cross-extracted" / source.name
                extracted.mkdir(parents=True)
                (extracted / "payload.txt").write_text("corrupt", encoding="utf-8")

            with mock.patch.object(benchmark, "_run_unmeasured", side_effect=fake_extract):
                with self.assertRaisesRegex(RuntimeError, "cross-extraction SHA-256 mismatch"):
                    benchmark.verify_generated_archive(case, archive, root / "cross-extracted")

    def test_reference_compression_cross_extracts_with_mxzip(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "source"
            source.mkdir()
            (source / "payload.txt").write_text("expected", encoding="utf-8")
            destination = root / "cross-extracted"
            case = self._case(tool="reference", source=source)

            def fake_extract(command):
                self.assertEqual(command[0], benchmark.sys.executable)
                extracted = destination / source.name
                extracted.mkdir(parents=True)
                (extracted / "payload.txt").write_text("expected", encoding="utf-8")

            with mock.patch.object(benchmark, "_run_unmeasured", side_effect=fake_extract):
                self.assertEqual(
                    benchmark.verify_generated_archive(case, root / "result.zip", destination),
                    benchmark.tree_sha256(source),
                )

    def test_json_includes_dataset_dimension(self):
        result = benchmark.BenchmarkResult(
            case=self._case(dataset="small-files"), measured_runs=7,
            wall_median_seconds=1.0, wall_p95_seconds=2.0, cpu_median_seconds=0.5,
            peak_rss_bytes=10, archive_size_bytes=20, requested_threads=4,
            command=("tool",), sha256_tree="hash",
        )
        self.assertEqual(result.to_json()["case"]["dataset"], "small-files")

    def test_7z_gate_common_mxzip_and_reference_slowdown_cancels(self):
        current = {"large-text": {"MxZip": 12.0, "reference": 6.0}}
        baseline = {"large-text": {"MxZip": 10.0, "reference": 5.0}}
        regression = benchmark.enforce_7z_regression_gate(current, baseline)
        self.assertAlmostEqual(regression["large-text"], 0.0)

    def test_7z_gate_rejects_mxzip_only_slowdown_over_five_percent(self):
        current = {"large-text": {"MxZip": 10.5001, "reference": 5.0}}
        baseline = {"large-text": {"MxZip": 10.0, "reference": 5.0}}
        with self.assertRaisesRegex(RuntimeError, "large-text.*5.00%"):
            benchmark.enforce_7z_regression_gate(current, baseline)

    def _gate_result(self, median, dataset="large-text", tool="MxZip"):
        return benchmark.BenchmarkResult(
            case=self._case(dataset=dataset, tool=tool, format="7z", operation="compress", threads=8),
            measured_runs=7, wall_median_seconds=median, wall_p95_seconds=median,
            cpu_median_seconds=median, peak_rss_bytes=1, archive_size_bytes=1,
            requested_threads=8, command=("tool",), sha256_tree="hash",
        )

    def _baseline_summary(self, mxzip_medians=None, reference_medians=None):
        if mxzip_medians is None:
            mxzip_medians = {name: 10.0 for name in benchmark.DATASET_NAMES}
        if reference_medians is None:
            reference_medians = {name: 5.0 for name in benchmark.DATASET_NAMES}
        return {
            "schema_version": 1,
            "regression_methodology": "paired-MxZip-over-reference-ratio-v1",
            "seven_zip_8_thread_medians_seconds": mxzip_medians,
            "reference_seven_zip_8_thread_medians_seconds": reference_medians,
            "seven_zip_8_thread_mxzip_reference_ratios": {
                name: mxzip_medians[name] / reference_medians[name]
                for name in mxzip_medians if name in reference_medians
            },
        }

    def _paired_results(self, mxzip=10.0, reference=5.0):
        results = []
        for name in benchmark.DATASET_NAMES:
            results.append(self._gate_result(reference, name, "reference"))
            results.append(self._gate_result(mxzip, name, "MxZip"))
        return results

    def test_paired_medians_reject_missing_or_duplicate_tool_cases(self):
        complete = self._paired_results()
        missing = complete[1:]
        duplicate = complete + [self._gate_result(10.0, "large-text", "MxZip")]
        for label, results in (("missing", missing), ("duplicate", duplicate)):
            with self.subTest(label=label), self.assertRaisesRegex(ValueError, label):
                benchmark._paired_7z_medians(results)

    def test_evaluate_existing_never_runs_matrix_and_preserves_input(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            source = root / "source.json"
            output = root / "evaluated.json"
            summary = root / "evaluated-summary.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            source_document = {
                "schema_version": 2,
                "runs": 7,
                "warmup_runs": 1,
                "dataset_count": 5,
                "case_count": 10,
                "results": [result.to_json() for result in self._paired_results()],
            }
            source.write_text(json.dumps(source_document, indent=2), encoding="utf-8")
            original = source.read_bytes()
            with mock.patch.object(benchmark, "_execute_matrix") as execute:
                code = benchmark.cli([
                    "--evaluate-existing", str(source),
                    "--output", str(output), "--summary", str(summary),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(0, code)
            execute.assert_not_called()
            self.assertEqual(original, source.read_bytes())
            evaluated = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(source_document["results"], evaluated["results"])
            self.assertEqual(
                hashlib.sha256(original).hexdigest(),
                evaluated["evaluation_provenance"]["input_raw_sha256"],
            )
            self.assertEqual("paired-MxZip-over-reference-ratio-v1",
                             evaluated["regression_methodology"])

    def test_evaluate_existing_missing_pair_fails_before_publishing(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            source = root / "source.json"
            output = root / "evaluated.json"
            summary = root / "evaluated-summary.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            results = [result.to_json() for result in self._paired_results()][1:]
            source.write_text(json.dumps({
                "schema_version": 2, "case_count": len(results), "results": results,
            }), encoding="utf-8")
            with mock.patch.object(benchmark, "_execute_matrix") as execute:
                code = benchmark.cli([
                    "--evaluate-existing", str(source),
                    "--output", str(output), "--summary", str(summary),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(1, code)
            execute.assert_not_called()
            self.assertFalse(output.exists())
            self.assertFalse(summary.exists())

    def test_evaluate_existing_refuses_to_overwrite_input(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            source = root / "source.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            source.write_text(json.dumps({
                "schema_version": 2, "case_count": 0, "results": [],
            }), encoding="utf-8")
            original = source.read_bytes()
            with mock.patch.object(benchmark, "_execute_matrix") as execute:
                code = benchmark.cli([
                    "--evaluate-existing", str(source), "--output", str(source),
                    "--regression-baseline", str(baseline),
                ])
            self.assertNotEqual(0, code)
            execute.assert_not_called()
            self.assertEqual(original, source.read_bytes())

    def test_invalid_baseline_medians_fail_before_matrix_execution(self):
        invalid_cases = {
            "missing medians": {"schema_version": 1},
            "wrong medians type": self._baseline_summary([]),
            "wrong dataset set": self._baseline_summary({"large-text": 10.0}),
            "nonpositive median": self._baseline_summary({
                **{name: 10.0 for name in benchmark.DATASET_NAMES},
                "large-text": 0.0,
            }),
            "nonfinite median": self._baseline_summary({
                **{name: 10.0 for name in benchmark.DATASET_NAMES},
                "large-text": float("inf"),
            }),
        }
        for label, document in invalid_cases.items():
            with self.subTest(label=label), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                baseline = root / "baseline.json"
                baseline.write_text(json.dumps(document), encoding="utf-8")
                with mock.patch.object(benchmark, "_execute_matrix", return_value=([], 0.0)) as execute, \
                     mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
                    code = benchmark.cli([
                        "--output", str(root / "result.json"),
                        "--regression-baseline", str(baseline),
                    ])
                self.assertNotEqual(0, code)
                self.assertIn("regression baseline", stderr.getvalue())
                execute.assert_not_called()

    def test_valid_committed_summary_schema_reaches_matrix_execution(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            results = self._paired_results()
            with mock.patch.object(
                benchmark, "_execute_matrix", return_value=(results, 1.0)
            ) as execute:
                code = benchmark.cli([
                    "--output", str(root / "result.json"),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(0, code)
            execute.assert_called_once_with(7)

    def test_cli_omission_uses_default_committed_regression_baseline(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            output = root / "result.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            results = self._paired_results()
            with mock.patch.object(benchmark, "DEFAULT_BASELINE", baseline):
                with mock.patch.object(benchmark, "_execute_matrix", return_value=(results, 1.0)):
                    self.assertEqual(benchmark.cli(["--output", str(output)]), 0)

    def test_cli_over_five_percent_regression_exits_nonzero(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            output = root / "result.json"
            summary = root / "summary.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            results = []
            for name in benchmark.DATASET_NAMES:
                results.append(self._gate_result(5.0, name, "reference"))
                results.append(self._gate_result(
                    10.5001 if name == "large-text" else 10.0, name, "MxZip"
                ))
            with mock.patch.object(benchmark, "DEFAULT_BASELINE", baseline):
                with mock.patch.object(benchmark, "_execute_matrix", return_value=(results, 1.0)):
                    code = benchmark.cli([
                        "--output", str(output), "--summary", str(summary),
                    ])
            self.assertEqual(1, code)
            self.assertTrue(output.is_file())
            self.assertTrue(summary.is_file())
            raw_document = json.loads(output.read_text(encoding="utf-8"))
            summary_document = json.loads(summary.read_text(encoding="utf-8"))
            self.assertEqual("failed", raw_document["status"])
            self.assertEqual(10, raw_document["case_count"])
            self.assertEqual("failed", summary_document["status"])
            self.assertEqual(10, summary_document["case_count"])
            self.assertEqual(0.05, raw_document["regression_gate"]["limit"])
            self.assertFalse(
                raw_document["regression_gate"]["datasets"]["large-text"]["passed"]
            )
            self.assertTrue(
                raw_document["regression_gate"]["datasets"]["small-files"]["passed"]
            )
            self.assertAlmostEqual(
                0.05001,
                raw_document["regression_gate"]["datasets"]["large-text"]["fraction"],
            )
            self.assertEqual(
                hashlib.sha256(output.read_bytes()).hexdigest(),
                summary_document["raw_json_sha256"],
            )

    def test_atomic_raw_write_failure_preserves_existing_output_and_is_clear(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            output = root / "result.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            output.write_bytes(b"existing evidence")
            results = self._paired_results()
            with mock.patch.object(
                benchmark, "_execute_matrix", return_value=(results, 1.0)
            ), mock.patch.object(
                benchmark.os, "replace", side_effect=PermissionError("replace denied")
            ), mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
                code = benchmark.cli([
                    "--output", str(output),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(1, code)
            self.assertEqual(b"existing evidence", output.read_bytes())
            self.assertIn("failed to write benchmark raw output", stderr.getvalue())

    def test_summary_publish_failure_restores_existing_pair_across_directories(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            raw_dir = root / "raw"
            summary_dir = root / "summary"
            raw_dir.mkdir()
            summary_dir.mkdir()
            baseline = root / "baseline.json"
            output = raw_dir / "result.json"
            summary = summary_dir / "summary.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            output.write_bytes(b"old raw evidence")
            summary.write_bytes(b"old summary evidence")
            results = self._paired_results()
            real_replace = benchmark.os.replace
            failed = False

            def fail_summary_publish(source, destination):
                nonlocal failed
                source_path = Path(source)
                if not failed and destination == summary and source_path.suffix == ".tmp":
                    failed = True
                    raise PermissionError("summary replace denied")
                return real_replace(source, destination)

            with mock.patch.object(
                benchmark, "_execute_matrix", return_value=(results, 1.0)
            ), mock.patch.object(
                benchmark.os, "replace", side_effect=fail_summary_publish
            ), mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
                code = benchmark.cli([
                    "--output", str(output), "--summary", str(summary),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(1, code)
            self.assertEqual(b"old raw evidence", output.read_bytes())
            self.assertEqual(b"old summary evidence", summary.read_bytes())
            self.assertIn("failed to publish benchmark evidence pair", stderr.getvalue())
            self.assertEqual([], list(root.rglob("*.tmp")))
            self.assertEqual([], list(root.rglob("*.bak")))

    def test_summary_publish_failure_removes_new_pair_and_transaction_artifacts(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            output = root / "result.json"
            summary = root / "summary.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            results = self._paired_results()
            real_replace = benchmark.os.replace
            failed = False

            def fail_summary_publish(source, destination):
                nonlocal failed
                source_path = Path(source)
                if not failed and destination == summary and source_path.suffix == ".tmp":
                    failed = True
                    raise PermissionError("summary replace denied")
                return real_replace(source, destination)

            with mock.patch.object(
                benchmark, "_execute_matrix", return_value=(results, 1.0)
            ), mock.patch.object(
                benchmark.os, "replace", side_effect=fail_summary_publish
            ):
                code = benchmark.cli([
                    "--output", str(output), "--summary", str(summary),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(1, code)
            self.assertFalse(output.exists())
            self.assertFalse(summary.exists())
            self.assertEqual([], list(root.rglob("*.tmp")))
            self.assertEqual([], list(root.rglob("*.bak")))

    def test_rollback_failure_is_reported_and_retains_unrestored_backup(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            output = root / "result.json"
            summary = root / "summary.json"
            baseline.write_text(json.dumps(self._baseline_summary()), encoding="utf-8")
            output.write_bytes(b"old raw evidence")
            summary.write_bytes(b"old summary evidence")
            results = self._paired_results()
            real_replace = benchmark.os.replace
            summary_failed = False

            def fail_publish_and_raw_restore(source, destination):
                nonlocal summary_failed
                source_path = Path(source)
                if not summary_failed and destination == summary and source_path.suffix == ".tmp":
                    summary_failed = True
                    raise PermissionError("summary replace denied")
                if summary_failed and destination == output and source_path.suffix == ".bak":
                    raise PermissionError("raw restore denied")
                return real_replace(source, destination)

            with mock.patch.object(
                benchmark, "_execute_matrix", return_value=(results, 1.0)
            ), mock.patch.object(
                benchmark.os, "replace", side_effect=fail_publish_and_raw_restore
            ), mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
                code = benchmark.cli([
                    "--output", str(output), "--summary", str(summary),
                    "--regression-baseline", str(baseline),
                ])
            self.assertEqual(1, code)
            self.assertIn("rollback failed", stderr.getvalue())
            backups = list(root.rglob("*.bak"))
            self.assertEqual(1, len(backups))
            self.assertEqual(b"old raw evidence", backups[0].read_bytes())
            self.assertEqual([], list(root.rglob("*.tmp")))

    def test_cli_refuses_summary_that_would_overwrite_regression_baseline(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            baseline = root / "baseline.json"
            baseline.write_text("{}", encoding="utf-8")
            with mock.patch.object(benchmark, "_execute_matrix") as execute:
                code = benchmark.cli([
                    "--output", str(root / "result.json"),
                    "--regression-baseline", str(baseline),
                    "--summary", str(baseline),
                ])
            self.assertNotEqual(code, 0)
            execute.assert_not_called()

    def test_cli_refuses_output_that_would_overwrite_regression_baseline(self):
        with tempfile.TemporaryDirectory() as temp:
            baseline = Path(temp) / "baseline.json"
            baseline.write_text("{}", encoding="utf-8")
            with mock.patch.object(benchmark, "_execute_matrix") as execute:
                code = benchmark.cli([
                    "--output", str(baseline), "--regression-baseline", str(baseline),
                ])
            self.assertNotEqual(code, 0)
            execute.assert_not_called()

    def test_cli_baseline_creation_refuses_output_equal_to_summary(self):
        with tempfile.TemporaryDirectory() as temp:
            shared = Path(temp) / "shared.json"
            with mock.patch.object(benchmark, "_execute_matrix") as execute:
                code = benchmark.cli([
                    "--create-baseline", "--output", str(shared), "--summary", str(shared),
                ])
            self.assertNotEqual(code, 0)
            execute.assert_not_called()

    def test_cli_baseline_creation_mode_is_explicit_and_allows_new_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            summary = root / "new-baseline.json"
            with mock.patch.object(benchmark, "_execute_matrix", return_value=(self._paired_results(), 1.0)):
                code = benchmark.cli([
                    "--create-baseline", "--output", str(root / "raw.json"), "--summary", str(summary),
                ])
            self.assertEqual(code, 0)
            self.assertTrue(summary.is_file())


if __name__ == "__main__":
    unittest.main()

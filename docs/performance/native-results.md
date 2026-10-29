# Native acceleration results

## Status

All three `yzip_accel.dll` operations remain disabled by default. They are diagnostic opt-ins:

- `YZIP_NATIVE_ZIP_EXTRACT=1`
- `YZIP_NATIVE_7Z_EXTRACT=1`
- `YZIP_NATIVE_ZIP_CREATE=1`

`YZIP_DISABLE_ACCEL=1` overrides all three. Unsupported, encrypted, split, unavailable, ABI-mismatched, or otherwise ineligible inputs retain the existing whole-operation Python path.

These tables combine three separately captured result sets. They are not one final Task 6 matrix. A fresh current-HEAD 1/4/8/16-thread, seven-run matrix is still required before release; no result below is presented as that final rerun.

## Task 1 CLI baseline

Conditions: level 5, requested threads 1/4/8/16, five datasets, one warm-up plus seven measured subprocess runs, process startup included, and cross-extraction SHA-256 verification outside the timed subprocess. The thread values are requests, not measured internal worker counts. Extraction compares opposite-producer archives rather than identical archive bytes.

At requested 8 threads, yzip/Bandizip wall-time geometric means were:

| Operation | Ratio | Result |
|---|---:|---|
| ZIP compression | 3.990x | yzip slower |
| ZIP extraction | 4.355x | yzip slower |
| 7z compression | 3.755x | yzip slower |
| 7z extraction | 9.355x | yzip slower |

ZIP compression archive-size geometric mean was 0.989x yzip/Bandizip. 7z was 1.712x, dominated by the 1,500-small-file case where yzip output was 12.292x the Bandizip output. At requested 8 threads, case-level peak-RSS medians were about 12.8 MiB for Bandizip and 39.0 MiB for yzip; observed maxima were about 199.5 MiB and 282.0 MiB respectively. These include each CLI runtime and are not codec-only measurements.

Raw ignored artifact: `.omc/artifacts/native-performance-baseline/baseline-five-datasets.json`. The committed evidence manifest is `docs/performance/native-benchmark-baseline.json`.

## Task 4: plain 7z extraction

This benchmark used identical archive bytes for existing yzip, native opt-in, and Bandizip; requested workers were 8. Each condition used one warm-up and seven measured fresh subprocess runs with SHA-256 verification.

| Dataset | Existing yzip median | Native median | Bandizip median |
|---|---:|---:|---:|
| small-files (1,500) | 1.7411 s | 2.8830 s | 0.8713 s |
| large-file (32 MiB) | 0.4513 s | 0.4796 s | 0.0531 s |
| large-text (16 MiB) | 0.4076 s | 0.4099 s | 0.0319 s |
| incompressible (16 MiB) | 0.3290 s | 0.3495 s | 0.0338 s |
| mixed-project | 0.5901 s | 0.9224 s | 0.1259 s |

Native missed the default-enablement gate in all five datasets. Plain LZMA/LZMA2 therefore remains opt-in; AES, unsupported filters, duplicate paths, links, and split volumes remain on the existing path.

Canonical ignored evidence: `.superpowers/sdd/2026-10-28-native-performance-hybrid/task4-benchmark.json`. Driver: `task4_bench.py`. The canonical result records commit `5fbca5aa595db3f4dc03d2053f05ed3e1c1eecf2`; it predates the final Task 6 rerun.

## Task 5: plain ZIP creation

Conditions: same source, level 5, requested threads 1 and 8, one warm-up plus seven measured fresh subprocess runs. Cross-product extraction and tree hashes were integrity checks outside compression timing. Every native run recorded actual native selection.

| Dataset | Threads | Existing yzip | Native | Bandizip | Native vs existing |
|---|---:|---:|---:|---:|---:|
| large-text | 1 | 0.1806 s | 0.3352 s | 0.0693 s | +85.6% |
| large-text | 8 | 0.1814 s | 0.3093 s | 0.0488 s | +70.5% |
| incompressible | 1 | 0.3312 s | 0.3765 s | 0.1800 s | +13.7% |
| incompressible | 8 | 0.2165 s | 0.3779 s | 0.0600 s | +74.5% |
| small-files | 1 | 1.2014 s | 4.0953 s | 0.7320 s | +240.9% |
| small-files | 8 | 0.9469 s | 6.2363 s | 0.9444 s | +558.6% |
| mixed-project | 1 | 0.5300 s | 1.0963 s | 0.2403 s | +106.8% |
| mixed-project | 8 | 0.5541 s | 1.0628 s | 0.2297 s | +91.8% |
| large-file | 1 | 0.6016 s | 1.0066 s | 0.1926 s | +67.3% |
| large-file | 8 | 0.3663 s | 0.9406 s | 0.0933 s | +156.8% |

Archive-size gates passed, but native was slower in every condition and remains opt-in. AES, split, edit/copy, symlinks, empty archives, ZIP64-boundary inputs, and unavailable runtimes use the existing writer.

Canonical ignored evidence: `.superpowers/sdd/2026-10-28-native-performance-hybrid/task5-benchmark.json`. Driver: `task5_bench.py`. It covers only requested threads 1 and 8 and is not a substitute for the final Task 6 1/4/8/16 rerun.

## Release evidence still required

Before a release claim, rerun the complete current-HEAD matrix at requested threads 1/4/8/16 with one warm-up and at least seven measured runs. Record tool/runtime versions, raw-artifact SHA-256, actual native selection, median/p95, CPU, peak RSS, archive sizes, and cross-extraction tree hashes. Recheck the existing-yzip 7z compression 8-thread regression gate. Until that evidence exists, the paths above remain opt-in and this document records historical canonical evidence only.

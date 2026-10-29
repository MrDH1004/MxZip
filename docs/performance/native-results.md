# Native acceleration results

## Status

All three `yzip_accel.dll` operations remain disabled by default. They are diagnostic opt-ins:

- `YZIP_NATIVE_ZIP_EXTRACT=1`
- `YZIP_NATIVE_7Z_EXTRACT=1`
- `YZIP_NATIVE_ZIP_CREATE=1`

`YZIP_DISABLE_ACCEL=1` overrides all three. Unsupported, encrypted, split, unavailable, ABI-mismatched, or otherwise ineligible inputs retain the existing whole-operation Python path.

The Task 6 current-HEAD matrix is now complete, but its historical 7z compression regression gate failed. Release remains blocked. The earlier Task 1, Task 4, and Task 5 evidence below remains useful for comparison, but it is not substituted for the Task 6 run.

## Task 6 current-HEAD matrix

The matrix ran from commit `88eb72eaaa65eebb8105eab1cf31a3443d6446fa` with five datasets, two programs, two formats, two operations, and requested thread counts 1/4/8/16: 160 cases total. Each case used one warm-up plus seven measured fresh subprocess runs. Total matrix elapsed time was 2,503.662 seconds (41 minutes 43.662 seconds). Thread counts are CLI requests, not observed internal worker counts.

Raw ignored artifact: `.omc/artifacts/native-performance-final/current.json`; SHA-256: `2b2499f06ad5165cfb4afef30367ad41a41209919fc8012ab22e486091f224bd`. Machine-readable summary: `.omc/artifacts/native-performance-final/current-summary.json`.

At requested 8 threads, the five-dataset wall-time geometric means were:

| Operation | yzip/Bandizip | yzip wins |
|---|---:|---:|
| ZIP compression | 3.1637x | 0/5 |
| ZIP extraction | 3.1327x | 0/5 |
| 7z compression | 3.5362x | 0/5 |
| 7z extraction | 7.1198x | 0/5 |

These are end-to-end CLI measurements including process startup. Extraction follows the benchmark's opposite-producer archive preparation, so it does not compare both tools against identical archive bytes.

At requested 8 threads, compression archive-size geometric means were 0.9891x for ZIP and 1.7230x for 7z (yzip/Bandizip). The 7z result is dominated by `small-files`, where the ratio was 12.5734x. Across each tool's 20 requested-8-thread cases, median CPU time was 0.109375 seconds for Bandizip and 0.296875 seconds for yzip. Median peak RSS was 12.46 MiB and 39.95 MiB respectively; observed maxima were 199.52 MiB and 292.79 MiB. These aggregate CLI processes and are not codec-only resource measurements.

The historical regression gate compares current yzip 7z compression at requested 8 threads with the committed Task 1 baseline. It failed overall:

| Dataset | Current median | Historical regression | Gate (<= +5%) |
|---|---:|---:|---:|
| large-text | 0.298375 s | +15.5008% | FAIL |
| incompressible | 2.499253 s | +36.8039% | FAIL |
| small-files | 0.701919 s | +23.6803% | FAIL |
| mixed-project | 0.370136 s | -3.1183% | PASS |
| large-file | 0.366385 s | -24.1696% | PASS |

The full matrix ran while an external video workload (`pipeline`/RIFE/FFmpeg) was observed on the machine. That concurrency is a measurement confounder; it does not turn the failed historical gate into a pass. A separate same-time diagnostic compared old commit `4dc8d7a` with current commit `88eb72e` for incompressible yzip 7z compression at requested 8 threads. Its five paired seven-run median ratios (current/old) were `1.0979804116310825`, `1.0175822002297623`, `1.0232991625938561`, `1.0011716987506925`, and `1.0706072848204216`: median +2.3299%, geometric mean +4.1504%. This paired diagnostic suggests a smaller same-time difference than the historical comparison, but it covers one dataset and one operation only. It is not a replacement for the 160-case gate and cannot clear the release block.

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

## Release status

The required current-HEAD matrix exists, but its historical 7z compression 8-thread regression gate failed and the run had concurrent external video-processing load. Release remains blocked pending a controlled rerun and a passing gate or an explicit reviewed disposition. The native paths remain opt-in.

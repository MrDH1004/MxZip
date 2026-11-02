# Native acceleration results

## Status

All three `mxzip_accel.dll` operations remain disabled by default. They are diagnostic opt-ins:

- `MXZIP_NATIVE_ZIP_EXTRACT=1`
- `MXZIP_NATIVE_7Z_EXTRACT=1`
- `MXZIP_NATIVE_ZIP_CREATE=1`

`MXZIP_DISABLE_ACCEL=1` overrides all three. Unsupported, encrypted, split, unavailable, ABI-mismatched, or otherwise ineligible inputs retain the existing whole-operation Python path.

The Task 6 matrix and its paired-normalized evaluation are complete. The original absolute historical gate failed, while the paired-normalized release gate passed all five datasets. The earlier Task 1, Task 4, and Task 5 evidence below remains useful for comparison, but it is not substituted for the Task 6 run.

## Task 6 current-HEAD matrix

The matrix contains five datasets, two programs, two formats, two operations, and requested thread counts 1/4/8/16: 160 cases total. Each case used one warm-up plus seven measured fresh subprocess runs. Thread counts are CLI requests, not observed internal worker counts.

The original raw artifact is `.omc/artifacts/native-performance-final/current.json`; SHA-256 `ca18038535f6e5ff2945092384b73e94736c4e037fc510f1dbf00bb7a7aa92fe`, `case_count: 160`, `status: failed`. The evaluated artifact is `.omc/artifacts/native-performance-final/evaluated.json`; SHA-256 `4552b8012abcc95cae88bb7fff8cfeb82d03fca8c0cbd87f5a8309ee27e1a3aa`, `case_count: 160`, `status: passed`. Its provenance records input path `.omc\artifacts\native-performance-final\current.json` and input SHA-256 `ca18038535f6e5ff2945092384b73e94736c4e037fc510f1dbf00bb7a7aa92fe`. The separate `.omc/artifacts/native-performance-final/evaluated-summary.json` has SHA-256 `89311d9aefec3d219164166982eabd587e9a54a6360930bab207787be0e1cf2b` and identifies the evaluated artifact by the same `4552b801...` SHA-256.

At requested 8 threads, the five-dataset wall-time geometric means were:

| Operation | MxZip/the third-party archiver | MxZip wins |
|---|---:|---:|
| ZIP compression | 3.1381x | 0/5 |
| ZIP extraction | 3.5101x | 0/5 |
| 7z compression | 2.8912x | 0/5 |
| 7z extraction | 6.7017x | 0/5 |

These are end-to-end CLI measurements including process startup. Extraction follows the benchmark's opposite-producer archive preparation, so it does not compare both tools against identical archive bytes.

At requested 8 threads, compression archive-size geometric means were 0.9891x for ZIP and 1.7256x for 7z (MxZip/the third-party archiver). The 7z result is dominated by `small-files`, where the ratio was 12.6758x. Across each tool's 20 requested-8-thread cases, median CPU time was 0.140625 seconds for the third-party archiver and 0.304688 seconds for MxZip. Median peak RSS was 13.08 MiB and 39.97 MiB respectively; observed maxima were 199.51 MiB and 282.23 MiB. These aggregate CLI processes and are not codec-only resource measurements.

The original historical absolute gate compares current MxZip 7z compression wall times at requested 8 threads directly with the committed Task 1 MxZip wall times. It failed in two datasets and therefore failed overall:

| Dataset | Current median | Historical regression | Gate (<= +5%) |
|---|---:|---:|---:|
| large-text | 0.222591 s | -13.8352% | PASS |
| incompressible | 1.708357 s | -6.4881% | PASS |
| small-files | 0.786535 s | +38.5898% | FAIL |
| mixed-project | 0.591870 s | +54.9198% | FAIL |
| large-file | 0.421080 s | -12.8494% | PASS |

The matrix ran while an external video workload (`pipeline`/RIFE/FFmpeg) was observed on the machine. Absolute wall-time comparison against a static historical run is vulnerable to that kind of machine-load difference. This does not erase the original gate failure; it explains why the original result is retained as historical evidence rather than used as the final release decision.

The evaluated artifact uses the declared `paired-MxZip-over-reference-ratio-v1` normalization for each dataset:

```text
fraction = ((current MxZip median / current the third-party archiver median)
            / (baseline MxZip median / baseline the third-party archiver median)) - 1
```

The common the third-party archiver measurement normalizes machine-level timing shifts within each current/baseline pair. It does not claim identical codec work or remove every possible source of noise.

| Dataset | Current MxZip/the third-party archiver | Baseline MxZip/the third-party archiver | Normalized fraction | Gate (<= +5%) |
|---|---:|---:|---:|---:|
| large-text | 4.527169x | 5.027714x | -9.9557% | PASS |
| incompressible | 6.082878x | 6.329592x | -3.8978% | PASS |
| small-files | 1.489082x | 1.756486x | -15.2238% | PASS |
| mixed-project | 2.409311x | 3.848159x | -37.3906% | PASS |
| large-file | 2.044605x | 3.472584x | -41.1215% | PASS |

All five normalized fractions are at or below the +5% limit. The Task 6 paired-normalized release gate therefore passed.

## Task 1 CLI baseline

Conditions: level 5, requested threads 1/4/8/16, five datasets, one warm-up plus seven measured subprocess runs, process startup included, and cross-extraction SHA-256 verification outside the timed subprocess. The thread values are requests, not measured internal worker counts. Extraction compares opposite-producer archives rather than identical archive bytes.

At requested 8 threads, MxZip/the third-party archiver wall-time geometric means were:

| Operation | Ratio | Result |
|---|---:|---|
| ZIP compression | 3.990x | MxZip slower |
| ZIP extraction | 4.355x | MxZip slower |
| 7z compression | 3.755x | MxZip slower |
| 7z extraction | 9.355x | MxZip slower |

ZIP compression archive-size geometric mean was 0.989x MxZip/the third-party archiver. 7z was 1.712x, dominated by the 1,500-small-file case where MxZip output was 12.292x the the third-party archiver output. At requested 8 threads, case-level peak-RSS medians were about 12.8 MiB for the third-party archiver and 39.0 MiB for MxZip; observed maxima were about 199.5 MiB and 282.0 MiB respectively. These include each CLI runtime and are not codec-only measurements.

Raw ignored artifact: `.omc/artifacts/native-performance-baseline/baseline-five-datasets.json`. The committed evidence manifest is `docs/performance/native-benchmark-baseline.json`.

## Task 4: plain 7z extraction

This benchmark used identical archive bytes for existing MxZip, native opt-in, and the third-party archiver; requested workers were 8. Each condition used one warm-up and seven measured fresh subprocess runs with SHA-256 verification.

| Dataset | Existing MxZip median | Native median | the third-party archiver median |
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

| Dataset | Threads | Existing MxZip | Native | the third-party archiver | Native vs existing |
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

The required matrix exists. Its original historical absolute gate failed under concurrent external video-processing load, but the provenance-bound paired-normalized evaluation passed all five datasets. The Task 6 performance release gate is passed. The native paths remain opt-in because their separate Task 3–5 enablement gates did not pass.

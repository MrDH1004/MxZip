# Native archive benchmark

`benchmark_native.py` compares the installed the third-party archiver console tool with the current yzip CLI on Windows. It covers ZIP and 7z compression and extraction at level 5 with 1, 4, 8, and 16 requested threads across five independent dataset classes.

## Run

From the repository root:

```powershell
python benchmark_native.py --runs 7 `
  --output .omc\artifacts\native-performance-baseline\latest.json `
  --summary .omc\artifacts\native-performance-baseline\latest-summary.json
```

Normal runs automatically use the committed `docs\performance\native-benchmark-baseline.json` regression baseline. `--regression-baseline` is only needed to select another existing baseline. Resolved output, summary, and selected baseline paths must all be distinct; collisions are rejected before the matrix starts.

An already completed raw matrix can be evaluated without rerunning any timing case:

```powershell
python benchmark_native.py `
  --evaluate-existing .omc\artifacts\native-performance-final\current.json `
  --output .omc\artifacts\native-performance-final\evaluated.json `
  --summary .omc\artifacts\native-performance-final\evaluated-summary.json
```

Evaluation preserves the input `results` array, records the input path and SHA-256, and
publishes the evaluated raw/summary as one rollback-protected pair. Input, output,
summary, and baseline paths must be distinct.

Creating or intentionally replacing a baseline is a separate explicit mode:

```powershell
python benchmark_native.py --create-baseline --runs 7 `
  --output .omc\artifacts\native-performance-baseline\baseline-five-datasets.json `
  --summary docs\performance\native-benchmark-baseline.json
```

`--create-baseline` requires `--summary` and cannot be combined with `--regression-baseline`, so it cannot silently act as a normal regression run.

The harness requires `C:\Program Files\the third-party archiver\the third-party CLI`. It generates these deterministic datasets in a temporary directory:

- 8 MiB of repeated application-log text;
- 8 MiB of seeded pseudorandom, effectively incompressible bytes;
- 1,500 small files spread across 15 directories;
- a real copy of the yzip project with generated, tool, state, and VCS directories excluded;
- one 32 MiB file.

Every case gets one unreported warm-up followed by the requested number of measured runs. The warm-up result receives the same SHA-256 integrity verification but its timing is discarded. Every iteration uses a separate temporary output root; the harness neither clears operating-system caches nor changes installed applications.

The third-party archiver receives `-t:N`. The yzip subprocess calls `runtimeconfig.configure(N, temporary_root)` before invoking `yzip_cli.main`, so the requested worker limit applies inside the measured process.

## Report contract

The JSON document contains `schema_version`, `runs`, `warmup_runs`, and a `results` array. Each result records:

- the tool, format, operation, level, source, archive, and requested thread count;
- measured wall-time median and nearest-rank p95;
- median process CPU time and maximum peak resident working set;
- archive size, exact command, measured-run count, and SHA-256 tree result.

The tree digest includes each relative path, file size, and file content in sorted path order. Extraction cases always consume an archive made by the opposite tool. Every measured compression result is also extracted by the opposite tool: yzip output by the third-party archiver, and the third-party archiver output by yzip. Any command failure or source/extracted-tree digest mismatch aborts the run.

The protected condition is the paired 8-thread 7z compression ratio
`yzip median / the third-party archiver median` for each dataset. The regression fraction is
`current ratio / baseline ratio - 1`. This normalizes machine-wide or environmental
slowdowns that affect both products. Execution exits nonzero when any normalized
fraction exceeds 5%. Absolute yzip and the third-party archiver medians remain in raw and summary
evidence as informational measurements. Missing, duplicate, nonpositive, or nonfinite
pairs fail before publication.

## 2026-10-28 baseline ledger

Raw artifact: `.omc/artifacts/native-performance-baseline/baseline-five-datasets.json` (git-ignored but retained locally and readable)

Committed evidence manifest: `docs/performance/native-benchmark-baseline.json`

The complete matrix contains 160 cases. All cases completed seven measured runs after one warm-up in 2,509.78 seconds. The raw JSON SHA-256 is `21dbd8c8cadfa431a5a0e06d244710d332ce08cabada3d9c776a27bcf572b465`.

Baseline 7z compression 8-thread paired wall medians and ratios:

| Dataset | yzip seconds | the third-party archiver seconds | yzip/the third-party archiver |
|---|---:|---:|---:|
| large-text | 0.258331 | 0.051381 | 5.027714 |
| incompressible | 1.826888 | 0.288626 | 6.329592 |
| small-files | 0.567527 | 0.323104 | 1.756486 |
| mixed-project | 0.382049 | 0.099281 | 3.848159 |
| large-file | 0.483164 | 0.139137 | 3.472584 |

The committed manifest records the raw artifact path and digest, case/run counts,
elapsed time, dataset tree digests, both products' medians, paired ratios, and gate
methodology. The raw JSON contains every case's median, p95, CPU, peak RSS, archive
size, command, and full tree digest.

# MxZip vs Bandizip (2026-11-01, home PC)

Paired end-to-end runs: each case runs MxZip (`python -c "import yzip_cli; yzip_cli.main(...)"`) and Bandizip
(`bz.exe`) back to back, level 5, default threads, 1 warm-up + 7 measured runs, median. Every round trip is
checked (extracted tree size equals the source). Ratio = Bandizip time / MxZip time (100 % = as fast).

| | before | after |
|---|---:|---:|
| compress (geomean, 12 cases) | 63 % | 106 % |
| extract (geomean, 12 cases) | 36 % | 70 % |
| all 24 cases | 48 % | 86 % |

Datasets: `benchmark_native.make_datasets` (8 MB text, 8 MB random, 1,500 small files, the repository as a
mixed project, a 32 MB repetitive file) plus a copy of the built app (~150 MB of DLLs, .pyd, Qt plugins).

Large jobs (Bandizip >= 0.3 s): MxZip is faster in all of them, e.g. app tree 7z compress 4.1 s vs 10.2 s,
1,500 small files 7z extract 0.63 s vs 1.00 s. The remaining gap is on jobs Bandizip finishes in 0.03-0.05 s:
starting a Python process costs ~0.03 s on this machine and importing the engine another ~0.03 s.

## What changed

- Path safety: one `realpath` per folder instead of per member (`ziplib.TargetResolver`); same zip-slip and
  link guarantees.
- New files are written in place (created exclusively, removed on failure); only existing files still go
  through a part file + rename. Times set on the open handle when there are many files.
- 7z: own header reader (`sevenzip_header.py`, 7zFormat.txt) for LZMA2/LZMA/COPY(+AES) archives, so py7zr
  (~0.13 s to import) is only loaded for other layouts; small members committed directly.
- 7z write: LZMA2 header (kEncodedHeader); already-compressed blocks stored as LZMA2 uncompressed chunks
  instead of being run through the encoder; smaller minimum block so mid-size inputs use several cores.
- Thread pool on `threading` alone (`runtimeconfig.ThreadPoolExecutor`): `concurrent.futures` pulled in
  logging/traceback/inspect, 20-50 ms per run. Decoding starts on the calling thread and moves to the pool
  only when the remaining work is estimated above ~5 ms; large ZIP members overlap inflate and disk writes.
- Start-up: rarely used modules load on first use (`runtimeconfig.lazy_import`), no `typing` / `dataclasses`
  on the hot path, argparse's colour theme and terminal width resolved only for help output.

## Not adopted

- Smaller 7z blocks for 2 MB inputs: 2x faster but archives 21 % larger.
- File preallocation (`truncate`/`SetEndOfFile`): +80 ms for 32 MB on NTFS.
- Native DLL paths stay opt-in (see `native-results.md`).

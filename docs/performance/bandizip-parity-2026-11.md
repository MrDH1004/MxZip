# MxZip vs Bandizip (2026-11-01, home PC)

Paired end-to-end runs: each case runs MxZip (`python -c "import yzip_cli; yzip_cli.main(...)"`) and Bandizip
(`bz.exe`) back to back, level 5, default threads, 1 warm-up + 7 measured runs, median. Every round trip is
checked (extracted tree size equals the source). Ratio = Bandizip time / MxZip time (100 % = as fast).

| | before | after |
|---|---:|---:|
| compress (geomean, 12 cases) | 63 % | 110 % |
| extract (geomean, 12 cases) | 36 % | 75 % |
| all 24 cases | 48 % | 91 % |

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
- Start-up: rarely used modules load on first use (`runtimeconfig.lazy_import`, a proxy outside sys.modules),
  no `typing` / `dataclasses` on the hot path, plain command lines parsed without argparse (argparse still
  handles help, errors and anything unusual), SetFileTime only once ctypes is loaded or for 256+ files,
  up to 8 threads committing small 7z members.

## Not adopted

- Smaller 7z blocks (256 KB) for inputs below workers x dictionary: 2 MB project 0.235 -> 0.111 s but the
  archive 37 % larger (584 KB -> 798 KB; 512 KB blocks: +21 %). Not worth it for an archiver.
- An own ZIP central-directory reader instead of zipfile: ~7 ms per ZIP run (at most +2 points on extract)
  against rewriting ZIP64 / name encoding / encryption handling. Overlapping 7z decode and file commits:
  the cases it helps already beat Bandizip.
- File preallocation (`truncate`/`SetEndOfFile`): +80 ms for 32 MB on NTFS.
- Native DLL paths stay opt-in (see `native-results.md`).

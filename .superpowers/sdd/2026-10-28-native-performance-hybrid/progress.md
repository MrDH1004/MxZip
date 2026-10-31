# SDD ledger — plan: docs/superpowers/plans/2026-10-28-native-performance-hybrid.md

Baseline: `python -W ignore -m unittest test_ziprepair test_ziplib test_zipwriter test_formats test_nativearchive test_moreformats test_updater` — 130 passed after linking the existing read-only tools payload into the worktree.

| Scope | Shared interface / internal consistency | Finding |
|---|---|---|
| Task 1 | benchmark result consumed by Tasks 3–6 | Clean: JSON contract carries timing, resources, size, threads, command, and integrity result. |
| Tasks 2→3 | loader/cancellation consumed by ZIP extraction | Clean: Task 2 owns ABI and Python wrapper; Task 3 adds operation entry points. |
| Tasks 2→4 | loader/cancellation consumed by 7z extraction | Clean: structured unsupported result is added by Task 4 without changing loader contract. |
| Tasks 2→5 | loader/cancellation consumed by ZIP compression | Clean: Task 5 adds output entry point and keeps Task 2 capability model. |
| Tasks 3–5→6 | runtime behavior consumed by packaging and benchmark | Clean: Task 6 packages only behavior already proven by prior tasks. |
| Task 1 | tests versus harness implementation | Clean: all required observable calculations and hash failure have named tests. |
| Task 2 | tests versus ABI/build files | Clean: stub ABI first; codec operations deliberately deferred. |
| Task 3 | selection/transaction tests versus ZIP extraction | Clean. |
| Task 4 | selection/safety tests versus 7z extraction | Clean. |
| Task 5 | selection/compatibility tests versus ZIP compression | Clean. |
| Task 6 | packaging tests versus release/build changes | Clean. |

Ruling: The plan's Task 1 says capture the complete 1/4/8/16 baseline, which is expensive but read-only and essential; execute it after unit GREEN and store ignored raw artifacts, not commit generated datasets.
Task 1: minor (deferred): documentation currently overstates cross-extraction integrity; correct it as part of the required cross-extraction implementation in fix round 1.
Task 1: fix round 1/5 (3 addressed, 3 open — mandatory regression baseline can be omitted; CLI behavior lacks coverage; warm-up integrity claim mismatch; new baseline-overwrite hazard; commits 73b5f2b..08fff48)
Task 1: fix round 2/5 (3 addressed, 1 open — output path can still overwrite baseline or collide with summary; commits 08fff48..0d91f43)
Task 1: fix round 3/5 (1 addressed, 0 open — all output/summary/baseline collisions rejected before execution; commits 0d91f43..4dc8d7a)
Task 1: complete (commits b3288b2..4dc8d7a, review clean; 17 benchmark tests pass; 160-case seven-run baseline captured)
Ruling: Task 2 reuses the Task 1 implementer because the collaboration agent thread limit rejected a fresh spawn; the task brief/report boundary still isolates requirements, but the cost if wrong is less fresh-context independence for this task.
Task 2: minor (deferred): Unicode error text could truncate if it grows across two retrieval calls; current DLL error is thread-local/stable, final review will triage.
Task 2: fix round 1/5 approved (build flags/LTCG, required-DLL quality gate, and single-source pin manifest resolved; commits b7ead0e2c9ffcf894c218753d4a72ce66f75b0f0..eb2c755b346d427e732bfe87b287c88d214b72a9).
Task 2: complete at eb2c755b346d427e732bfe87b287c88d214b72a9; independent scoped review approved, 20 focused tests passed.
Task 3: implementation commit 53cc2a3f07bc9b149108a45a964ef98e26efea68; 58 focused tests passed, but performance gate failed and native ZIP extraction remains disabled by default.
Task 3: review requires fixes for runtime-unavailable fallback, post-native cancellation, I/O error classification, publication rollback coverage, unchecked cleanup results, and progress semantics.
Task 3: performance evidence identifies Python staging verification/publication (78.9% of small-files wall time) and O(n^2) native job lookup as the first optimization targets.
Task 3: Ruling: The plan references 7-Zip 26.05, but Task 2 pins and verifies official 26.04; Task 3 retains the verified 26.04 supply-chain contract because changing it would invalidate Task 2 hashes and is outside this task. Cost if wrong: Task 3 may need ABI retesting when the repository deliberately upgrades to 26.05.
Task 3: Ruling: All five required ZIP extraction datasets regressed versus both the third-party archiver and the existing Python baseline, so the capability remains opt-in through YZIP_NATIVE_ZIP_EXTRACT=1 instead of default-enabled. Cost if wrong: users do not receive the native path automatically until a later optimization clears the benchmark gate.
Task 3: complete (commits eb2c755..53cc2a3, tests: python -W ignore -m unittest test_archive_accel test_zip_native test_ziplib test_zip_extract_perf -q -> 58 passed; native build and exports verified; self-review used because no subagent tool is available).
Task 3: follow-up complete at c4d8318 (I/O classification and rollback continuation fixed; small-file native opt-in routes to the faster existing engine; independent review found Critical 0 / Important 0).
Release 1.0.21: source/tag pushed at 320f755, but GitHub run 40799282781 failed in packaged Explorer verb smoke; release artifact was not published. A dedicated fix is in progress.
Task 4: in progress from 320f755 using TDD for in-process plain LZMA/LZMA2 7z extraction, whole-operation fallback, and same-archive benchmark gating.
Task 4: implementation committed at 772d84bee6b22c8ee99ea28bded64bf60077a7b8; native build, 96 focused tests, and 341 full-suite tests passed. Same-archive benchmark failed the default-enablement gate, so YZIP_NATIVE_7Z_EXTRACT remains opt-in pending independent review.
Task 4: independent review rejected release (Critical 0, Important 5, Minor 1): solid non-leading member selection failure, missing real solid/index coverage, inaccurate symlink inspection, cleanup error masking, incomplete 1,500-file benchmark contract, and unchecked inspect cleanup. Fix round 1/5 is in progress from 772d84b.
Task 4: fix round 1/5 complete at 5fbca5aa595db3f4dc03d2053f05ed3e1c1eecf2. Independent re-review resolved all Important/Minor findings; native build, 128 focused tests, and 346 full-suite tests passed. The canonical five-dataset same-archive benchmark is UTF-8 and contract-complete. Native 7z remains opt-in because all five medians missed the default performance gate.
Task 5: Ruling: Native ZIP compression remains opt-in through YZIP_NATIVE_ZIP_CREATE=1 because only one of ten benchmark conditions beat existing yzip (incompressible/t1 by 1.5%) and none met the sustained the third-party archiver-near gate. Cost if wrong: users do not receive the native path automatically, but the faster existing writer remains the default.
Task 5: RED/GREEN complete for selection, real Store/Deflate bridge, source mutation/read failure, cancellation, corrupt-OK output, output limit, publication failure, metadata, ZIP64-size metadata, and empty-archive fallback.
Task 5: complete (base 5fbca5a; native build passed; focused 96 passed; full suite 362 passed, 1 existing environment skip; benchmark 10/10 conditions SHA cross-verified; default remains opt-in).
Task 5: independent review fix round 1 complete: same-size/same-mtime mutation is blocked by pre/post SHA-256, published central-directory host/modes match the existing writer, ZIP64 is an explicit whole-operation fallback, benchmark evidence records and validates every run's source/destination SHA-256 plus actual selection, and C ABI error recording is fixed-buffer/noexcept-safe. Native clean build passed; focused 101 passed; full suite 368 passed with 1 existing environment skip; canonical warm-up 1 + 7-run benchmark passed integrity/selection validation and native remained slower in 10/10 conditions, so default remains opt-in.
Task 5: final re-review OPEN closed: report relative deltas now match raw medians for incompressible/t8 (+74.6%) and mixed-project/t1 (+106.9%); all 10 derived table rows were recalculated and matched.
Task 6: 160-case matrix complete (five datasets, requested threads 1/4/8/16, warm-up 1 + measured 7). Original raw `.omc/artifacts/native-performance-final/current.json` SHA-256 is `ca18038535f6e5ff2945092384b73e94736c4e037fc510f1dbf00bb7a7aa92fe`, `case_count: 160`, `status: failed`; the absolute historical yzip 7z compression 8-thread gate failed in 2/5 datasets. External `pipeline`/RIFE/FFmpeg load was observed, and static historical wall-time comparison is vulnerable to that machine-load difference. Provenance-bound `.omc/artifacts/native-performance-final/evaluated.json` SHA-256 `4552b8012abcc95cae88bb7fff8cfeb82d03fca8c0cbd87f5a8309ee27e1a3aa` records the current raw path/SHA as input, has `case_count: 160`, and uses `paired-yzip-over-reference-ratio-v1`: `((current yzip / current the third-party archiver) / (baseline yzip / baseline the third-party archiver)) - 1`. Fractions are large-text -9.9557%, incompressible -3.8978%, small-files -15.2238%, mixed-project -37.3906%, and large-file -41.1215%; all five pass the +5% limit. Evaluated status and the Task 6 performance release gate are passed. Separate Task 3–5 native paths remain opt-in.

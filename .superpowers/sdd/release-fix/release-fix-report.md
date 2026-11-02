# Release shell smoke fix report

## Scope

- Worktree: `E:\AIWork\MxZip\.worktrees\native-performance`
- Starting HEAD: `320f755f71779d77a9fd4672ee70074b50b13f1d`
- Failed run: `40799282781`, step `Packaged Explorer verb smoke`
- Forbidden files were not edited or staged by this task: `nativeaccel/**`, `archive_accel.py`, `formats.py`, `test_7z_native.py`.

## Observed failure

The CI log showed that packaged `MxZip.exe --register-shell` returned zero and the smoke could read:

- `HKCU\Software\Classes\CLSID\{7E3B1C52-9A4D-4F1E-B6C2-3D8A5F0E9C71}\InprocServer32`
- the copied shell DLL outside `dist`
- `HKCU\Software\MxZip\Command` pointing at the packaged executable

Static verbs were removed as intended. The first failure occurred only when `probe.exe` asked the hosted runner's default shell menu through `IShellFolder::GetUIObjectOf`. That menu contained built-in and other preinstalled handlers but no MxZip verbs. Every later menu/invocation failure followed from the same missing enumeration. There was no observation that `mxzipshell.dll` had been loaded or that its `QueryContextMenu` failed.

## Root cause

The smoke coupled two independent assertions:

1. MxZip wrote the COM and context-menu handler registration correctly.
2. the GitHub-hosted runner's non-interactive Explorer/default-shell menu immediately discovered and loaded a newly registered in-process HKCU extension.

The second assertion is an environment-dependent Explorer integration check and is not a reliable hosted-runner precondition. Treating its absence as a product-DLL failure produced the 30/50 result. The registration itself still needed stronger value checks; an exit code alone was insufficient.

## TDD evidence

RED command:

```text
python .\shell_probe_smoke.py
```

The first version required a `DIRECT_DLL` marker. It failed with exit 1: the old probe did not understand `direct-list` and returned the ambient Explorer-composed menu instead. This proved the missing capability rather than accepting a locally registered handler as a false pass.

GREEN commands:

```text
cmd /c shell\build.cmd
python .\shell_probe_smoke.py
```

Result: `PASS direct DLL context-menu probe`.

## Fix

- `probe.exe` now has `direct-*` modes that load the exact DLL path with `LoadLibraryW`, call `DllGetClassObject`, create the COM object, initialize it with the same `CF_HDROP` data object, and exercise the same `IContextMenu` methods.
- `exe_verbs_smoke.py` uses this direct DLL path for menu labels and invocation behavior.
- Registration verification remains separate and now checks both file/directory handler keys, CLSID values, `InprocServer32`, packaged command path, and `ThreadingModel=Apartment`.
- The workflow runs a minimal direct-DLL precondition before the packaged registration/verb smoke. It no longer claims hosted Explorer discovery was verified.

## Verification

```text
.\test_shell_native.ps1
```

Passed menu-mask cases 0 through 7.

```text
python .\shell_probe_smoke.py
```

Passed direct DLL context-menu probe.

```text
python -m py_compile exe_verbs_smoke.py shell_probe_smoke.py
git diff --check
```

Passed. `git diff --check` only printed line-ending warnings for concurrent, forbidden-file changes and reported no whitespace error.

The packaged EXE smoke was not executed locally because this task explicitly forbids running or updating the installed app. The release workflow remains the execution environment for that packaged test.

## Follow-up: COM lifetime review minors

The follow-up review found that the direct probe ignored the `CoInitializeEx` result and used manual cleanup after the success path only.

Microsoft's documented contract was applied as follows:

- `S_OK` and `S_FALSE` are successful initialization calls and each is balanced by `CoUninitialize`.
- `RPC_E_CHANGED_MODE` means an incompatible apartment was already selected. The failed STA call is reported distinctly and is not balanced by `CoUninitialize`.
- COM interfaces, menu handles, PIDLs and the loaded module use scope-bound cleanup on every return path. The module lifetime encloses all objects created from the DLL.

TDD RED added two negative probes. The old binary returned the ambient Explorer menu for `mta-direct-list` and did not emit either the changed-mode result or cleanup evidence. After implementation, the smoke reports:

```text
PASS direct DLL context-menu probe
PASS RPC_E_CHANGED_MODE contract and negative cleanup probes
```

The negative cases cover an incompatible MTA-to-STA initialization and a missing DLL. `MXZIP_PROBE_TRACE_CLEANUP=1` emits `CLEANUP` from the outer scope guard after inner resource destructors have run, providing observable coverage of early-return cleanup within the probe's testable process boundary.

"""Headless engine calls for the WinForms viewer (MxZipViewer.exe): no Qt is imported, so they answer within
the viewer's own start-up. The windowed exe has no stdout, so results go to a file the viewer names; a
password comes on stdin (never on the command line, where other processes could read it).

  MxZip.exe --list-json    ARCHIVE RESULT.json [--password-stdin]
  MxZip.exe --extract-to   ARCHIVE DEST RESULT.json [--password-stdin] [--rename] [-- MEMBER ...]
  MxZip.exe --test-json    ARCHIVE RESULT.json [--password-stdin]
  MxZip.exe --delete-json  ARCHIVE RESULT.json [--password-stdin] -- MEMBER ...
  MxZip.exe --add-json     ARCHIVE RESULT.json [--password-stdin] [--into FOLDER] -- PATH ...
  MxZip.exe --create-json  OUT RESULT.json [--password-stdin] [--format ID] [--level N] [--split BYTES]
                           [--exclude "a,b"] [--test] [--each] [--delete-source] [--list FILE] -- PATH ...

RESULT.json: {"ok": true, ...}  or {"ok": false, "error": "password" | "missing" | "cancel" | "message",
"message": "..."}. While working, RESULT.json.progress holds "percent<TAB>name" (rewritten at most every
0.1 s); creating RESULT.json.cancel stops the work.

--list-json: "entries": [[name, is_dir, size, csize, "YYYY-MM-DD HH:MM", encrypted], ...], "format",
"editable" (adding / deleting works: a plain, unsplit ZIP), "damaged" (a ZIP cut short: members whose data is
not all there, left out of "entries").  --extract-to: "skipped": [damaged members left out; the rest is
extracted].  --test-json: "bad": [names that failed].
--create-json (the viewer's 새 압축 window): formats.create(); --level 0 stores only; --each makes one archive per
PATH in OUT's folder, named after the item; --test checks every produced archive afterwards; --delete-source sends
the PATHs to the Recycle Bin only when everything succeeded; --list FILE: more PATHs, one per line (UTF-8, the
file is deleted), for selections too long for a command line. "outputs": [the files made]."""
from __future__ import annotations

import json
import os
import sys
import time


def _split(rest: list[str]) -> tuple[list[str], list[str]]:
    """(options, operands): operands follow "--", so a member named like an option stays a member."""
    i = rest.index("--") if "--" in rest else len(rest)
    return rest[:i], rest[i + 1:]


def _password(options: list[str]) -> str | None:
    if "--password-stdin" not in options:
        return None
    data = sys.stdin.buffer.read() if sys.stdin is not None else b""
    return data.decode("utf-8").rstrip("\r\n") or None


def _option(options: list[str], name: str, default: str = "") -> str:
    return options[options.index(name) + 1] if name in options[:-1] else default


def _write(path: str, doc: dict) -> None:
    tmp = path + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, separators=(",", ":"))
    os.replace(tmp, path)


def _progress(result: str):
    """A progress(done, total, name) callback that the viewer can follow, and a cancel() it can trigger."""
    path, stop = result + ".progress", result + ".cancel"
    last = [0.0]

    def progress(done, total, name=""):
        now = time.monotonic()
        if now - last[0] < 0.1 and done < total:
            return
        last[0] = now
        pct = int(done * 100 / total) if total else 100
        try:
            with open(path + ".part", "w", encoding="utf-8") as f:
                f.write(f"{pct}\t{name}")
            os.replace(path + ".part", path)
        except OSError:
            pass

    def cancel() -> bool:
        return os.path.exists(stop)

    return progress, cancel


def _cleanup(result: str) -> None:
    for suffix in (".progress", ".cancel", ".progress.part"):
        try:
            os.remove(result + suffix)
        except OSError:
            pass


def _stamp(mtime) -> str:
    try:
        y, mo, d, h, mi = mtime[:5]
        return f"{y:04d}-{mo:02d}-{d:02d} {h:02d}:{mi:02d}"
    except (TypeError, ValueError):
        return ""


def _failure(e: BaseException) -> dict:
    import ziplib
    if isinstance(e, ziplib.BadPassword):
        return {"ok": False, "error": "password", "message": "암호가 필요하거나 틀렸습니다."}
    if isinstance(e, ziplib.Cancelled):
        return {"ok": False, "error": "cancel", "message": "취소했습니다."}
    if isinstance(e, FileNotFoundError):
        return {"ok": False, "error": "missing",
                "message": f"파일을 찾을 수 없습니다: {os.path.basename(str(e.filename or ''))}"}
    return {"ok": False, "error": "message", "message": _explain(e)}


class UserError(ValueError):
    """A refusal already worded for the user (what is not possible and why); shown as it is."""


def _explain(e: BaseException) -> str:
    """What failed and what to do next, for the errors people actually meet; the original text follows."""
    detail = str(e) or type(e).__name__
    if isinstance(e, UserError):
        return detail
    errno = getattr(e, "errno", None)
    winerror = getattr(e, "winerror", None)
    if isinstance(e, PermissionError) and winerror in (32, 33):  # sharing / lock violation
        advice = "다른 프로그램이 파일을 쓰고 있습니다. 닫고 다시 시도하세요."
    elif isinstance(e, PermissionError):
        advice = "이 위치에 쓸 권한이 없습니다. 다른 폴더를 고르세요."
    elif errno == 28 or winerror in (39, 112):  # no space left
        advice = "디스크 공간이 부족합니다."
    elif isinstance(e, OSError):
        advice = "파일을 읽거나 쓰지 못했습니다."
    else:
        advice = "압축 파일이 손상되었을 수 있습니다. 원본을 다시 받아 보세요."
    if len(detail) > 120:
        detail = detail[:117] + "..."
    return f"{advice}\n({detail})"


def _run(result: str, work) -> int:
    try:
        doc = work()
        _write(result, dict({"ok": True}, **(doc or {})))
        return 0
    except Exception as e:  # reported to the viewer, which shows it
        _write(result, _failure(e))
        return 1
    finally:
        _cleanup(result)


def _format_name(path: str) -> str:
    import formats
    name = os.path.basename(path).lower()
    for ext in sorted(formats.READ_EXTS, key=len, reverse=True):
        if name.endswith(ext):
            return ext.lstrip(".").upper()
    return os.path.splitext(name)[1].lstrip(".").upper()


def _editable(path: str) -> bool:
    import formats
    import ziplib
    try:
        return formats.is_zip(path) and not ziplib.is_split_first(path)
    except OSError:
        return False


def list_json(args: list[str]) -> int:
    archive, result = args[1], args[2]
    options, _ = _split(args[3:])

    def work():
        import formats
        with formats.open_any(archive, _password(options)) as z:
            rows = [[e.name, bool(e.is_dir), int(e.size or 0), int(e.csize or 0), _stamp(e.mtime),
                     bool(e.encrypted)] for e in z.entries()]
            # a cut-short ZIP (directory rebuilt from the local headers): read-only, damaged members listed apart
            rebuilt = getattr(z, "recovered", None) is not None
            damaged = list(getattr(z, "damaged", []))
        return {"entries": rows, "format": _format_name(archive), "editable": _editable(archive) and not rebuilt,
                "damaged": damaged}
    return _run(result, work)


def extract_to(args: list[str]) -> int:
    """--rename: files already in DEST are kept and the extracted ones become "name (2)" (a folder the user
    picked); without it they are replaced (the viewer's own temp folder)."""
    archive, dest, result = args[1], args[2], args[3]
    options, members = _split(args[4:])
    overwrite = "rename" if "--rename" in options else True
    progress, cancel = _progress(result)

    def work():
        import formats
        with formats.open_any(archive, _password(options)) as z:
            z.extract(dest, members or None, progress=progress, cancel=cancel, overwrite=overwrite)
            skipped = list(getattr(z, "skipped", []))  # damaged members left out, the rest extracted
            return {"skipped": skipped} if skipped else None
    return _run(result, work)


def test_json(args: list[str]) -> int:
    archive, result = args[1], args[2]
    options, _ = _split(args[3:])
    progress, cancel = _progress(result)

    def work():
        import formats
        with formats.open_any(archive, _password(options)) as z:
            return {"bad": list(z.test(progress=progress, cancel=cancel) or [])}
    return _run(result, work)


def delete_json(args: list[str]) -> int:
    archive, result = args[1], args[2]
    options, members = _split(args[3:])
    progress, cancel = _progress(result)

    def work():
        import ziplib
        if not _editable(archive):
            raise UserError("파일 삭제는 ZIP에서만 됩니다.")
        ziplib.edit_zip(archive, drop=tuple(members), progress=progress, cancel=cancel)
    return _run(result, work)


def add_json(args: list[str]) -> int:
    archive, result = args[1], args[2]
    options, paths = _split(args[3:])
    progress, cancel = _progress(result)

    def work():
        import ziplib
        if not _editable(archive):
            raise UserError("파일 추가는 ZIP에서만 됩니다.")
        ziplib.edit_zip(archive, add=paths, password=_password(options), progress=progress, cancel=cancel,
                        into=_option(options, "--into"))
    return _run(result, work)


def _each_target(folder: str, path: str, fmt, used: set) -> str:
    """OUT's folder + the item's name + the format's extension, never an existing file or one made already."""
    path = os.path.abspath(path)
    name = os.path.basename(path.rstrip("\\/"))
    if not fmt.single and not os.path.isdir(path):
        name = os.path.splitext(name)[0] or name  # a.txt -> a.zip; a stream keeps it: a.txt.gz
    base = os.path.join(folder, name)
    target, i = base + fmt.ext, 2
    while os.path.lexists(target) or os.path.exists(target + ".001") or os.path.normcase(target) in used:
        target, i = f"{base} ({i}){fmt.ext}", i + 1
    used.add(os.path.normcase(target))
    return target


def _recycle(paths: list[str]) -> None:
    """Sends files and folders to the Recycle Bin (SHFileOperationW, FOF_ALLOWUNDO); never deletes for good
    without asking: where there is no Recycle Bin, Windows warns first (FOF_WANTNUKEWARNING)."""
    import ctypes
    from ctypes import wintypes

    class SHFILEOPSTRUCTW(ctypes.Structure):
        _fields_ = [("hwnd", wintypes.HWND), ("wFunc", wintypes.UINT), ("pFrom", wintypes.LPCWSTR),
                    ("pTo", wintypes.LPCWSTR), ("fFlags", wintypes.WORD), ("fAnyOperationsAborted", wintypes.BOOL),
                    ("hNameMappings", wintypes.LPVOID), ("lpszProgressTitle", wintypes.LPCWSTR)]

    FO_DELETE = 0x0003
    FOF_SILENT, FOF_NOCONFIRMATION, FOF_ALLOWUNDO, FOF_NOERRORUI, FOF_WANTNUKEWARNING = \
        0x0004, 0x0010, 0x0040, 0x0400, 0x4000
    op = SHFILEOPSTRUCTW()
    op.wFunc = FO_DELETE
    op.pFrom = "".join(os.path.abspath(p) + "\0" for p in paths) + "\0"  # double-null terminated list
    op.fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_WANTNUKEWARNING
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    shell32.SHFileOperationW.argtypes = [ctypes.POINTER(SHFILEOPSTRUCTW)]
    shell32.SHFileOperationW.restype = ctypes.c_int
    code = shell32.SHFileOperationW(ctypes.byref(op))
    if code != 0 or op.fAnyOperationsAborted:
        raise UserError(f"압축은 끝났지만 원본을 휴지통으로 옮기지 못했습니다. (코드 {code:#x})")


def create_json(args: list[str]) -> int:
    out, result = args[1], args[2]
    options, paths = _split(args[3:])
    progress, cancel = _progress(result)

    def work():
        import appcore
        import formats
        items = list(paths)
        if "--list" in options[:-1]:
            items += appcore.read_list(_option(options, "--list"))
        if not items:
            raise UserError("압축할 파일이 없습니다. 파일이나 폴더를 추가하세요.")
        for p in items:
            if not os.path.exists(p):
                raise FileNotFoundError(2, "not found", p)
        fmt_id = _option(options, "--format", "zip")
        if fmt_id not in formats.BY_ID:
            raise UserError(f"이 압축 형식은 만들 수 없습니다: {fmt_id}")
        fmt = formats.BY_ID[fmt_id]
        level = max(0, min(9, int(_option(options, "--level", "6"))))
        split = int(_option(options, "--split", "0"))
        if split and not fmt.split:
            raise UserError(f"{fmt.label} 형식은 분할 압축을 지원하지 않습니다.")
        if 0 < split < 64 << 10:
            raise UserError("분할 크기는 64KB 이상이어야 합니다.")
        exclude = [e.strip() for e in _option(options, "--exclude").split(",") if e.strip()]
        password = _password(options)
        out_path = os.path.abspath(out)
        folder = os.path.dirname(out_path)
        if "--each" in options:
            used: set = set()
            jobs = [([p], _each_target(folder, p, fmt, used)) for p in items]
        else:
            jobs = [(items, out_path)]
        outputs: list[str] = []
        for group, target in jobs:
            os.makedirs(os.path.dirname(target), exist_ok=True)
            outputs += formats.create(fmt_id, group, target, level=max(1, level), password=password,
                                      store_only=level == 0, split_size=split, exclude=exclude,
                                      progress=progress, cancel=cancel)
        if "--test" in options:
            firsts = [t for _, t in jobs]
            firsts = [t + ".001" if not os.path.exists(t) and os.path.exists(t + ".001") else t for t in firsts]
            for arc in firsts:
                with formats.open_any(arc, password) as z:
                    bad = list(z.test(progress=progress, cancel=cancel) or [])
                if bad:
                    more = f"\n외 {len(bad) - 5}개" if len(bad) > 5 else ""
                    raise UserError(f"압축 후 테스트 실패: {os.path.basename(arc)}\n" + "\n".join(bad[:5]) + more
                                    + "\n원본은 지우지 않았습니다.")
        if "--delete-source" in options:
            _recycle(items)
        return {"outputs": outputs}
    return _run(result, work)


def update_check(args: list[str]) -> int:
    """--update-check RESULT: the automatic update check MxZip's Qt window ran at every start (when enabled in
    the settings, installed exe only). "due": whether a check ran; "version": a newer release, else ""."""
    result = args[1]

    def work():
        from datetime import datetime
        import appcore
        import updater
        if not updater.due(appcore.load_settings()) or not (
                getattr(sys, "frozen", False) or os.environ.get("MXZIP_UPDATE_URL")):
            return {"due": False, "version": ""}
        try:
            rel = updater.check()
        except Exception as e:
            appcore.save_setting("update_last_result", f"업데이트 확인 실패: {e}")
            raise
        from version import __version__
        appcore.save_setting("update_last_checked", datetime.now().astimezone().isoformat(timespec="seconds"))
        appcore.save_setting("update_last_result", f"새 버전: {rel.version}" if rel else f"최신 버전: {__version__}")
        return {"due": True, "version": rel.version if rel else ""}
    return _run(result, work)


COMMANDS = {"--list-json": (list_json, 3), "--extract-to": (extract_to, 4), "--test-json": (test_json, 3),
            "--delete-json": (delete_json, 3), "--add-json": (add_json, 3), "--create-json": (create_json, 3),
            "--update-check": (update_check, 2)}


def dispatch(args: list[str]) -> int | None:
    """The exit code when args are a viewer engine call, else None."""
    if args and args[0] in COMMANDS and len(args) >= COMMANDS[args[0]][1]:
        return COMMANDS[args[0]][0](args)
    return None

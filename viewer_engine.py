"""Headless engine calls for the WinForms viewer (MxZipViewer.exe): no Qt is imported, so they answer within
the viewer's own start-up. The windowed exe has no stdout, so results go to a file the viewer names; a
password comes on stdin (never on the command line, where other processes could read it).

  MxZip.exe --list-json    ARCHIVE RESULT.json [--password-stdin]
  MxZip.exe --extract-to   ARCHIVE DEST RESULT.json [--password-stdin] [--rename] [-- MEMBER ...]
  MxZip.exe --test-json    ARCHIVE RESULT.json [--password-stdin]
  MxZip.exe --delete-json  ARCHIVE RESULT.json [--password-stdin] -- MEMBER ...
  MxZip.exe --add-json     ARCHIVE RESULT.json [--password-stdin] [--into FOLDER] -- PATH ...

RESULT.json: {"ok": true, ...}  or {"ok": false, "error": "password" | "missing" | "cancel" | "message",
"message": "..."}. While working, RESULT.json.progress holds "percent<TAB>name" (rewritten at most every
0.1 s); creating RESULT.json.cancel stops the work.

--list-json: "entries": [[name, is_dir, size, csize, "YYYY-MM-DD HH:MM", encrypted], ...], "format",
"editable" (adding / deleting works: a plain, unsplit ZIP).  --test-json: "bad": [names that failed]."""
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
        return {"ok": False, "error": "missing", "message": f"파일을 찾을 수 없습니다: {e.filename or ''}"}
    return {"ok": False, "error": "message", "message": str(e) or type(e).__name__}


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
        return {"entries": rows, "format": _format_name(archive), "editable": _editable(archive)}
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
            raise ValueError("파일 삭제는 ZIP 형식에서만 됩니다.")
        ziplib.edit_zip(archive, drop=tuple(members), progress=progress, cancel=cancel)
    return _run(result, work)


def add_json(args: list[str]) -> int:
    archive, result = args[1], args[2]
    options, paths = _split(args[3:])
    progress, cancel = _progress(result)

    def work():
        import ziplib
        if not _editable(archive):
            raise ValueError("파일 추가는 ZIP 형식에서만 됩니다.")
        ziplib.edit_zip(archive, add=paths, password=_password(options), progress=progress, cancel=cancel,
                        into=_option(options, "--into"))
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
            "--delete-json": (delete_json, 3), "--add-json": (add_json, 3), "--update-check": (update_check, 2)}


def dispatch(args: list[str]) -> int | None:
    """The exit code when args are a viewer engine call, else None."""
    if args and args[0] in COMMANDS and len(args) >= COMMANDS[args[0]][1]:
        return COMMANDS[args[0]][0](args)
    return None

"""Exe entry point. "<name>.zip으로 압축하기" (--archive-list LIST --out ZIP) starts compressing before Qt is
imported (PySide6 and the GUI module take ~0.2 s to load), then the progress window follows the running job."""
import os
import sys


def _open_in_viewer(args: list[str]) -> bool:
    """MxZip.exe with no argument (Start menu, the installer's "launch MxZip") or with one archive: the WinForms
    window (MxZipViewer.exe, its "drop files here" page or the archive) instead of the Qt window, where it can
    run (installed exe, .NET Desktop runtime present). Whether it was started; MXZIP_QT_HOME=1 keeps the Qt one."""
    if not getattr(sys, "frozen", False) or os.environ.get("MXZIP_QT_HOME"):
        return False
    env = None
    if args == ["--updated"] and not os.environ.get("MXZIP_UPDATE_TEST_MARKER"):
        # the installer's relaunch after an update: the new window, telling which version is now installed
        from version import __version__
        env = dict(os.environ, MXZIP_VIEWER_NOTICE=f"MxZip {__version__}(으)로 업데이트했습니다.")
        args = []
    if args and (len(args) > 1 or args[0].startswith("-") or not os.path.isfile(args[0])):
        return False
    import subprocess
    import assoc
    viewer = assoc.viewer_for(sys.executable)
    if not viewer:
        return False
    try:
        subprocess.Popen([viewer] + [os.path.abspath(a) for a in args], close_fds=True, env=env,
                         creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
        return True
    except OSError:
        return False


def main() -> int:
    import appcore
    settings = appcore.apply_runtime_settings(strict=False)
    args = sys.argv[1:]
    if _open_in_viewer(args):
        return 0
    if args and args[0] in ("--list-json", "--extract-to", "--test-json", "--delete-json", "--add-json",
                            "--update-check"):
        # the WinForms viewer's engine calls: no Qt
        import viewer_engine
        code = viewer_engine.dispatch(args)
        if code is not None:
            return code
    job = None
    if args[:1] == ["--archive-list"] and len(args) >= 4 and args[2] == "--out":
        # Keep the selection available for elevation or GUI fallback.
        with open(args[1], encoding="utf-8") as selection:
            paths = [line.rstrip("\r") for line in selection.read().split("\n") if line.strip("\r")]
        if not paths:
            appcore.read_list(args[1])
            return 0
        # "--each": the shell's "각각 ZIP 파일로 압축하기"; without it the one archive the menu named
        each = "--each" in args[4:]
        targets = appcore.quick_targets(paths, args[3], settings, explicit_zip=True, separate=each)
        writable = all(appcore.can_write(os.path.dirname(os.path.abspath(out))) for _, out in targets)
        # a folder only administrators may write to (C:\): the same command as administrator, like the third-party archiver
        # (Windows asks in its UAC prompt); if that is refused, the GUI offers another place
        if not writable and not os.environ.get(
                "MXZIP_AUTOACCEPT") and appcore.run_elevated(args):
            return 0
        if writable:
            try:
                paths = appcore.read_list(args[1])
                if not paths:
                    return 0
                job = appcore.quick_compress(paths, args[3], separate=each)
            except Exception as e:
                appcore.log_exception(e)
                raise
    import mxzip_gui
    mxzip_gui.finish(mxzip_gui.attach_job(job) if job is not None else mxzip_gui.main() or 0)
    return 0


if __name__ == "__main__":
    import appcore
    try:
        code = main()
    except BaseException as e:  # still leave through hard_exit: a normal exit runs shiboken6's DLL detach
        if not isinstance(e, SystemExit):
            appcore.log_exception(e)
        code = e.code if isinstance(e, SystemExit) and isinstance(e.code, int) else 1
    appcore.hard_exit(code)

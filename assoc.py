"""Archive file associations, per user (HKCU, no admin).

Registers a ProgID per extension (MxZip.zip, MxZip.7z, ...), lists MxZip under each extension's "Open with", and
publishes Capabilities + RegisteredApplications so Settings > Default apps knows MxZip. Windows 10/11 keep the
user's own choice (UserChoice, hash-protected) out of reach of programs: where one exists, only the user can
switch it in Settings, so status() reads the handler Windows really uses (AssocQueryString) instead of
trusting what was written. Where no choice exists, the extension's default is pointed at MxZip; the value it
had is kept and put back by unregister(). A choice left behind by an uninstalled app (its ProgID is gone) is
removed, since Windows would otherwise keep showing a blank icon instead of using the default."""
from __future__ import annotations

import ctypes
import os
import sys
import winreg
from ctypes import wintypes

from formats import SUPPORTED_TYPES

APP = "MxZip"
CAP_KEY = r"Software\MxZipAssoc\Capabilities"
REG_APPS = r"Software\RegisteredApplications"
LEGACY_APP = "yzip"  # the app's former name: ProgIDs yzip.zip, ..., Software\yzipAssoc
H = winreg.HKEY_CURRENT_USER

# extension -> type name shown by Explorer
# All readable types are offered; executables must never be associated.
TYPES = {ext: label for ext, label in SUPPORTED_TYPES.items() if ext != ".exe"}


def _extensions(exts):
    """None selects all; an empty selection stays empty. Never accept .exe."""
    selected = list(TYPES if exts is None else exts)
    if any(ext not in TYPES for ext in selected):
        raise ValueError("Unsupported archive extension")
    return list(dict.fromkeys(selected))


def exe_path() -> str:
    return sys.executable if getattr(sys, "frozen", False) else os.path.abspath(sys.argv[0])


def open_command(exe: str) -> str:
    if getattr(sys, "frozen", False) or exe.lower().endswith(".exe") and not exe.lower().endswith("python.exe"):
        return f'"{exe}" "%1"'
    return f'"{sys.executable}" "{exe}" "%1"'


def progid(ext: str) -> str:
    return APP + ext


def _set(path, name, value):
    with winreg.CreateKey(H, path) as k:
        winreg.SetValueEx(k, name, 0, winreg.REG_SZ, value)


def _get(path, name=""):
    try:
        with winreg.OpenKey(H, path) as k:
            return winreg.QueryValueEx(k, name)[0]
    except OSError:
        return None


def _del_value(path, name):
    try:
        with winreg.OpenKey(H, path, 0, winreg.KEY_SET_VALUE) as k:
            winreg.DeleteValue(k, name)
    except OSError:
        pass


def _del_tree(path):
    try:
        with winreg.OpenKey(H, path) as k:
            while True:
                try:
                    sub = winreg.EnumKey(k, 0)
                except OSError:
                    break
                _del_tree(path + "\\" + sub)
        winreg.DeleteKey(H, path)
    except OSError:
        pass


FILE_EXTS = r"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts"


def _progid_exists(pid: str) -> bool:
    try:
        winreg.CloseKey(winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, pid))
        return True
    except OSError:
        return False


def clear_stale_choice(ext: str) -> bool:
    """Drop the user's choice for ext when it names a ProgID that no longer exists (an uninstalled app).
    Windows still obeys such a choice, so the files show a blank icon and open the "Open with" dialog,
    and the extension's default never applies. A choice naming an installed app is left alone.
    Returns whether a stale choice was removed."""
    removed = False
    for key, sub in (("UserChoice", ""), ("UserChoiceLatest", r"\ProgId")):
        path = rf"{FILE_EXTS}\{ext}\{key}"
        pid = _get(path + sub, "ProgId")
        if pid and not _progid_exists(pid):
            _del_tree(path)
            removed = removed or _get(path + sub, "ProgId") is None
    return removed


def notify():
    """Tell Explorer that associations changed (icons, Open with, defaults)."""
    ctypes.windll.shell32.SHChangeNotify(0x08000000, 0, None, None)  # SHCNE_ASSOCCHANGED, SHCNF_IDLIST


def register(exts=None, exe: str | None = None, icon: str | None = None) -> None:
    exts = _extensions(exts)
    if not exts:
        return
    exe = exe or exe_path()
    icon = icon or (exe + ",0" if exe.lower().endswith(".exe") else exe)
    cmd = open_command(exe)
    remove_legacy()
    _set(CAP_KEY, "ApplicationName", "MxZip")  # shown in Default apps
    _set(CAP_KEY, "ApplicationDescription", "가볍고 빠른 압축 프로그램")
    _set(REG_APPS, APP, CAP_KEY)
    for ext in exts:
        pid = progid(ext)
        base = rf"Software\Classes\{pid}"
        _set(base, "", TYPES.get(ext, ext.upper().lstrip(".") + " 파일"))
        _set(base + r"\DefaultIcon", "", icon)
        _set(base + r"\shell\open\command", "", cmd)
        _set(rf"Software\Classes\{ext}\OpenWithProgids", pid, "")
        _set(CAP_KEY + r"\FileAssociations", ext, pid)
        cls = rf"Software\Classes\{ext}"
        cur = _get(cls)
        if cur != pid:
            if cur and _get(base, "PreviousDefault") is None:
                _set(base, "PreviousDefault", cur)
            _set(cls, "", pid)  # used by Windows only where the user has made no choice of their own
        clear_stale_choice(ext)
    notify()


def _drop_progids(app: str, exts) -> None:
    """Remove app's ProgID of each extension, giving the extension back its previous default."""
    for ext in exts:
        pid = app + ext
        base = rf"Software\Classes\{pid}"
        cls = rf"Software\Classes\{ext}"
        if _get(cls) == pid:
            prev = _get(base, "PreviousDefault")
            if prev:
                _set(cls, "", prev)
            else:
                _del_value(cls, "")
        _del_value(cls + r"\OpenWithProgids", pid)
        _del_tree(base)


def remove_legacy() -> None:
    """Associations written under the former name yzip (an install before the rename to MxZip)."""
    _drop_progids(LEGACY_APP, TYPES)
    _del_tree(rf"Software\{LEGACY_APP}Assoc")
    _del_value(REG_APPS, LEGACY_APP)


def unregister(exts=None) -> None:
    all_types = exts is None
    exts = _extensions(exts)
    if not exts:
        return
    _drop_progids(APP, exts)
    for ext in exts:
        _del_value(CAP_KEY + r"\FileAssociations", ext)
    if all_types:
        remove_legacy()
        _del_tree(CAP_KEY.rsplit("\\", 1)[0])  # Software\MxZipAssoc
        _del_value(REG_APPS, APP)
    notify()


def enabled(exts=None) -> bool:
    """Whether MxZip's per-user ProgID is registered for every requested extension."""
    return all(_get(rf"Software\Classes\{progid(ext)}") is not None for ext in _extensions(exts))


def effective(ext: str) -> tuple[str, str]:
    """(executable, friendly app name) Windows opens ext with right now, '' when nothing is set."""
    out = []
    for what in (2, 4):  # ASSOCSTR_EXECUTABLE, ASSOCSTR_FRIENDLYAPPNAME
        size = wintypes.DWORD(1024)
        buf = ctypes.create_unicode_buffer(1024)
        hr = ctypes.windll.shlwapi.AssocQueryStringW(0, what, ext, "open", buf, ctypes.byref(size))
        out.append(buf.value if hr == 0 else "")
    return out[0], out[1]


def status(exts=None, exe: str | None = None) -> dict:
    """ext -> (is_mxzip, app name) as Windows resolves it now."""
    exe = os.path.normcase(os.path.abspath(exe or exe_path()))
    res = {}
    for ext in _extensions(exts):
        path, name = effective(ext)
        res[ext] = (bool(path) and os.path.normcase(os.path.abspath(path)) == exe, name or "(없음)")
    return res


def open_default_apps():
    """Settings > Default apps, on MxZip's page where Windows supports it."""
    os.startfile(f"ms-settings:defaultapps?registeredAppUser={APP}")

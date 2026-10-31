"""Executable version contracts shared by the build generator and release tests."""
from __future__ import annotations

import pathlib
import re


def version_quad(value: str) -> tuple[int, int, int, int]:
    parts = value.split(".")
    if not 1 <= len(parts) <= 4 or any(not part.isdigit() for part in parts):
        raise ValueError(f"invalid numeric version: {value}")
    numbers = [int(part) for part in parts]
    return tuple((numbers + [0] * 4)[:4])


def write_version_resources(root: pathlib.Path, value: str) -> None:
    quad = version_quad(value)
    build = root / "build"
    build.mkdir(parents=True, exist_ok=True)
    (build / "version_info.txt").write_text(f"""VSVersionInfo(
  ffi=FixedFileInfo(filevers={quad}, prodvers={quad}, mask=0x3f, flags=0x0, OS=0x40004, fileType=0x1,
                    subtype=0x0, date=(0, 0)),
  kids=[
    StringFileInfo([StringTable('040904B0', [
      StringStruct('CompanyName', 'YMX'),
      StringStruct('FileDescription', 'MxZip archiver'),
      StringStruct('FileVersion', '{value}'),
      StringStruct('InternalName', 'yzip'),
      StringStruct('LegalCopyright', 'Copyright (c) 2026 MrDH1004. MIT License.'),
      StringStruct('OriginalFilename', 'yzip.exe'),
      StringStruct('ProductName', 'MxZip'),
      StringStruct('ProductVersion', '{value}')])]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
""", encoding="utf-8")

    shell = root / "shell" / "build"
    shell.mkdir(parents=True, exist_ok=True)
    shell_quad = ",".join(str(number) for number in quad)
    (shell / "version.h").write_text(
        f'#define YZ_VER {shell_quad}\n#define YZ_VER_STR "{value}"\n', encoding="ascii"
    )


def installer_setup_contract(path: pathlib.Path, app_version: str) -> dict[str, str]:
    """Expand AppVersion in the installer's Setup fields used by release packaging."""
    version_quad(app_version)
    text = path.read_text(encoding="utf-8")
    match = re.search(r"(?ms)^\[Setup\]\s*(.*?)(?=^\[|\Z)", text)
    if match is None:
        raise ValueError("installer has no [Setup] section")
    values: dict[str, str] = {}
    for line in match.group(1).splitlines():
        if not line or line.lstrip().startswith(";") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().replace("{#AppVersion}", app_version)
    required = {"AppVersion", "AppVerName", "VersionInfoVersion", "VersionInfoProductVersion",
                "OutputBaseFilename"}
    missing = required - values.keys()
    if missing:
        raise ValueError(f"installer [Setup] is missing version fields: {sorted(missing)}")
    return values

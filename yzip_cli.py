"""Command line front-end. Command set follows what REA found in Bandizip's bz.exe usage text:
a/c add-create, x extract (full path), e extract flat, t test, l list, d delete, rn rename."""
from __future__ import annotations

import os
import re
import sys

import formats
from runtimeconfig import lazy_import

shutil = lazy_import("shutil")  # 'e' (flat extract) only
tempfile = lazy_import("tempfile")
import ziplib

_SIZE_RE = re.compile(r"^(\d+(?:\.\d+)?)\s*(b|k|kb|m|mb|g|gb)?$", re.I)


def parse_size(s: str) -> int:
    m = _SIZE_RE.match(s.strip())
    if not m:
        import argparse
        raise argparse.ArgumentTypeError(f"bad size: {s}")
    mult = {None: 1, "b": 1, "k": 1 << 10, "kb": 1 << 10, "m": 1 << 20, "mb": 1 << 20,
            "g": 1 << 30, "gb": 1 << 30}[(m.group(2) or "").lower() or None]
    return int(float(m.group(1)) * mult)


def _bar(done, total, name):
    pct = int(done * 100 / total) if total else 100
    sys.stdout.write(f"\r{pct:3d}%  {name[-60:]:<60}")
    sys.stdout.flush()


def build_parser():
    import argparse

    class _LazyThemeFormatter(argparse.HelpFormatter):
        """Python 3.14's HelpFormatter imports _colorize (~5 ms) as soon as a parser exists. The theme is only
        needed to print help or an error, so it is resolved then; output is plain (no colour codes)."""
    
        def __init__(self, prog, indent_increment=2, max_help_position=24, width=None, color=True):
            if width is None:  # what HelpFormatter asks shutil for; shutil itself costs ~6 ms to import
                try:
                    width = int(os.environ.get("COLUMNS", 0)) or os.get_terminal_size().columns
                except (ValueError, OSError):
                    width = 80
                width -= 2
            super().__init__(prog, indent_increment, max_help_position, width, color)
    
        def _set_color(self, color):
            self._decolor = lambda text: text
    
        def __getattr__(self, name):
            if name == "_theme":
                from _colorize import get_theme
                self._theme = get_theme(force_no_color=True).argparse
                return self._theme
            raise AttributeError(name)

    p = argparse.ArgumentParser(prog="yzip", description="archiver: creates zip/7z/tar.*/gz/bz2/xz/zst, reads all of them",
                                formatter_class=_LazyThemeFormatter)
    p.add_argument("command", choices=["a", "c", "x", "e", "t", "l", "d", "rn"])
    p.add_argument("archive")
    p.add_argument("files", nargs="*")
    p.add_argument("-fmt", dest="fmt", default="zip", choices=list(formats.BY_ID),
                   help="format for c (a/d/rn edit ZIP only)")
    p.add_argument("-l", dest="level", type=int, default=5, choices=range(0, 10), metavar="0-9")
    p.add_argument("-p", dest="password")
    p.add_argument("-o", dest="out", default=".")
    p.add_argument("-v", dest="volume", type=parse_size, default=0)
    p.add_argument("-y", action="store_true", help="assume yes")
    ow = p.add_mutually_exclusive_group()
    ow.add_argument("-aoa", dest="ow", action="store_const", const="all", default="all")
    ow.add_argument("-aos", dest="ow", action="store_const", const="skip")
    ow.add_argument("-aou", dest="ow", action="store_const", const="rename")
    return p


class _Args:
    """What parse_args returns, for the common command lines _quick_args understands."""

    def __init__(self, **kw):
        self.__dict__.update(kw)


_FLAGS = {"-y": ("y", True), "-aoa": ("ow", "all"), "-aos": ("ow", "skip"), "-aou": ("ow", "rename")}
_VALUED = {"-fmt": "fmt", "-l": "level", "-p": "password", "-o": "out", "-v": "volume"}


def _quick_args(argv):
    """Parse plain command lines (exact option tokens, values that do not start with '-') without argparse,
    which with its help formatter costs ~5-10 ms of every start. Anything else -- help, an unknown or joined
    option, a bad value, two overwrite modes -- returns None and argparse handles it, errors included."""
    if not argv or argv[0] not in ("a", "c", "x", "e", "t", "l", "d", "rn"):
        return None
    a = {"command": argv[0], "fmt": "zip", "level": 5, "password": None, "out": ".", "volume": 0, "y": False,
         "ow": "all"}
    positional, ow_given, i = [], 0, 1
    while i < len(argv):
        tok = argv[i]
        if tok in _FLAGS:
            key, value = _FLAGS[tok]
            ow_given += key == "ow"
            a[key] = value
        elif tok in _VALUED:
            if i + 1 >= len(argv) or argv[i + 1].startswith("-"):
                return None
            value = argv[i + 1]
            key = _VALUED[tok]
            try:
                if key == "fmt" and value not in formats.BY_ID:
                    return None
                if key == "level":
                    value = int(value)
                    if not 0 <= value <= 9:
                        return None
                if key == "volume":
                    value = parse_size(value)
            except Exception:
                return None
            a[key] = value
            i += 1
        elif tok.startswith("-"):
            return None
        else:
            positional.append(tok)
        i += 1
    if not positional or ow_given > 1:
        return None
    return _Args(archive=positional[0], files=positional[1:], **a)


def main(argv=None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    argv = [x for arg in argv for x in (arg.split(":", 1) if arg.startswith("-fmt:") else [arg])]  # bz.exe style
    a = _quick_args(argv) or build_parser().parse_args(argv)
    cmd = a.command
    try:
        if cmd in ("a", "c"):
            if not a.files:
                print("no input files", file=sys.stderr)
                return 2
            files = a.files
            if cmd == "a" and os.path.exists(a.archive):  # "c" replaces the archive once the new one is complete
                return _add_existing(a)
            level = 0 if a.level == 0 else max(1, min(9, a.level))
            parts = formats.create(a.fmt if cmd == "c" else "zip", files, a.archive, level=max(1, level),
                                   password=a.password, store_only=a.level == 0, split_size=a.volume, progress=_bar)
            print(f"\ncreated: {', '.join(parts)}")
        elif cmd in ("x", "e"):
            with formats.open_any(a.archive, a.password) as z:
                if cmd == "x":
                    z.extract(a.out, a.files or None, progress=_bar, overwrite={"all": True, "skip": False, "rename": "rename"}[a.ow])
                else:
                    _extract_flat(z, a.out, a.files or None, a.ow)
            print("\ndone")
        elif cmd == "t":
            with formats.open_any(a.archive, a.password) as z:
                bad = z.test(progress=_bar)
            print("\nOK" if not bad else "\nBROKEN: " + ", ".join(bad))
            return 0 if not bad else 1
        elif cmd == "l":
            with formats.open_any(a.archive, a.password) as z:
                for e in z.entries():
                    print(f"{e.size:>12} {e.csize:>12} {e.method:<8} {'*' if e.encrypted else ' '} {e.name}")
        elif cmd == "d":
            ziplib.edit_zip(a.archive, drop=a.files)
            print("deleted")
        elif cmd == "rn":
            if len(a.files) % 2:
                print("rn needs pairs: old new [old new ...]", file=sys.stderr)
                return 2
            ziplib.edit_zip(a.archive, renames=dict(zip(a.files[::2], a.files[1::2])))
            print("renamed")
    except ziplib.BadPassword:
        print("wrong or missing password", file=sys.stderr)
        return 3
    except FileNotFoundError as e:
        print(f"not found: {e.filename}", file=sys.stderr)
        return 4
    except ValueError as e:  # format rules, e.g. a single-file format given a folder
        print(str(e), file=sys.stderr)
        return 2
    except ziplib.Cancelled:
        return 130
    except Exception as e:  # corrupt archive, disk full, ...: a message, not a traceback
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


def _add_existing(a) -> int:
    """Add files to an existing zip: old entries are copied as they are, same names are replaced."""
    if not formats.is_zip(a.archive):
        print("a: adding to an existing archive works for ZIP only", file=sys.stderr)
        return 2
    ziplib.edit_zip(a.archive, add=a.files, password=a.password, level=max(1, a.level),
                    store_only=a.level == 0, progress=_bar)
    print("\nupdated")
    return 0


def _extract_flat(z, out, names, ow="all"):
    """e: every file straight into out (no folders). Each one is extracted into a private temp folder first and
    moved from where the extractor actually wrote it, so a hostile name cannot point the move elsewhere."""
    out = os.path.realpath(out)
    os.makedirs(out, exist_ok=True)
    for e in z.entries():
        if e.is_dir or (names and e.name not in names):
            continue
        tmp = os.path.realpath(tempfile.mkdtemp())
        try:
            z.extract(tmp, [e.name])
            src = ziplib.safe_target(tmp, e.name)
            if src is None or not os.path.isfile(src):
                continue
            dst = os.path.join(out, os.path.basename(src))
            if os.path.exists(dst):
                if ow == "skip":
                    continue
                if ow == "rename":
                    base, ext = os.path.splitext(dst)
                    i = 2
                    while os.path.exists(f"{base} ({i}){ext}"):
                        i += 1
                    dst = f"{base} ({i}){ext}"
            shutil.move(src, dst)  # temp folder may be on another drive
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

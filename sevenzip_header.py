"""A small 7z header reader (7zFormat.txt) for the layouts lzmamt.fast_extract decodes: folders of one coder,
LZMA2 or COPY, optionally behind 7zAES. It replaces py7zr for listing and extracting such archives: importing
py7zr (its codecs, Cryptodome, cffi, ...) takes ~0.13 s, longer than reading a small archive.

read(path, password) -> Header or raises:
  Unsupported  any other layout (BCJ, PPMd, LZMA data, several coders, ...): the caller uses py7zr
  NeedPassword the header is encrypted and no password was given
  BadHeader    damaged / wrong password (for an encrypted header)
The result mirrors what SevenZArchive took from py7zr: ArchiveFile-like entries (filename, is_directory,
is_file, is_symlink, uncompressed, compressed, crc32, creationtime, emptystream, id) and the folder list in
lzmamt.fast_plan's format."""
from __future__ import annotations

import lzma
import struct
import zlib
from datetime import datetime, timedelta, timezone

from lzmamt import Unsupported, _ID_AES, _ID_COPY, _ID_LZMA2, _aes_reader

_SIG = b"7z\xbc\xaf\x27\x1c"
_ID_LZMA = b"\x03\x01\x01"
_EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)
_MAX_HEADER = 256 << 20  # a header (even encoded) larger than this is not decoded in memory here


class NeedPassword(Exception):
    pass


class BadHeader(Exception):
    pass


class Entry:
    """The ArchiveFile / FileInfo attributes SevenZArchive uses."""
    __slots__ = ("id", "filename", "emptystream", "emptyfile", "attributes", "mtime", "uncompressed", "compressed",
                 "crc32", "creationtime")

    def __init__(self, index: int):
        self.id = index
        self.filename = ""
        self.emptystream = False
        self.emptyfile = False
        self.attributes = None
        self.mtime = None
        self.uncompressed = 0
        self.compressed = 0
        self.crc32 = None
        self.creationtime = None

    def _bit(self, mask: int) -> bool:
        return self.attributes is not None and self.attributes & mask == mask

    def _unix(self):
        return self.attributes >> 16 if self._bit(0x8000) else None

    @property
    def is_directory(self) -> bool:
        return self._bit(0x10)

    @property
    def is_symlink(self) -> bool:
        u = self._unix()
        if u is not None:
            return u & 0o170000 == 0o120000
        return self._bit(0x400)

    @property
    def is_file(self) -> bool:
        u = self._unix()
        if u is not None:
            return u & 0o170000 == 0o100000
        return not (self.is_directory or self.is_symlink or self._bit(0x410))


class Header:
    def __init__(self, files, folders, need_password: bool):
        self.files, self.folders, self.need_password = files, folders, need_password


class _R:
    """Cursor over header bytes."""

    def __init__(self, b: bytes):
        self.b, self.i = b, 0

    def byte(self) -> int:
        if self.i >= len(self.b):
            raise BadHeader("header ends early")
        v = self.b[self.i]
        self.i += 1
        return v

    def take(self, n: int) -> bytes:
        if n < 0 or self.i + n > len(self.b):
            raise BadHeader("header ends early")
        v = self.b[self.i:self.i + n]
        self.i += n
        return v

    def num(self) -> int:
        first = self.byte()
        mask, value = 0x80, 0
        for k in range(8):
            if not first & mask:
                return value | ((first & (mask - 1)) << (8 * k))
            value |= self.byte() << (8 * k)
            mask >>= 1
        return value

    def count(self, limit: int = 1 << 24) -> int:
        n = self.num()
        if n > limit:
            raise BadHeader("implausible count")
        return n

    def u32(self) -> int:
        return struct.unpack("<I", self.take(4))[0]

    def u64(self) -> int:
        return struct.unpack("<Q", self.take(8))[0]

    def bits(self, n: int) -> list[bool]:
        out, cur, mask = [], 0, 0
        for _ in range(n):
            if not mask:
                cur, mask = self.byte(), 0x80
            out.append(bool(cur & mask))
            mask >>= 1
        return out

    def defined(self, n: int) -> list[bool]:
        return [True] * n if self.byte() else self.bits(n)


def _digests(r: _R, n: int):
    d = r.defined(n)
    return d, [r.u32() if x else None for x in d]


def _folder(r: _R) -> dict:
    coders = []
    n_in = n_out = 0
    for _ in range(r.count(64)):
        flags = r.byte()
        if flags & 0xC0:
            raise Unsupported("alternative coder methods")
        cid = r.take(flags & 0x0F)
        ins = outs = 1
        if flags & 0x10:
            ins, outs = r.count(64), r.count(64)
        props = r.take(r.count(1 << 16)) if flags & 0x20 else b""
        coders.append({"id": cid, "props": props, "ins": ins, "outs": outs})
        n_in, n_out = n_in + ins, n_out + outs
    pairs = [(r.num(), r.num()) for _ in range(n_out - 1)]
    n_packed = n_in - len(pairs)
    packed = [r.num() for _ in range(n_packed)] if n_packed > 1 else [0]
    return {"coders": coders, "pairs": pairs, "packed": packed, "n_out": n_out}


def _streams(r: _R) -> dict:
    s = {"pack_pos": 0, "pack_sizes": [], "folders": [], "unpack": [], "folder_crc": [], "num_sub": None,
         "sub_sizes": None, "sub_crc": None}
    while True:
        t = r.byte()
        if t == 0x00:
            return s
        if t == 0x06:  # PackInfo
            s["pack_pos"] = r.num()
            n = r.count()
            while True:
                k = r.byte()
                if k == 0x00:
                    break
                if k == 0x09:
                    s["pack_sizes"] = [r.num() for _ in range(n)]
                elif k == 0x0A:
                    _digests(r, n)
                else:
                    raise Unsupported("PackInfo property")
        elif t == 0x07:  # UnpackInfo
            if r.byte() != 0x0B:
                raise Unsupported("folder list layout")
            n = r.count()
            if r.byte():
                raise Unsupported("external folders")
            s["folders"] = [_folder(r) for _ in range(n)]
            if r.byte() != 0x0C:
                raise Unsupported("unpack sizes layout")
            s["unpack"] = [[r.num() for _ in range(f["n_out"])] for f in s["folders"]]
            while True:
                k = r.byte()
                if k == 0x00:
                    break
                if k == 0x0A:
                    d, c = _digests(r, n)
                    s["folder_crc"] = c
                else:
                    raise Unsupported("UnpackInfo property")
        elif t == 0x08:  # SubStreamsInfo
            nf = len(s["folders"])
            num = [1] * nf
            k = r.byte()
            if k == 0x0D:
                num = [r.count() for _ in range(nf)]
                k = r.byte()
            sizes = []
            have_sizes = k == 0x09
            for i, f in enumerate(s["folders"]):
                if not num[i]:
                    continue
                total = _final_size(f, s["unpack"][i])
                got = [r.num() for _ in range(num[i] - 1)] if have_sizes else []
                if sum(got) > total:
                    raise BadHeader("substream sizes exceed the folder")
                sizes += got + [total - sum(got)]
            if have_sizes:
                k = r.byte()
            crcs = []
            folder_crc = s["folder_crc"] or [None] * nf
            unknown = sum(n for i, n in enumerate(num) if not (n == 1 and folder_crc[i] is not None))
            given = [None] * unknown
            while k != 0x00:
                if k == 0x0A:
                    _, given = _digests(r, unknown)
                else:
                    raise Unsupported("SubStreamsInfo property")
                k = r.byte()
            it = iter(given)
            for i, n in enumerate(num):
                if n == 1 and folder_crc[i] is not None:
                    crcs.append(folder_crc[i])
                else:
                    crcs += [next(it) for _ in range(n)]
            s["num_sub"], s["sub_sizes"], s["sub_crc"] = num, sizes, crcs
        else:
            raise Unsupported(f"StreamsInfo property {t}")


def _final_size(f: dict, unpack: list[int]) -> int:
    """The folder's main output: the coder output no bind pair consumes."""
    bound = {o for _, o in f["pairs"]}
    free = [i for i in range(f["n_out"]) if i not in bound]
    if len(free) != 1:
        raise BadHeader("folder without a single output")
    return unpack[free[0]]


def _plain_chain(f: dict):
    """(aes props or None, 'lzma2'/'copy'/'lzma', props) for a supported folder, else Unsupported."""
    ids = [c["id"] for c in f["coders"]]
    if any(c["ins"] != 1 or c["outs"] != 1 for c in f["coders"]) or len(f["packed"]) != 1:
        raise Unsupported("coder graph")
    if len(ids) == 2 and ids[0] == _ID_AES:
        if f["pairs"] != [(1, 0)]:  # bind pair (in 1, out 0): the codec (coder 1) reads AES's (coder 0) output
            raise Unsupported("aes chain")
        aes, c = f["coders"][0]["props"], f["coders"][1]
    elif ids == [_ID_AES]:  # AES alone (MxZip's encrypted header): the plaintext is stored
        return f["coders"][0]["props"], "copy", b""
    elif len(ids) == 1:
        aes, c = None, f["coders"][0]
    else:
        raise Unsupported("coders")
    kind = {_ID_LZMA2: "lzma2", _ID_COPY: "copy", _ID_LZMA: "lzma"}.get(c["id"])
    if kind is None:
        raise Unsupported(f"coder {c['id'].hex()}")
    return aes, kind, c["props"]


def _decode_folder_bytes(f, raw: bytes, unpack: list[int], password, limit: int) -> bytes:
    """One small folder (an encoded header) decoded in memory."""
    aes, kind, props = _plain_chain(f)
    size = _final_size(f, unpack)
    if size > limit:
        raise Unsupported("header too large")
    if aes is not None:
        if not password:
            raise NeedPassword("encrypted header")
        import io
        coded = unpack[0]  # AES output (coder 0) = the codec's input
        read = _aes_reader(io.BytesIO(raw), len(raw), coded, aes, password)
        raw = b"".join(iter(lambda: read(1 << 20), b""))
    try:
        if kind == "copy":
            out = raw[:size]
        elif kind == "lzma2":
            d = props[0] if props else 0
            dict_size = min((2 | (d & 1)) << (d // 2 + 11), 1 << 30)
            out = lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=[
                {"id": lzma.FILTER_LZMA2, "dict_size": max(dict_size, 4096)}]).decompress(raw, max_length=size)
        else:  # LZMA1, what 7-Zip and other archivers use for headers
            if len(props) != 5:
                raise BadHeader("LZMA properties")
            lclppb, dict_size = props[0], struct.unpack("<I", props[1:5])[0]
            if lclppb >= 9 * 5 * 5:
                raise BadHeader("LZMA properties")
            pb, rest = divmod(lclppb, 45)
            lp, lc = divmod(rest, 9)
            out = lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=[
                {"id": lzma.FILTER_LZMA1, "lc": lc, "lp": lp, "pb": pb,
                 "dict_size": max(min(dict_size, 1 << 30), 4096)}]).decompress(raw, max_length=size)
    except lzma.LZMAError as e:
        raise BadHeader(str(e))
    if len(out) != size:
        raise BadHeader("header data ends early")
    return out


def _files(r: _R, n: int) -> list[Entry]:
    files = [Entry(i) for i in range(n)]
    empty_stream = [False] * n
    empty_file: list[bool] = []
    while True:
        t = r.num()
        if t == 0:
            break
        size = r.num()
        end = r.i + size
        if end > len(r.b):
            raise BadHeader("file property ends early")
        if t == 0x0E:
            empty_stream = r.bits(n)
        elif t == 0x0F:
            empty_file = r.bits(sum(empty_stream))
        elif t == 0x11:
            if r.byte():
                raise Unsupported("external names")
            names = r.b[r.i:end].decode("utf-16-le", "surrogatepass").split("\0")
            if len(names) < n:
                raise BadHeader("names missing")
            for f, name in zip(files, names):
                f.filename = name
        elif t in (0x12, 0x13, 0x14):
            d = r.defined(n)
            if r.byte():
                raise Unsupported("external times")
            for f, x in zip(files, d):
                v = r.u64() if x else None
                if t == 0x14:
                    f.mtime = v
        elif t == 0x15:
            d = r.defined(n)
            if r.byte():
                raise Unsupported("external attributes")
            for f, x in zip(files, d):
                f.attributes = r.u32() if x else None
        r.i = end  # also skips kDummy, kStartPos, kAnti and unknown properties
    ei = 0
    for f, e in zip(files, empty_stream):
        f.emptystream = e
        if e:
            f.emptyfile = empty_file[ei] if ei < len(empty_file) else False
            ei += 1
    return files


def _filetime_dt(ft: int):
    return _EPOCH + timedelta(microseconds=(ft - 116444736000000000) // 10)


def read(path: str, password=None) -> Header:
    with open(path, "rb") as fh:
        start = fh.read(32)
        if len(start) < 32 or start[:6] != _SIG:
            raise BadHeader("not a 7z archive")
        if zlib.crc32(start[12:32]) != struct.unpack("<I", start[8:12])[0]:
            raise BadHeader("start header CRC")
        off, size, crc = struct.unpack("<QQI", start[12:32])
        if size > _MAX_HEADER:
            raise Unsupported("header too large")
        base = 32
        fh.seek(base + off)
        hdr = fh.read(size)
        if len(hdr) != size or zlib.crc32(hdr) != crc:
            raise BadHeader("header CRC")
        header_encrypted = False
        for _ in range(4):  # an encoded header could in theory encode another one
            r = _R(hdr)
            kind = r.byte()
            if kind == 0x01:
                break
            if kind != 0x17:
                raise BadHeader("header type")
            s = _streams(r)
            if len(s["folders"]) != 1 or len(s["pack_sizes"]) != 1:
                raise Unsupported("encoded header layout")
            f = s["folders"][0]
            header_encrypted = header_encrypted or f["coders"][0]["id"] == _ID_AES
            if s["pack_sizes"][0] > _MAX_HEADER:  # never allocate what a crafted size asks for
                raise Unsupported("encoded header too large")
            fh.seek(base + s["pack_pos"])
            raw = fh.read(s["pack_sizes"][0])
            if len(raw) != s["pack_sizes"][0]:
                raise BadHeader("encoded header ends early")
            try:
                hdr = _decode_folder_bytes(f, raw, s["unpack"][0], password, _MAX_HEADER)
            except BadHeader:
                if header_encrypted:
                    raise NeedPassword("wrong password") from None
                raise
            if s["folder_crc"] and s["folder_crc"][0] is not None and zlib.crc32(hdr) != s["folder_crc"][0]:
                if header_encrypted:
                    raise NeedPassword("wrong password")
                raise BadHeader("encoded header CRC")
        else:
            raise BadHeader("nested encoded headers")
    try:
        return _parse_header(r, base, header_encrypted)
    except (BadHeader, IndexError, UnicodeDecodeError) as e:
        if header_encrypted:  # a wrong key whose garbage still passed the checks
            raise NeedPassword("wrong password") from e
        raise


def _parse_header(r: _R, base: int, header_encrypted: bool) -> Header:
    streams = None
    files: list[Entry] = []
    while True:
        t = r.byte()
        if t == 0x00:
            break
        if t == 0x02:
            raise Unsupported("archive properties")
        if t == 0x03:
            raise Unsupported("additional streams")
        if t == 0x04:
            streams = _streams(r)
        elif t == 0x05:
            files = _files(r, r.count(len(r.b)))  # each file costs header bytes: bounded by its size
        else:
            raise Unsupported("header property")
    folders_out = []
    need = header_encrypted
    with_data = [f for f in files if not f.emptystream]
    if streams and streams["folders"]:
        nf = len(streams["folders"])
        if len(streams["pack_sizes"]) != nf:
            raise Unsupported("pack streams per folder")
        num = streams["num_sub"] or [1] * nf
        sizes = streams["sub_sizes"] or [_final_size(f, u) for f, u in zip(streams["folders"], streams["unpack"])]
        crcs = streams["sub_crc"] or [c for c in (streams["folder_crc"] or [None] * nf)]
        if len(with_data) != sum(num) or len(sizes) != sum(num):
            raise BadHeader("file count does not match the streams")
        pos = base + streams["pack_pos"]
        fi = 0
        for i, f in enumerate(streams["folders"]):
            aes, kind, props = _plain_chain(f)
            if kind == "lzma":
                raise Unsupported("LZMA data")
            need = need or aes is not None
            members = []
            for k in range(num[i]):
                e = with_data[fi]
                e.uncompressed = sizes[fi]
                e.crc32 = crcs[fi] if fi < len(crcs) else None
                e.compressed = streams["pack_sizes"][i] if k == 0 else None
                members.append((e, sizes[fi], e.crc32))
                fi += 1
            folders_out.append({"offset": pos, "packsize": streams["pack_sizes"][i], "aes": aes,
                                "coded": streams["unpack"][i][0] if aes is not None else _final_size(
                                    f, streams["unpack"][i]),
                                "lzma2": kind == "lzma2", "dict": props[0] if kind == "lzma2" and props else 0,
                                "members": members})
            pos += streams["pack_sizes"][i]
    elif with_data:
        raise BadHeader("files without streams")
    last = None
    for e in files:
        if e.mtime is not None:
            last = _filetime_dt(e.mtime)
        e.creationtime = last  # py7zr's list() carries the previous time forward; kept for identical listings
    return Header(files, folders_out, need)

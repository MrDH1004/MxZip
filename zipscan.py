"""Open a ZIP whose central directory is missing or unreadable (a download or copy that stopped early).

The local headers are walked from the start of the file; every member whose data is fully present is kept and a
central directory for those members is rebuilt in memory and appended after the file's own bytes. The result is
handed to zipfile / pyzipper as a file object, so listing, extraction and the CRC checks stay the ordinary ones.
Member offsets are unchanged (the rebuilt directory starts after the last byte of the file), which lets the raw
store/deflate reader in ziplib keep reading the original path directly.

Members whose data runs past the end of the file, or whose length cannot be determined, are reported in
`damaged` and left out."""
from __future__ import annotations

import io
import mmap
import os
import struct
import zipfile
import zlib

_LOCAL = b"PK\x03\x04"
_LOCAL_HEADER = struct.Struct("<4sHHHHHIIIHH")
_DESCRIPTOR = b"PK\x07\x08"
_KNOWN_METHODS = {0, 1, 6, 8, 9, 12, 14, 93, 95, 98, 99}
_NO_LIMIT = 1 << 62


class _Member:
    __slots__ = ("offset", "version", "flags", "method", "time", "date", "crc", "csize", "usize", "raw_name",
                 "extra")

    def __init__(self, offset, version, flags, method, time, date, crc, csize, usize, raw_name, extra):
        self.offset, self.version, self.flags, self.method = offset, version, flags, method
        self.time, self.date, self.crc, self.csize, self.usize = time, date, crc, csize, usize
        self.raw_name, self.extra = raw_name, extra


class _Truncated(ValueError):
    """The member's data runs to the end of the file: the file stops here, nothing real can follow."""


class _NullSink:
    def write(self, data) -> int:
        return len(data)


def _display(raw: bytes, flags: int) -> str:
    if flags & 0x0800:
        return raw.decode("utf-8", "replace")
    try:
        return raw.decode("cp949")
    except UnicodeDecodeError:
        return raw.decode("cp437", "replace")


def _plausible(mm, offset: int) -> bool:
    """A real local header (not four lucky bytes inside compressed data): sane version, method and name."""
    if offset + 30 > len(mm):
        return False
    _sig, version, _flags, method, _t, _d, _crc, _cs, _us, nlen, xlen = _LOCAL_HEADER.unpack_from(mm, offset)
    if (version & 0xFF) > 63 or method not in _KNOWN_METHODS or not 0 < nlen <= 1024 or xlen > 4096:
        return False
    if offset + 30 + nlen > len(mm):
        return False
    return not any(b < 0x20 for b in mm[offset + 30:offset + 30 + nlen])


UNNAMED = "(이름을 읽을 수 없는 마지막 항목)"


def _cut_header(mm, offset: int) -> bool:
    """A local header the end of the file cuts through (before its name ends), with sane leading fields."""
    if offset + 30 > len(mm):  # not even the fixed part: check the fields that are there
        if offset + 5 <= len(mm) and mm[offset + 4] > 63:
            return False
        return offset + 10 > len(mm) or struct.unpack_from("<H", mm, offset + 8)[0] in _KNOWN_METHODS
    nlen = struct.unpack_from("<H", mm, offset + 26)[0]
    _sig, version, _flags, method = struct.unpack_from("<4sHHH", mm, offset)
    return (version & 0xFF) <= 63 and method in _KNOWN_METHODS and 0 < nlen <= 1024 and \
        offset + 30 + nlen > len(mm)


def _zip64_sizes(extra: bytes, csize: int, usize: int) -> tuple[int, int, bool]:
    if csize != 0xFFFFFFFF and usize != 0xFFFFFFFF:
        return csize, usize, False
    cursor = 0
    while cursor + 4 <= len(extra):
        field_id, field_size = struct.unpack_from("<HH", extra, cursor)
        body = extra[cursor + 4:cursor + 4 + field_size]
        if field_id == 0x0001:
            at = 0
            if usize == 0xFFFFFFFF and at + 8 <= len(body):
                usize = struct.unpack_from("<Q", body, at)[0]
                at += 8
            if csize == 0xFFFFFFFF and at + 8 <= len(body):
                csize = struct.unpack_from("<Q", body, at)[0]
            break
        cursor += 4 + field_size
    if csize == 0xFFFFFFFF or usize == 0xFFFFFFFF:
        raise ValueError("ZIP64 size field is missing")
    return csize, usize, True


def _without_zip64(extra: bytes) -> bytes:
    """The local extra fields minus the ZIP64 one (rebuilt for the central directory); AES (0x9901), Unicode
    path (0x7075) and the rest stay, pyzipper and the name repair need them."""
    out, cursor = bytearray(), 0
    while cursor + 4 <= len(extra):
        field_id, field_size = struct.unpack_from("<HH", extra, cursor)
        end = cursor + 4 + field_size
        if end > len(extra):
            break
        if field_id != 0x0001:
            out += extra[cursor:end]
        cursor = end
    return bytes(out)


def _member(mm, offset: int) -> tuple[_Member, int]:
    """The member at offset and the offset just after it; ValueError when its data is not all there."""
    import ziprepair
    _sig, version, flags, method, dos_time, dos_date, crc, csize, usize, nlen, xlen = \
        _LOCAL_HEADER.unpack_from(mm, offset)
    data_start = offset + 30 + nlen + xlen
    if data_start > len(mm):
        raise _Truncated("local header is truncated")
    raw_name = bytes(mm[offset + 30:offset + 30 + nlen])
    extra = bytes(mm[offset + 30 + nlen:data_start])
    csize, usize, zip64 = _zip64_sizes(extra, csize, usize)
    if flags & 0x0008 and not (csize or usize or crc):
        # sizes follow the data (a streamed ZIP): find the end of the data to learn them
        if flags & 1:
            raise ValueError("encrypted member without sizes")
        if method == 8:
            try:
                end, crc, usize = ziprepair._inflate(mm, data_start, None, _NullSink(), _NO_LIMIT)
            except ValueError as e:  # "deflate stream is truncated": the stream reached the end of the file
                raise _Truncated(str(e)) from None
            csize = end - data_start
            if end + 12 > len(mm):  # not even an unsigned descriptor fits
                raise _Truncated("data descriptor is cut off")
            after = ziprepair._descriptor(mm, end, crc, csize, usize, zip64)
        elif method == 0:
            try:  # scans for its descriptor up to the end of the file: not found means the file stops inside
                after, crc, usize = ziprepair._store_with_descriptor(mm, data_start, _NullSink(), _NO_LIMIT, None,
                                                                     zip64)
            except ValueError as e:
                raise _Truncated(str(e)) from None
            csize = usize
        else:
            raise ValueError(f"compression method {method} without sizes")
    else:
        after = data_start + csize
        if after > len(mm):
            raise _Truncated("data runs past the end of the file")
        if flags & 0x0008:  # sizes in the header, a descriptor after the data: its CRC may be the only real one
            width = 20 if zip64 else 12
            signed = mm[after:after + 4] == _DESCRIPTOR
            at = after + (4 if signed else 0)
            if signed or mm[after:after + 4] != _LOCAL:
                if at + 4 <= len(mm) and not crc:
                    crc = struct.unpack_from("<I", mm, at)[0]
                after = min(at + width, len(mm))
    return _Member(offset, version, flags, method, dos_time, dos_date, crc, csize, usize, raw_name, extra), after


def _central(members: list[_Member], cd_offset: int) -> bytes:
    out = bytearray()
    for m in members:
        fields = []
        usize = csize = offset = None
        if m.usize >= 0xFFFFFFFF:
            fields.append(m.usize)
            usize = 0xFFFFFFFF
        if m.csize >= 0xFFFFFFFF:
            fields.append(m.csize)
            csize = 0xFFFFFFFF
        if m.offset >= 0xFFFFFFFF:
            fields.append(m.offset)
            offset = 0xFFFFFFFF
        extra = _without_zip64(m.extra)
        if fields:
            extra = struct.pack("<HH", 0x0001, 8 * len(fields)) + struct.pack(f"<{len(fields)}Q", *fields) + extra
        version = max(m.version & 0xFF, 45 if fields else 20)
        external = 0x10 if m.raw_name.endswith(b"/") else 0
        out += struct.pack("<4sBBHHHHHIIIHHHHHII", b"PK\x01\x02", version, 0, version, m.flags, m.method,
                           m.time, m.date, m.crc, m.csize if csize is None else csize,
                           m.usize if usize is None else usize, len(m.raw_name), len(extra), 0, 0, 0, external,
                           m.offset if offset is None else offset)
        out += m.raw_name + extra
    size = len(out)
    count = len(members)
    if count >= 0xFFFF or size >= 0xFFFFFFFF or cd_offset >= 0xFFFFFFFF:
        eocd64_at = cd_offset + size
        out += struct.pack("<4sQHHIIQQQQ", b"PK\x06\x06", 44, 45, 45, 0, 0, count, count, size, cd_offset)
        out += struct.pack("<4sIQI", b"PK\x06\x07", 0, eocd64_at, 1)
        out += struct.pack("<4sHHHHIIH", b"PK\x05\x06", 0, 0, 0xFFFF, 0xFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0)
    else:
        out += struct.pack("<4sHHHHIIH", b"PK\x05\x06", 0, 0, count, count, size, cd_offset, 0)
    return bytes(out)


class _Joined(io.RawIOBase):
    """The original file followed by the rebuilt central directory, as one read-only seekable file."""

    def __init__(self, path: str, size: int, tail: bytes):
        super().__init__()
        self._f = open(path, "rb")
        self._size, self._tail, self._pos = size, tail, 0

    def readable(self) -> bool:
        return True

    def seekable(self) -> bool:
        return True

    def tell(self) -> int:
        return self._pos

    def seek(self, offset: int, whence: int = 0) -> int:
        base = {0: 0, 1: self._pos, 2: self._size + len(self._tail)}[whence]
        self._pos = max(0, base + offset)
        return self._pos

    def readinto(self, buffer) -> int:
        view = memoryview(buffer).cast("B")
        done = 0
        if self._pos < self._size:
            self._f.seek(self._pos)
            n = self._f.readinto(view[:min(len(view), self._size - self._pos)]) or 0
            done += n
            self._pos += n
            if n == 0:
                return 0
        if done < len(view) and self._pos >= self._size:
            start = self._pos - self._size
            piece = self._tail[start:start + len(view) - done]
            view[done:done + len(piece)] = piece
            done += len(piece)
            self._pos += len(piece)
        return done

    def close(self):
        if not self.closed:
            self._f.close()
        super().close()


class Recovered:
    """members: how many were kept; damaged: display names of those left out (data cut off or unreadable)."""

    def __init__(self, path: str, size: int, tail: bytes, members: int, damaged: list[str]):
        self.path, self._size, self._tail = path, size, tail
        self.members, self.damaged = members, damaged

    def open(self) -> _Joined:
        return _Joined(self.path, self._size, self._tail)


def recover(path: str) -> Recovered:
    """Rebuild the directory of a ZIP whose own directory cannot be read; BadZipFile when nothing is usable."""
    size = os.path.getsize(path)
    if size < 30:
        raise zipfile.BadZipFile("File is not a zip file")
    members: list[_Member] = []
    damaged: list[str] = []
    with open(path, "rb") as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as mm:
        pos = mm.find(_LOCAL)
        while pos >= 0:
            if _cut_header(mm, pos):  # the file stops inside this member's own header
                damaged.append(UNNAMED)
                break
            if not _plausible(mm, pos):
                pos = mm.find(_LOCAL, pos + 4)
                continue
            try:
                member, after = _member(mm, pos)
            except (ValueError, struct.error, zlib.error) as e:
                nlen = struct.unpack_from("<H", mm, pos + 26)[0]
                flags = struct.unpack_from("<H", mm, pos + 6)[0]
                damaged.append(_display(bytes(mm[pos + 30:pos + 30 + nlen]), flags))
                if isinstance(e, _Truncated):
                    # the data runs to the end of the file: anything that looks like a header further on is
                    # inside this member (a .docx or .zip stored in it), never a member of this archive
                    break
                pos = mm.find(_LOCAL, pos + 4)  # corrupt in the middle: look for the next member
                continue
            members.append(member)
            pos = after if mm[after:after + 4] == _LOCAL else mm.find(_LOCAL, after)
    if not members:
        raise zipfile.BadZipFile("File is not a zip file")
    return Recovered(path, size, _central(members, size), len(members), damaged)

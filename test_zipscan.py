"""A ZIP cut short (no central directory): the members that are all there list and extract, the cut one is
reported and skipped (zipscan + ziplib.Archive)."""
import io
import os
import struct
import tempfile
import unittest
import zipfile

import formats
import ziplib
import zipscan


class _NoSeek(io.RawIOBase):
    """A write-only stream: zipfile then writes sizes in data descriptors after each member (streamed ZIP)."""

    def __init__(self, f):
        super().__init__()
        self.f = f

    def writable(self):
        return True

    def write(self, b):
        return self.f.write(b)


def _payload(i: int, size: int) -> bytes:
    return (f"member {i} ".encode() * (size // 9 + 1))[:size] + os.urandom(64)


class ZipScanTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def _zip(self, name, members, method=zipfile.ZIP_DEFLATED, streamed=False):
        path = os.path.join(self.dir, name)
        with open(path, "wb") as raw:
            target = _NoSeek(raw) if streamed else raw
            with zipfile.ZipFile(target, "w", method) as z:
                for arc, data in members:
                    z.writestr(arc, data)
        return path

    def _cut(self, path, keep):
        with open(path, "r+b") as f:
            f.truncate(keep)

    def _offset_of(self, path, name):
        with zipfile.ZipFile(path) as z:
            return z.getinfo(name).header_offset

    def _check_cut(self, method, streamed):
        members = [(f"dir/f{i}.bin", _payload(i, 40000 + i * 1000)) for i in range(4)] + [("last.bin", _payload(9, 90000))]
        path = self._zip("a.zip", members, method, streamed)
        self._cut(path, self._offset_of(path, "last.bin") + 200)  # last member's data stops after 200 bytes
        with self.assertRaises(zipfile.BadZipFile):
            zipfile.ZipFile(path)
        with formats.open_any(path) as z:
            self.assertIsNotNone(z.recovered)
            self.assertEqual(sorted(e.name for e in z.entries()), sorted(n for n, _ in members[:4]))
            self.assertEqual(z.damaged, ["last.bin"])
            out = os.path.join(self.dir, "out")
            z.extract(out)
            self.assertEqual(z.skipped, ["last.bin"])
            self.assertEqual(z.test(), ["last.bin"])
        for arc, data in members[:4]:
            with open(os.path.join(out, *arc.split("/")), "rb") as f:
                self.assertEqual(f.read(), data)
        self.assertFalse(os.path.exists(os.path.join(out, "last.bin")))

    def test_cut_deflate(self):
        self._check_cut(zipfile.ZIP_DEFLATED, streamed=False)

    def test_cut_store(self):
        self._check_cut(zipfile.ZIP_STORED, streamed=False)

    def test_cut_streamed_deflate_with_descriptors(self):
        self._check_cut(zipfile.ZIP_DEFLATED, streamed=True)

    def test_cut_streamed_store_with_descriptors(self):
        self._check_cut(zipfile.ZIP_STORED, streamed=True)

    def test_cut_inside_central_directory(self):
        members = [(f"f{i}.txt", _payload(i, 5000)) for i in range(3)]
        path = self._zip("b.zip", members)
        self._cut(path, os.path.getsize(path) - 30)  # end record and part of the directory gone
        with formats.open_any(path) as z:
            self.assertEqual(len(z.entries()), 3)
            self.assertEqual(z.damaged, [])
            z.extract(os.path.join(self.dir, "out"))
        for arc, data in members:
            with open(os.path.join(self.dir, "out", arc), "rb") as f:
                self.assertEqual(f.read(), data)

    def test_corrupt_member_is_skipped_rest_extracts(self):
        members = [(f"f{i}.bin", _payload(i, 30000)) for i in range(3)] + [("cut.bin", _payload(7, 30000))]
        path = self._zip("c.zip", members, zipfile.ZIP_STORED)
        bad_at = self._offset_of(path, "f1.bin") + 30 + len("f1.bin") + 100
        self._cut(path, self._offset_of(path, "cut.bin") + 100)
        with open(path, "r+b") as f:  # flip bytes inside f1.bin's data: its CRC check fails
            f.seek(bad_at)
            f.write(b"XXXXXXXX")
        with formats.open_any(path) as z:
            z.extract(os.path.join(self.dir, "out"))
            self.assertEqual(sorted(z.skipped), ["cut.bin", "f1.bin"])
        for arc, data in (members[0], members[2]):
            with open(os.path.join(self.dir, "out", arc), "rb") as f:
                self.assertEqual(f.read(), data)
        self.assertFalse(os.path.exists(os.path.join(self.dir, "out", "f1.bin")))

    def test_korean_cp949_names(self):
        path = os.path.join(self.dir, "k.zip")
        with zipfile.ZipFile(path, "w") as z:
            info = zipfile.ZipInfo("출입문/사진.txt")
            z.writestr(info, b"x" * 1000)
            z.writestr("tail.bin", _payload(1, 50000))
        self._cut(path, self._offset_of(path, "tail.bin") + 100)
        with formats.open_any(path) as z:
            self.assertEqual([e.name for e in z.entries()], ["출입문/사진.txt"])
            self.assertEqual(z.damaged, ["tail.bin"])

    def test_aes_member_kept(self):
        import pyzipper
        path = os.path.join(self.dir, "e.zip")
        with pyzipper.AESZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, encryption=pyzipper.WZ_AES) as z:
            z.setpassword(b"pw")
            z.writestr("secret.txt", b"hello" * 500)
            z.writestr("tail.bin", _payload(2, 50000))
        with zipfile.ZipFile(path) as z:
            cut = z.getinfo("tail.bin").header_offset + 100
        self._cut(path, cut)
        with formats.open_any(path, "pw") as z:
            self.assertTrue(z.needs_password())
            z.extract(os.path.join(self.dir, "out"))
            self.assertEqual(z.skipped, ["tail.bin"])
        with open(os.path.join(self.dir, "out", "secret.txt"), "rb") as f:
            self.assertEqual(f.read(), b"hello" * 500)

    def test_zip_stored_inside_cut_member_is_not_taken_for_members(self):
        inner = io.BytesIO()
        with zipfile.ZipFile(inner, "w", zipfile.ZIP_STORED) as z:
            z.writestr("inner_a.txt", b"a" * 3000)
            z.writestr("inner_b.txt", b"b" * 3000)
        path = self._zip("outer.zip", [("first.txt", b"first" * 100), ("doc.docx", inner.getvalue() + os.urandom(20000))],
                         zipfile.ZIP_STORED)
        self._cut(path, self._offset_of(path, "doc.docx") + 30 + 8 + 4500)  # past both inner headers, before its directory
        with formats.open_any(path) as z:
            self.assertEqual([e.name for e in z.entries()], ["first.txt"])
            self.assertEqual(z.damaged, ["doc.docx"])

    def test_descriptor_crc_used_when_header_crc_is_zero(self):
        members = [("a.txt", b"hello world " * 400), ("tail.bin", _payload(3, 40000))]
        path = self._zip("d.zip", members, zipfile.ZIP_DEFLATED)
        with open(path, "r+b") as f:  # rewrite a.txt as "flag 8, sizes in header, CRC only in the descriptor"
            data = bytearray(f.read())
        with zipfile.ZipFile(path) as z:
            info = z.getinfo("a.txt")
            tail_at = z.getinfo("tail.bin").header_offset
        data[6:8] = (info.flag_bits | 8).to_bytes(2, "little")
        data[14:18] = b"\0\0\0\0"
        end = 30 + len(b"a.txt") + info.compress_size
        data[end:end] = b"PK\x07\x08" + struct.pack("<III", info.CRC, info.compress_size, info.file_size)
        with open(path, "wb") as f:
            f.write(bytes(data[:tail_at + 16 + 100]))  # cut inside tail.bin
        with formats.open_any(path) as z:
            z.extract(os.path.join(self.dir, "out"))
            self.assertEqual(z.skipped, ["tail.bin"])
        with open(os.path.join(self.dir, "out", "a.txt"), "rb") as f:
            self.assertEqual(f.read(), members[0][1])

    def test_cut_inside_last_header_is_reported(self):
        members = [("a.txt", b"x" * 2000), ("b_with_a_long_name.txt", b"y" * 2000)]
        path = self._zip("h.zip", members, zipfile.ZIP_STORED)
        self._cut(path, self._offset_of(path, "b_with_a_long_name.txt") + 35)  # inside b's name
        with formats.open_any(path) as z:
            self.assertEqual([e.name for e in z.entries()], ["a.txt"])
            self.assertEqual(z.damaged, [zipscan.UNNAMED])

    def test_extracting_chosen_members_reports_only_their_failures(self):
        members = [("a.txt", b"a" * 5000), ("b.txt", b"b" * 5000), ("last.bin", _payload(4, 50000))]
        path = self._zip("s.zip", members)
        self._cut(path, self._offset_of(path, "last.bin") + 100)
        with formats.open_any(path) as z:
            z.extract(os.path.join(self.dir, "out"), ["a.txt"])
            self.assertEqual(z.skipped, [])
            z.extract(os.path.join(self.dir, "out2"))
            z.extract(os.path.join(self.dir, "out3"))
            self.assertEqual(z.skipped, ["last.bin"])  # not doubled by a second run

    def test_nothing_usable_stays_bad_zip(self):
        path = os.path.join(self.dir, "junk.zip")
        with open(path, "wb") as f:
            f.write(b"PK\x03\x04" + os.urandom(10))
        with self.assertRaises(zipfile.BadZipFile):
            zipscan.recover(path)
        with self.assertRaises(zipfile.BadZipFile):
            ziplib.Archive(path)

    def test_zip64_offsets_in_rebuilt_directory(self):
        members = [zipscan._Member(0x1_0000_0000, 20, 0, 0, 0, 0, 0, 5, 5, b"big.bin", b"")]
        tail = zipscan._central(members, 0x1_2000_0000)
        self.assertIn(b"PK\x06\x06", tail)
        self.assertIn(b"PK\x06\x07", tail)


if __name__ == "__main__":
    unittest.main()

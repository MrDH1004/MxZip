"""sevenzip_header: the py7zr-free 7z header reader must list exactly what py7zr lists, plan the same folders as
lzmamt.fast_plan, refuse what it cannot decode (py7zr then takes over) and never accept a wrong password."""
import os
import subprocess
import sys
import tempfile
import unittest

import py7zr

import formats
import lzmamt
import sevenzip_header as sh
from ziplib import BadPassword

HERE = os.path.dirname(os.path.abspath(__file__))
SEVEN = os.path.join(HERE, "tools", "7z.exe")
BZ = r"C:\Program Files\Bandizip\bz.exe"


def key(e):
    return (e.filename, e.is_directory, e.is_file, e.is_symlink, e.uncompressed, e.compressed, e.crc32,
            e.creationtime)


def plan_key(folders):
    return [(f["offset"], f["packsize"], f["coded"], f["lzma2"], f["dict"], f["aes"],
             [(m[1], m[2]) for m in f["members"]]) for f in folders]


class SevenZipHeaderTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.src = os.path.join(self.root, "자료")
        os.makedirs(os.path.join(self.src, "sub"))
        os.makedirs(os.path.join(self.src, "empty dir"))
        with open(os.path.join(self.src, "a.txt"), "wb") as f:
            f.write(b"hello " * 50000)
        with open(os.path.join(self.src, "zero.bin"), "wb"):
            pass
        with open(os.path.join(self.src, "sub", "r.bin"), "wb") as f:
            f.write(os.urandom(200_000))

    def tearDown(self):
        self.tmp.cleanup()

    def archives(self):
        out = []
        for pw in (None, "암호 pw"):
            path = os.path.join(self.root, f"yzip-{bool(pw)}.7z")
            formats.create("7z", [self.src], path, level=5, password=pw)
            out.append((path, pw, "yzip"))
        if os.path.exists(SEVEN):
            for tag, args, pw in (("7zip", ["-mx5"], None), ("7zip-store", ["-mx0"], None),
                                  ("7zip-nonsolid", ["-mx5", "-ms=off"], None),
                                  ("7zip-he", ["-mx5", "-ppw", "-mhe=on"], "pw"),
                                  ("7zip-pw", ["-mx5", "-ppw", "-mhe=off"], "pw")):
                path = os.path.join(self.root, tag + ".7z")
                subprocess.run([SEVEN, "a", "-y", path, self.src] + args, capture_output=True, check=True)
                out.append((path, pw, tag))
        if os.path.exists(BZ):
            path = os.path.join(self.root, "bandizip.7z")
            subprocess.run([BZ, "c", "-fmt:7z", "-y", path, self.src], capture_output=True)
            if os.path.exists(path):
                out.append((path, None, "bandizip"))
        return out

    def test_same_listing_and_plan_as_py7zr(self):
        for path, pw, tag in self.archives():
            with self.subTest(tag):
                h = sh.read(path, pw)
                with py7zr.SevenZipFile(path, password=pw) as z:
                    ref, need = z.list(), z.needs_password()
                    folders = lzmamt.fast_plan(z)
                self.assertEqual([key(e) for e in h.files], [key(e) for e in ref])
                self.assertEqual(h.need_password, need)
                self.assertEqual(plan_key(h.folders), plan_key(folders))

    def test_password_required_and_wrong(self):
        for path, pw, tag in self.archives():
            if not pw or tag == "7zip-pw":  # names readable without the key there
                continue
            with self.subTest(tag):
                with self.assertRaises(sh.NeedPassword):
                    sh.read(path, None)
                with self.assertRaises(sh.NeedPassword):
                    sh.read(path, "wrong")
                with self.assertRaises(BadPassword):
                    formats.SevenZArchive(path, "wrong")

    def test_wrong_password_for_data_is_reported(self):
        path = os.path.join(self.root, "yzip.7z")
        formats.create("7z", [self.src], path, level=5, password="right")
        with self.assertRaises(BadPassword):
            with formats.open_any(path, "wrong") as a:
                a.extract(os.path.join(self.root, "x"))

    @unittest.skipUnless(os.path.exists(SEVEN), "bundled 7z.exe missing")
    def test_other_coders_fall_back_to_py7zr(self):
        path = os.path.join(self.root, "bcj.7z")
        exe = os.path.join(self.src, "fake.exe")
        with open(exe, "wb") as f:
            f.write(b"MZ" + bytes(range(256)) * 400)
        subprocess.run([SEVEN, "a", "-y", "-mf=BCJ", path, self.src], capture_output=True, check=True)
        with self.assertRaises(lzmamt.Unsupported):
            sh.read(path)
        dest = os.path.join(self.root, "out")
        with formats.open_any(path) as a:
            self.assertIsNone(a.fast)  # py7zr reads it
            self.assertEqual(a.test(), [])
            a.extract(dest)
        with open(os.path.join(dest, "자료", "fake.exe"), "rb") as f, open(exe, "rb") as g:
            self.assertEqual(f.read(), g.read())

    def test_damaged_header_is_rejected(self):
        path = os.path.join(self.root, "d.7z")
        formats.create("7z", [self.src], path, level=5)
        data = bytearray(open(path, "rb").read())
        data[-5] ^= 0xFF  # inside the (encoded) header: its CRC no longer matches
        with open(path, "wb") as f:
            f.write(data)
        with self.assertRaises(sh.BadHeader):
            sh.read(path)

    def test_extract_without_importing_py7zr(self):
        path = os.path.join(self.root, "y.7z")
        formats.create("7z", [self.src], path, level=5)
        dest = os.path.join(self.root, "x")
        code = ("import sys, formats\n"
                f"a = formats.open_any({path!r}); a.extract({dest!r}); assert a.test() == []\n"
                "print('py7zr' in sys.modules)")
        r = subprocess.run([sys.executable, "-c", code], cwd=HERE, capture_output=True, text=True, timeout=120)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.strip(), "False")
        for rel in ("a.txt", "zero.bin", os.path.join("sub", "r.bin")):
            with open(os.path.join(dest, "자료", rel), "rb") as f, open(os.path.join(self.src, rel), "rb") as g:
                self.assertEqual(f.read(), g.read())
        self.assertTrue(os.path.isdir(os.path.join(dest, "자료", "empty dir")))


if __name__ == "__main__":
    unittest.main()

"""viewer_engine: the headless calls MxZipViewer.exe makes (--list-json / --extract-to) answer through a result
file, take the password on stdin only, report a wrong password as "password", and never import Qt."""
import json
import os
import subprocess
import sys
import tempfile
import unittest

import formats

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.join(HERE, "mxzip_main.py")


class ViewerEngineTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.src = os.path.join(self.root, "자료")
        os.makedirs(os.path.join(self.src, "하위"))
        with open(os.path.join(self.src, "a.txt"), "wb") as f:
            f.write(b"hello" * 1000)
        with open(os.path.join(self.src, "하위", "b.bin"), "wb") as f:
            f.write(os.urandom(5000))
        self.result = os.path.join(self.root, "r.json")

    def tearDown(self):
        self.tmp.cleanup()

    def run_engine(self, *args, stdin=b""):
        code = ("import sys, runpy; sys.argv = [%r] + %r; "
                "runpy.run_path(%r, run_name='__main__')") % (MAIN, list(args), MAIN)
        r = subprocess.run([sys.executable, "-c", code], input=stdin, capture_output=True, cwd=HERE, timeout=120)
        with open(self.result, encoding="utf-8") as f:
            return r.returncode, json.load(f)

    def test_list_reports_every_entry(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        code, doc = self.run_engine("--list-json", arc, self.result)
        self.assertEqual(code, 0)
        self.assertTrue(doc["ok"])
        names = {row[0].rstrip("/") for row in doc["entries"]}
        self.assertTrue({"자료/a.txt", "자료/하위/b.bin"} <= names, names)
        row = next(r for r in doc["entries"] if r[0] == "자료/a.txt")
        self.assertEqual(row[1:4], [False, 5000, row[3]])
        self.assertRegex(row[4], r"^\d{4}-\d\d-\d\d \d\d:\d\d$")

    def test_password_comes_on_stdin_and_a_wrong_one_is_reported(self):
        arc = os.path.join(self.root, "p.7z")
        formats.create("7z", [self.src], arc, level=5, password="암호 pw")
        code, doc = self.run_engine("--list-json", arc, self.result)
        self.assertEqual((code, doc["ok"], doc["error"]), (1, False, "password"))
        code, doc = self.run_engine("--list-json", arc, self.result, "--password-stdin", stdin="틀림".encode())
        self.assertEqual(doc["error"], "password")
        code, doc = self.run_engine("--list-json", arc, self.result, "--password-stdin", stdin="암호 pw".encode())
        self.assertTrue(doc["ok"], doc)

    def test_extract_selected_member_only(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        dest = os.path.join(self.root, "out")
        code, doc = self.run_engine("--extract-to", arc, dest, self.result, "--", "자료/하위/b.bin")
        self.assertEqual((code, doc), (0, {"ok": True}))
        self.assertTrue(os.path.isfile(os.path.join(dest, "자료", "하위", "b.bin")))
        self.assertFalse(os.path.exists(os.path.join(dest, "자료", "a.txt")))

    def test_rename_keeps_the_users_file(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        dest = os.path.join(self.root, "out")
        os.makedirs(os.path.join(dest, "자료"))
        with open(os.path.join(dest, "자료", "a.txt"), "wb") as f:
            f.write(b"PRECIOUS")
        code, doc = self.run_engine("--extract-to", arc, dest, self.result, "--rename", "--", "자료/a.txt")
        self.assertEqual((code, doc), (0, {"ok": True}))
        with open(os.path.join(dest, "자료", "a.txt"), "rb") as f:
            self.assertEqual(f.read(), b"PRECIOUS")
        self.assertTrue(os.path.isfile(os.path.join(dest, "자료", "a (2).txt")))

    def test_zip_password_is_asked_for_extracting(self):
        # ZIP names are not encrypted: the listing works without a password, extracting must report "password"
        arc = os.path.join(self.root, "p.zip")
        formats.create("zip", [self.src], arc, level=5, password="pw")
        code, doc = self.run_engine("--list-json", arc, self.result)
        self.assertTrue(doc["ok"])
        self.assertTrue(any(row[5] for row in doc["entries"]))  # flagged encrypted
        dest = os.path.join(self.root, "out")
        code, doc = self.run_engine("--extract-to", arc, dest, self.result, "--", "자료/a.txt")
        self.assertEqual(doc.get("error"), "password", doc)
        code, doc = self.run_engine("--extract-to", arc, dest, self.result, "--password-stdin", "--", "자료/a.txt",
                                    stdin=b"pw")
        self.assertTrue(doc["ok"], doc)

    def test_list_reports_format_and_editability(self):
        z = os.path.join(self.root, "a.zip")
        s = os.path.join(self.root, "a.7z")
        formats.create("zip", [self.src], z, level=5)
        formats.create("7z", [self.src], s, level=5)
        _, doc = self.run_engine("--list-json", z, self.result)
        self.assertEqual((doc["format"], doc["editable"]), ("ZIP", True))
        _, doc = self.run_engine("--list-json", s, self.result)
        self.assertEqual((doc["format"], doc["editable"]), ("7Z", False))

    def test_test_finds_damage(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=0)
        code, doc = self.run_engine("--test-json", arc, self.result)
        self.assertEqual((code, doc), (0, {"ok": True, "bad": []}))
        with open(arc, "rb") as f:
            data = bytearray(f.read())
        i = data.find(b"hellohello")
        data[i:i + 5] = b"XXXXX"  # stored data no longer matches its CRC
        with open(arc, "wb") as f:
            f.write(data)
        code, doc = self.run_engine("--test-json", arc, self.result)
        self.assertTrue(doc["ok"])
        self.assertIn("자료/a.txt", doc["bad"])

    def test_delete_and_add_edit_the_zip(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        code, doc = self.run_engine("--delete-json", arc, self.result, "--", "자료/a.txt")
        self.assertEqual((code, doc), (0, {"ok": True}))
        extra = os.path.join(self.root, "새 파일.txt")
        with open(extra, "w", encoding="utf-8") as f:
            f.write("added")
        code, doc = self.run_engine("--add-json", arc, self.result, "--into", "자료/하위/", "--", extra)
        self.assertEqual((code, doc), (0, {"ok": True}))
        _, doc = self.run_engine("--list-json", arc, self.result)
        names = {row[0] for row in doc["entries"]}
        self.assertNotIn("자료/a.txt", names)
        self.assertIn("자료/하위/새 파일.txt", names)

    def test_editing_other_formats_is_refused(self):
        arc = os.path.join(self.root, "a.7z")
        formats.create("7z", [self.src], arc, level=5)
        code, doc = self.run_engine("--delete-json", arc, self.result, "--", "자료/a.txt")
        self.assertEqual((code, doc["ok"], doc["error"]), (1, False, "message"))

    def test_cancel_file_stops_the_work(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        open(self.result + ".cancel", "w").close()  # already asked to stop
        code, doc = self.run_engine("--extract-to", arc, os.path.join(self.root, "out"), self.result)
        self.assertEqual(doc.get("error"), "cancel", doc)
        self.assertFalse(os.path.exists(self.result + ".cancel"))  # cleaned up

    def test_update_check_is_skipped_from_source(self):
        code = ("import sys, viewer_engine; viewer_engine.dispatch(['--update-check', %r])") % self.result
        env = {k: v for k, v in os.environ.items() if k not in ("MXZIP_UPDATE_URL",)}
        subprocess.run([sys.executable, "-c", code], cwd=HERE, env=env, timeout=60, check=True)
        with open(self.result, encoding="utf-8") as f:
            self.assertEqual(json.load(f), {"ok": True, "due": False, "version": ""})

    def test_plain_launch_opens_the_viewer_when_it_can_run(self):
        import mxzip_main
        from unittest import mock
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        viewer = r"C:\app\MxZipViewer.exe"
        cases = [([], True, [viewer]), ([arc], True, [viewer, os.path.abspath(arc)]),
                 (["--updated"], True, [viewer]),  # the relaunch after an update, with its notice
                 (["--settings"], False, None), ([arc, arc], False, None),
                 ([os.path.join(self.root, "없음.zip")], False, None)]
        for args, opened, cmd in cases:
            with self.subTest(args=args), mock.patch.object(mxzip_main.sys, "frozen", True, create=True), \
                    mock.patch.dict(os.environ, {"MXZIP_QT_HOME": ""}), \
                    mock.patch("assoc.viewer_for", return_value=viewer), \
                    mock.patch("subprocess.Popen") as popen:
                self.assertEqual(mxzip_main._open_in_viewer(args), opened)
                if cmd:
                    self.assertEqual(popen.call_args.args[0], cmd)
                    notice = (popen.call_args.kwargs.get("env") or {}).get("MXZIP_VIEWER_NOTICE", "")
                    self.assertEqual(bool(notice), args == ["--updated"])
                else:
                    popen.assert_not_called()
        with mock.patch.object(mxzip_main.sys, "frozen", True, create=True), \
                mock.patch("assoc.viewer_for", return_value=None), mock.patch("subprocess.Popen") as popen:
            self.assertFalse(mxzip_main._open_in_viewer([]))  # no .NET runtime: the Qt window as before
            popen.assert_not_called()

    def test_errors_say_what_to_do(self):
        import viewer_engine as ve
        locked = PermissionError(13, "denied")
        locked.winerror = 32
        cases = [(PermissionError(13, "denied"), "다른 폴더를 골라"), (locked, "그 프로그램을 닫고"),
                 (OSError(28, "No space left"), "공간을 비운 뒤"), (ValueError("bad header"), "'오류 검사'"),
                 (ve.UserError("파일 삭제는 ZIP 형식에서만 됩니다."), "파일 삭제는 ZIP 형식에서만 됩니다.")]
        for e, advice in cases:
            with self.subTest(e=e):
                msg = ve._failure(e)["message"]
                self.assertIn(advice, msg)
                if not isinstance(e, ve.UserError):
                    self.assertIn(str(e), msg)  # the original text stays for support

    def test_missing_archive_is_reported(self):
        code, doc = self.run_engine("--list-json", os.path.join(self.root, "없음.zip"), self.result)
        self.assertEqual((code, doc["error"]), (1, "missing"))

    def test_no_qt_is_loaded(self):
        arc = os.path.join(self.root, "a.zip")
        formats.create("zip", [self.src], arc, level=5)
        code = ("import sys, viewer_engine; viewer_engine.dispatch(['--list-json', %r, %r]); "
                "print(any(m.startswith('PySide6') for m in sys.modules))") % (arc, self.result)
        r = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, cwd=HERE, timeout=120)
        self.assertEqual(r.stdout.strip(), "False", r.stderr)


if __name__ == "__main__":
    unittest.main()

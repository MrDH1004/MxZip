"""appcore.hard_exit: the exe's exit path (yzip_main / yzip_gui.finish). It must keep the exit code, flush Python's
stdio buffers and skip DLL_PROCESS_DETACH, where shiboken6's static destructors crashed with 0xC0000005."""
import os
import subprocess
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))


def run(code: str):
    return subprocess.run([sys.executable, "-c", code], cwd=HERE, capture_output=True, text=True, timeout=60,
                          env=dict(os.environ, QT_QPA_PLATFORM="offscreen"))


class HardExit(unittest.TestCase):
    def test_exit_code_and_buffered_output(self):
        r = run("import sys, appcore; sys.stdout.write('out'); sys.stderr.write('err'); appcore.hard_exit(7)")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (7, "out", "err"))

    def test_zero(self):
        self.assertEqual(run("import appcore; appcore.hard_exit(0)").returncode, 0)

    @unittest.skipUnless(os.name == "nt", "DLL detach is Windows-only")
    def test_skips_dll_detach(self):
        # The loader's process-shutdown work (DLL detach, which ran shiboken6's static destructors) also runs
        # fiber-local-storage callbacks; one that changes the exit code to 99 shows whether that phase ran.
        # os._exit(3) gives 99 here, hard_exit(3) must give 3.
        probe = ("import ctypes, os, appcore\n"
                 "k = ctypes.WinDLL('kernel32'); CB = ctypes.WINFUNCTYPE(None, ctypes.c_void_p)\n"
                 "k.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]\n"
                 "cb = CB(lambda p: k.TerminateProcess(ctypes.c_void_p(-1), 99))\n"
                 "k.FlsAlloc.argtypes = [ctypes.c_void_p]; i = k.FlsAlloc(ctypes.cast(cb, ctypes.c_void_p))\n"
                 "k.FlsSetValue.argtypes = [ctypes.c_uint, ctypes.c_void_p]; k.FlsSetValue(i, 1)\n")
        self.assertEqual(run(probe + "os._exit(3)").returncode, 99)  # the probe sees loader shutdown
        self.assertEqual(run(probe + "appcore.hard_exit(3)").returncode, 3)

    def test_unhandled_error_still_uses_hard_exit(self):
        # an exception out of main() is logged and ends with exit code 1, not a normal interpreter exit
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            r = subprocess.run([sys.executable, "yzip_main.py", "--archive-list", os.path.join(d, "missing.txt"),
                                "--out", os.path.join(d, "x.zip")], cwd=HERE, capture_output=True, timeout=60,
                               env=dict(os.environ, LOCALAPPDATA=d, YZIP_NO_UPDATE="1", QT_QPA_PLATFORM="offscreen"))
            self.assertEqual(r.returncode, 1)
            logs = [os.path.join(dp, f) for dp, _, fs in os.walk(d) for f in fs if f.endswith(".log")]
            self.assertTrue(any("FileNotFoundError" in open(p, encoding="utf-8", errors="replace").read() for p in logs))

    def test_exit_with_qt_loaded(self):
        r = run("from PySide6.QtWidgets import QApplication, QLabel\n"
                "import yzip_gui\n"
                "app = QApplication([]); w = QLabel('x'); w.show(); app.processEvents()\n"
                "yzip_gui.finish(5)")
        self.assertEqual(r.returncode, 5, r.stderr[-2000:])


if __name__ == "__main__":
    unittest.main()

"""assoc on a throwaway extension (.yzt): the user's real .zip/.7z handlers are never touched.
The former-name cleanup runs against a made-up name, so a real yzip install's associations stay as they are."""
import os
import unittest
from unittest import mock

import assoc

EXT = ".yzt"
EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dist", "MxZip", "MxZip.exe")


class AssocTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # register() also publishes the app for Settings > Default apps; keep that only if it was there before
        cls.published = assoc._get(assoc.REG_APPS, assoc.APP) is not None

    @classmethod
    def tearDownClass(cls):
        if not cls.published:
            assoc._del_value(assoc.REG_APPS, assoc.APP)
            assoc._del_tree(assoc.CAP_KEY.rsplit("\\", 1)[0])

    def setUp(self):
        if not os.path.exists(EXE):
            self.skipTest("build the exe first")
        assoc.TYPES[EXT] = "YZT 테스트 파일"
        legacy = mock.patch.object(assoc, "LEGACY_APP", "MxZipLegacyTest")
        legacy.start()
        self.addCleanup(legacy.stop)

    def tearDown(self):
        assoc.unregister([EXT])
        assoc._del_tree(rf"Software\Classes\{EXT}")
        assoc._del_tree(r"Software\Classes\Other.yzt")
        assoc._del_tree(r"Software\Classes\MxZipLegacyTest" + EXT)
        assoc._del_tree(rf"{assoc.FILE_EXTS}\{EXT}")
        assoc.TYPES.pop(EXT, None)

    def _user_choice(self, pid):
        # what Windows writes when the user picks an app with "Always" (hash irrelevant here)
        assoc._set(rf"{assoc.FILE_EXTS}\{EXT}\UserChoice", "ProgId", pid)
        assoc._set(rf"{assoc.FILE_EXTS}\{EXT}\UserChoiceLatest\ProgId", "ProgId", pid)

    def test_choice_of_an_uninstalled_app_is_cleared(self):
        self._user_choice("GoneApp" + EXT)  # e.g. Bandizip.zip after Bandizip was removed
        assoc.register([EXT], exe=EXE)
        self.assertIsNone(assoc._get(rf"{assoc.FILE_EXTS}\{EXT}\UserChoice", "ProgId"))
        self.assertIsNone(assoc._get(rf"{assoc.FILE_EXTS}\{EXT}\UserChoiceLatest\ProgId", "ProgId"))
        self.assertTrue(assoc.status([EXT], EXE)[EXT][0])

    def _windows_refuses(self):
        # Windows 11 for its own archive types (.zip, .7z, ...): a program-set default yields no handler
        refuse = mock.patch.object(assoc, "effective", lambda ext: (r"C:\WINDOWS\system32\OpenWith.exe", ""))
        refuse.start()
        self.addCleanup(refuse.stop)

    def test_types_windows_keeps_are_reported(self):
        self._windows_refuses()
        self.assertEqual(assoc.register([EXT], exe=EXE), [EXT])
        self.assertTrue(assoc.enabled([EXT]))  # still offered under "Open with" and in Default apps

    def test_choice_of_an_installed_app_is_kept(self):
        assoc._set(r"Software\Classes\Other.yzt\shell\open\command", "", r'"C:\Windows\notepad.exe" "%1"')
        self._user_choice("Other.yzt")
        assoc.register([EXT], exe=EXE)
        self.assertEqual(assoc._get(rf"{assoc.FILE_EXTS}\{EXT}\UserChoice", "ProgId"), "Other.yzt")
        self.assertEqual(assoc._get(rf"{assoc.FILE_EXTS}\{EXT}\UserChoiceLatest\ProgId", "ProgId"), "Other.yzt")

    def test_register_makes_windows_open_it_with_mxzip(self):
        self.assertFalse(assoc.status([EXT], EXE)[EXT][0])
        self.assertFalse(assoc.enabled([EXT]))
        self.assertEqual(assoc.register([EXT], exe=EXE), [])
        mine, name = assoc.status([EXT], EXE)[EXT]
        self.assertTrue(mine, name)
        self.assertTrue(assoc.enabled([EXT]))
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}\OpenWithProgids", "MxZip" + EXT), "")
        assoc.unregister([EXT])
        self.assertFalse(assoc.status([EXT], EXE)[EXT][0])
        self.assertFalse(assoc.enabled([EXT]))
        self.assertIsNone(assoc._get(r"Software\Classes\MxZip" + EXT))

    def test_previous_default_is_restored(self):
        assoc._set(r"Software\Classes\Other.yzt\shell\open\command", "", r'"C:\Windows\notepad.exe" "%1"')
        assoc._set(rf"Software\Classes\{EXT}", "", "Other.yzt")
        assoc.register([EXT], exe=EXE)
        self.assertTrue(assoc.status([EXT], EXE)[EXT][0])
        assoc.unregister([EXT])
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}"), "Other.yzt")
        self.assertIn("notepad", assoc.effective(EXT)[0].lower())

    def _offer(self, chose):
        app = assoc._app_key(EXE)
        self.addCleanup(assoc._del_tree, app)
        assoc._set(r"Software\Classes\Other.yzt\shell\open\command", "", r'"C:\Windows\notepad.exe" "%1"')
        assoc._set(rf"Software\Classes\{EXT}", "", "Other.yzt")
        with mock.patch.object(assoc, "TYPES", {EXT: "YZT 테스트 파일"}), \
                mock.patch.object(assoc, "_user_chose", lambda ext: chose):
            assoc.offer(exe=EXE)
        self.assertEqual(assoc._get(app + r"\SupportedTypes", EXT), "")
        self.assertEqual(assoc._get(rf"{assoc.CAP_KEY}\FileAssociations", EXT), "MxZip" + EXT)
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}"), "Other.yzt")  # default untouched
        self.assertFalse(assoc.enabled([EXT]))  # offered is not associated

    def test_offer_without_a_user_choice_keeps_the_working_default(self):
        self._offer(chose=False)
        self.assertIsNone(assoc._get(rf"Software\Classes\{EXT}\OpenWithProgids", "MxZip" + EXT))
        self.assertIn("notepad", assoc.effective(EXT)[0].lower())  # Windows does not start asking
        assoc.register([EXT], exe=EXE)
        self.assertTrue(assoc.enabled([EXT]))
        assoc.unregister([EXT])
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}"), "Other.yzt")

    def test_offer_lists_mxzip_where_the_user_chose_an_app(self):
        self._offer(chose=True)
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}\OpenWithProgids", "MxZip" + EXT), "")

    def test_viewer_opens_archives_when_its_runtime_is_installed(self):
        viewer = os.path.join(os.path.dirname(EXE), assoc.VIEWER)
        with mock.patch.object(assoc.sys, "frozen", True, create=True), \
                mock.patch.object(assoc.os.path, "isfile", lambda p: p == viewer), \
                mock.patch.object(assoc, "_desktop_runtime_ok", lambda major=9: True):
            self.assertEqual(assoc.open_command(EXE), f'"{viewer}" "%1"')
        with mock.patch.object(assoc.sys, "frozen", True, create=True), \
                mock.patch.object(assoc.os.path, "isfile", lambda p: p == viewer), \
                mock.patch.object(assoc, "_desktop_runtime_ok", lambda major=9: False):
            self.assertEqual(assoc.open_command(EXE), f'"{EXE}" "%1"')  # no runtime: the Qt window

    def test_status_counts_the_viewer_as_mxzip(self):
        viewer = os.path.join(os.path.dirname(EXE), assoc.VIEWER)
        with mock.patch.object(assoc, "effective", lambda ext: (viewer, "MxZip")):
            self.assertTrue(assoc.status([EXT], EXE)[EXT][0])

    def test_former_name_association_is_replaced(self):
        # what an install under the former name left: its ProgID as the extension's default, over notepad's
        legacy = "MxZipLegacyTest" + EXT
        assoc._set(r"Software\Classes\Other.yzt\shell\open\command", "", r'"C:\Windows\notepad.exe" "%1"')
        assoc._set(rf"Software\Classes\{legacy}\shell\open\command", "", r'"C:\gone\yzip.exe" "%1"')
        assoc._set(rf"Software\Classes\{legacy}", "PreviousDefault", "Other.yzt")
        assoc._set(rf"Software\Classes\{EXT}\OpenWithProgids", legacy, "")
        assoc._set(rf"Software\Classes\{EXT}", "", legacy)
        assoc.register([EXT], exe=EXE)
        self.assertIsNone(assoc._get(rf"Software\Classes\{legacy}"))
        self.assertIsNone(assoc._get(rf"Software\Classes\{EXT}\OpenWithProgids", legacy))
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}"), "MxZip" + EXT)
        assoc.unregister([EXT])
        self.assertEqual(assoc._get(rf"Software\Classes\{EXT}"), "Other.yzt")


if __name__ == "__main__":
    unittest.main()

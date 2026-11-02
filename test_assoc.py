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
        assoc.TYPES.pop(EXT, None)

    def test_register_makes_windows_open_it_with_mxzip(self):
        self.assertFalse(assoc.status([EXT], EXE)[EXT][0])
        self.assertFalse(assoc.enabled([EXT]))
        assoc.register([EXT], exe=EXE)
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

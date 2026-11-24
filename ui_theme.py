"""Shared Qt surfaces. Point sizes are based on a 10 pt user font.

Appearance owns live palette conversion and scales these sizes proportionally.
Light tokens (appearance._DARK maps each one to its dark counterpart):
page #fafafa, card #ffffff, raised #f4f4f5, pressed #e9e9eb, hairline #e4e4e7,
strong hairline #d4d4d8, text #171717, secondary #3f3f46, muted #5f5f66,
disabled #a3a3a3, accent #2f5bd3 (hover #284fbb, pressed #2244a3), selection #e8eefb.
Focus borders are 2 px with 1 px less padding, so focus never moves the layout.
"""
import os
import struct
import zlib


def chevron(color: str) -> str:
    """Path of a 10x6 px (drawn at 2x) down-chevron PNG in COLOR, written once per colour.

    Qt style sheets need an image file for a combo arrow; a PNG needs no image plugin in
    the packaged exe. The name carries the colour so Appearance can swap it for dark.
    """
    hexcode = color.lstrip("#").lower()
    folder = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "MxZip", "ui")
    path = os.path.join(folder, f"chevron-{hexcode}.png").replace("\\", "/")
    if os.path.exists(path):
        return path
    w, h, sub = 20, 12, 4
    rgb = bytes.fromhex(hexcode)
    segments = (((2.0, 2.0), (10.0, 10.0)), ((10.0, 10.0), (18.0, 2.0)))
    rows = []
    for y in range(h):
        row = bytearray([0])
        for x in range(w):
            hits = 0
            for sy in range(sub):
                for sx in range(sub):
                    px, py = x + (sx + 0.5) / sub, y + (sy + 0.5) / sub
                    for (ax, ay), (bx, by) in segments:
                        t = max(0.0, min(1.0, ((px - ax) * (bx - ax) + (py - ay) * (by - ay)) /
                                         ((bx - ax) ** 2 + (by - ay) ** 2)))
                        if (px - ax - t * (bx - ax)) ** 2 + (py - ay - t * (by - ay)) ** 2 <= 1.6 ** 2:
                            hits += 1
                            break
            row += rgb + bytes([hits * 255 // (sub * sub)])
        rows.append(bytes(row))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) +
           chunk(b"IDAT", zlib.compress(b"".join(rows))) + chunk(b"IEND", b""))
    try:
        os.makedirs(folder, exist_ok=True)
        tmp = f"{path}.{os.getpid()}.tmp"
        with open(tmp, "wb") as stream:
            stream.write(png)
        os.replace(tmp, path)
    except OSError:
        pass  # no arrow image is cosmetic; the combo still opens on click and keys
    return path


# One family only: a list ('Segoe UI', 'Malgun Gothic') makes Qt resolve it for every widget polish,
# ~0.3 s before the first window shows. Appearance swaps in the user's font anyway.
COMMON_QSS = """
QWidget { font-family: 'Malgun Gothic'; font-size: 10pt; color: #171717; }
QDialog { background: #fafafa; }
QLabel { background: transparent; }
QPushButton, QToolButton { background: #ffffff; color: #171717; border: 1px solid #e4e4e7;
    border-radius: 6px; font-weight: 400; }
QPushButton { padding: 0 16px; min-height: 30px; }
QToolButton { padding: 5px 12px; }
QPushButton:hover, QToolButton:hover { background: #f4f4f5; border-color: #d4d4d8; }
QPushButton:pressed, QToolButton:pressed { background: #e9e9eb; }
QPushButton:focus { border: 2px solid #2f5bd3; padding: 0 15px; }
QToolButton:focus { border-color: #2f5bd3; }
QPushButton:disabled, QToolButton:disabled { color: #a3a3a3; background: #ffffff; border-color: #e4e4e7; }
QPushButton:default, #primaryButton, #homeMain, QToolButton#primary, #updBtn { background: #2f5bd3;
    color: #ffffff; border: 1px solid #2f5bd3; font-weight: 600; }
QPushButton:default:hover, #primaryButton:hover, #homeMain:hover, QToolButton#primary:hover, #updBtn:hover {
    background: #284fbb; border-color: #284fbb; }
QPushButton:default:pressed, #primaryButton:pressed, #homeMain:pressed, QToolButton#primary:pressed,
#updBtn:pressed { background: #2244a3; border-color: #2244a3; }
QPushButton:default:focus, #primaryButton:focus, #updBtn:focus {
    border: 2px solid #2244a3; padding: 0 15px; }
QPushButton:default:disabled, #primaryButton:disabled, #homeMain:disabled, #updBtn:disabled {
    background: #f4f4f5; color: #a3a3a3; border: 1px solid #e4e4e7; }
QLineEdit, QComboBox, QSpinBox, QPlainTextEdit { background: #ffffff; color: #171717;
    border: 1px solid #e4e4e7; border-radius: 6px; padding: 0 8px; min-height: 30px;
    selection-background-color: #e8eefb; selection-color: #171717; }
QPlainTextEdit { padding: 6px 8px; }
QLineEdit:hover, QComboBox:hover, QSpinBox:hover, QPlainTextEdit:hover { border-color: #d4d4d8; }
QLineEdit:focus, QComboBox:focus, QSpinBox:focus { border: 2px solid #2f5bd3; padding: 0 7px; }
QPlainTextEdit:focus { border: 2px solid #2f5bd3; padding: 5px 7px; }
QLineEdit:disabled, QComboBox:disabled, QSpinBox:disabled { color: #a3a3a3; background: #fafafa;
    border-color: #e4e4e7; }
QComboBox { padding-right: 24px; }
QComboBox:focus { padding-right: 23px; }
QComboBox::drop-down { subcontrol-origin: border; subcontrol-position: center right; width: 28px;
    border: none; background: transparent; }
QComboBox::down-arrow { image: url("{CHEVRON}"); width: 10px; height: 6px; }
QComboBox::down-arrow:disabled { image: url("{CHEVRON_DISABLED}"); }
QComboBox QAbstractItemView { background: #ffffff; color: #171717; border: 1px solid #e4e4e7;
    outline: none; padding: 4px; selection-background-color: #e8eefb; selection-color: #171717; }
QCheckBox, QRadioButton { spacing: 8px; padding: 3px 0; }
QCheckBox:disabled, QRadioButton:disabled { color: #a3a3a3; }
QMenu { background: #ffffff; color: #171717; border: 1px solid #e4e4e7; padding: 4px; }
QMenu::item { padding: 8px 24px 8px 12px; border-radius: 4px; }
QMenu::item:selected { background: #f4f4f5; }
QMenu::item:disabled { color: #a3a3a3; }
QMenu::separator { height: 1px; background: #e4e4e7; margin: 4px 8px; }
/* No background here: it makes the windows11 style drop the checked fill of item checkboxes.
   The view takes palette Base (Appearance sets it to the card colour). */
QTreeWidget { border: none; alternate-background-color: #fafafa;
    color: #171717; selection-background-color: #e8eefb; selection-color: #171717; outline: none; }
QTreeWidget::item { min-height: 32px; padding: 0 6px; }
QTreeWidget::item:hover { background: #f4f4f5; color: #171717; }
QTreeWidget::item:selected { background: #e8eefb; color: #171717; }
QTreeWidget::item:disabled { color: #a3a3a3; }
QHeaderView::section { background: #fafafa; color: #5f5f66; border: none;
    border-bottom: 1px solid #e4e4e7; padding: 8px; font-size: 9pt; }
QProgressBar { background: #e4e4e7; border: none; border-radius: 3px; min-height: 6px; max-height: 6px; }
QProgressBar::chunk { background: #2f5bd3; border-radius: 3px; }
QToolTip { background: #ffffff; color: #171717; border: 1px solid #e4e4e7; padding: 6px; }
""".replace("{CHEVRON}", chevron("#5f5f66")).replace("{CHEVRON_DISABLED}", chevron("#a3a3a3"))

MAIN_QSS = COMMON_QSS + """
QMainWindow, #home, #homeScroll, #listPage { background: #fafafa; }
#homeScroll { border: none; }
#appHeader { background: #ffffff; border-bottom: 1px solid #e4e4e7; }
#brand { font-size: 17pt; font-weight: 600; }
#brandHint, #appVersion, #homeHint, #homeFoot, #homeCreateFormats, #homeVersion, #homeActionHint, #archMeta, #searchSummary {
    color: #5f5f66; font-size: 9pt; }
#homeTitle { font-size: 22pt; font-weight: 600; }
#dropTitle { font-size: 14pt; font-weight: 600; }
#homeDrop { border: 2px dashed #e4e4e7; border-radius: 10px; background: #ffffff; }
#homeDrop[hot="true"] { border-color: #2f5bd3; background: #f4f4f5; }
#homeMain, #homeBtn { padding: 11px 22px; }
#homeMain:focus, #homeBtn:focus { border-width: 2px; padding: 10px 21px; }
#formatsLink { color: #2f5bd3; background: transparent; border: none; padding: 4px 0; font-size: 9pt; }
#archHead { background: #ffffff; }
#archName { font-size: 15pt; font-weight: 600; }
#archBadge { color: #ffffff; font-size: 10pt; font-weight: 600; border-radius: 6px; }
#lockPill { color: #2f5bd3; border: 1px solid #2f5bd3; border-radius: 4px; padding: 2px 7px; font-size: 9pt; }
#crumbBar, #searchBar { background: #ffffff; border-bottom: 1px solid #e4e4e7; }
#crumbBar QToolButton { border: none; padding: 5px 7px; }
#crumbLast { font-weight: 600; }
#crumbSep { color: #5f5f66; }
QToolBar { background: #ffffff; border: none; border-bottom: 1px solid #e4e4e7; padding: 6px 14px; spacing: 6px; }
QStatusBar { background: #ffffff; color: #5f5f66; border-top: 1px solid #e4e4e7; font-size: 9pt; }
#updBar { background: #f4f4f5; border-bottom: 1px solid #e4e4e7; }
"""

PROGRESS_QSS = COMMON_QSS + """
QDialog#progressWindow { background: #ffffff; }
QToolButton { padding: 4px; }
QToolButton:checked { border-color: #2f5bd3; background: #e8eefb; }
#progressHint { color: #5f5f66; font-size: 9pt; }
"""

DIALOG_QSS = COMMON_QSS + """
#dialogTitle { font-size: 14pt; font-weight: 600; }
#dialogHint { font-size: 9pt; color: #5f5f66; }
QDialogButtonBox { padding-top: 12px; }
"""

PREFERENCES_QSS = DIALOG_QSS + """
#settingsDialog { background: #fafafa; }
#settingsTitle { font-size: 13pt; font-weight: 600; }
#settingsBody { background: transparent; }
QTabWidget#settingsPages::pane { background: #ffffff; border: 1px solid #e4e4e7; border-radius: 8px; top: 0; }
#settingsNav { background: transparent; border: none; outline: none; padding: 0; }
#settingsNav::item { padding: 8px 10px; border-radius: 6px; color: #3f3f46;
    border-left: 2px solid transparent; }
#settingsNav::item:hover { background: #f4f4f5; color: #171717; }
#settingsNav::item:selected { background: #f4f4f5; color: #171717; border-left: 2px solid #2f5bd3; }
#settingsSectionTitle { font-size: 13pt; font-weight: 600; }
#settingsSectionHint { font-size: 9pt; color: #3f3f46; }
#settingsFieldHint, #settingsFooterHint { font-size: 9pt; color: #5f5f66; }
#settingsScroll, #settingsPage, #settingsScroll QWidget#qt_scrollarea_viewport { background: #ffffff; border: none; }
#settingsAssocList { border: 1px solid #e4e4e7; border-radius: 6px; }
#settingsAssocList::item { min-height: 24px; }
#settingsButtons { background: transparent; padding: 0; }
"""

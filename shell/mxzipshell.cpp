// MxZip Explorer context-menu handler. When every selected item is an archive (an extension listed in
// HKCU\Software\MxZip\ArchiveExts), first the extract items, so they sit in the menu's extract section next to
// Windows' and the third-party archiver's instead of the static-verb group at the top:
//   2 "여기에 풀기": <Command> --extract-list here <list>
//   5 "알아서 풀기": <Command> --extract-list smart <list>
//   3 "<name>\ 에 풀기": <Command> --extract-list folder <list> --out <folder>
//   6 "MxZip으로 압축 풀기...": <Command> --extract-list dialog <list>
// otherwise (not all archives) the compress items:
//   0 "<name>.zip으로 압축하기 - MxZip": compresses at once. The handler decides the output path (same "(2)" rule
//     as MxZip's unique_path) so the label and the file always agree: <Command> --archive-list <list> --out <zip>
//   4 "각각 ZIP 파일로 압축하기 - MxZip" (several items, "파일별로 처리" setting): one ZIP per item, named by
//     MxZip: <Command> --archive-list <list> --out <zip> --each
//   1 "MxZip으로 압축하기(W)...": opens MxZip's compress window (format, password, ...):
//     <Command> --compress-dialog-list <list>
// The selection goes in a UTF-8 list file. <Command> comes from HKCU\Software\MxZip\Command (--register-shell).
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

// {E856C547-DF4F-438A-9357-3225756E4D24}
static const CLSID CLSID_MxZipMenu = {0xe856c547, 0xdf4f, 0x438a, {0x93, 0x57, 0x32, 0x25, 0x75, 0x6e, 0x4d, 0x24}};
enum { CMD_COMPRESS, CMD_DIALOG, CMD_HERE, CMD_FOLDER, CMD_EACH, CMD_SMART, CMD_EXTRACTDLG, N_CMDS };
static const wchar_t* const VERBS_W[] = {L"MxZip.compress", L"MxZip.compressdlg", L"MxZip.extracthere", L"MxZip.extractfolder",
                                         L"MxZip.compresseach", L"MxZip.extractsmart", L"MxZip.extractdlg"};
static const char* const VERBS_A[] = {"MxZip.compress", "MxZip.compressdlg", "MxZip.extracthere", "MxZip.extractfolder",
                                      "MxZip.compresseach", "MxZip.extractsmart", "MxZip.extractdlg"};

static HINSTANCE g_inst;
static long g_refs;

static bool exists(const std::wstring& p) { return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES; }

static bool is_dir(const std::wstring& p) {
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

static size_t last_sep(const std::wstring& p) { return p.find_last_of(L"\\/"); }

static std::wstring file_name(const std::wstring& p) {
    size_t i = last_sep(p);
    return i == std::wstring::npos ? p : p.substr(i + 1);
}

// os.path.splitext: the extension starts at the last dot that follows a non-dot character of the name
static std::wstring strip_ext(const std::wstring& p) {
    size_t start = last_sep(p);
    start = start == std::wstring::npos ? 0 : start + 1;
    size_t first = p.find_first_not_of(L'.', start);
    size_t dot = p.find_last_of(L'.');
    if (first == std::wstring::npos || dot == std::wstring::npos || dot <= first || dot < start)
        return p;
    return p.substr(0, dot);
}

// Same naming as MxZip: one file -> name without extension, one folder -> folder name,
// several items -> name of the folder they are in; then "name (2).zip" if taken.
static std::wstring output_path(const std::vector<std::wstring>& paths) {
    std::wstring base;
    if (paths.size() == 1) {
        std::wstring p = paths[0];
        if (p.size() <= 3 && p.size() >= 2 && p[1] == L':')
            return L"";  // drive root
        base = is_dir(p) ? p : strip_ext(p);
    } else {
        std::wstring dir = paths[0].substr(0, last_sep(paths[0]));
        if (dir.size() == 2 && dir[1] == L':')
            dir += L"\\";
        std::wstring name = file_name(dir);
        base = (dir.back() == L'\\' ? dir : dir + L"\\") + (name.empty() ? L"archive" : name);
    }
    std::wstring out = base + L".zip";
    for (int i = 2; exists(out); i++)
        out = base + L" (" + std::to_wstring(i) + L").zip";
    return out;
}

static std::wstring menu_label(const std::wstring& out) {
    std::wstring name = file_name(out);
    if (name.size() > 52)
        name = name.substr(0, 24) + L"…" + name.substr(name.size() - 24);
    std::wstring esc;
    for (wchar_t c : name) {
        if (c == L'&')
            esc += L'&';
        esc += c;
    }
    return esc + L"으로 압축하기 - &MxZip";  // The third-party archiver uses the same wording with &Z, so name ourselves and take Y
}

static std::wstring read_command() {
    wchar_t buf[2048];
    DWORD cb = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\MxZip", L"Command", RRF_RT_REG_SZ, nullptr, buf, &cb) != ERROR_SUCCESS)
        return L"";
    return buf;
}

// MenuMask bits: 1 compress, 2 "여기에 풀기", 4 "<folder>\에 풀기", 8 "알아서 풀기", 16 "MxZip으로 압축 풀기...".
// 0x80 marks a value that knows bits 8 and 16; an older value (written before them) shows both.
static DWORD menu_mask() {
    DWORD value = 0x9F, cb = sizeof(value);
    LSTATUS result = RegGetValueW(HKEY_CURRENT_USER, L"Software\\MxZip", L"MenuMask",
                                 RRF_RT_REG_DWORD, nullptr, &value, &cb);
    if (result != ERROR_SUCCESS)
        return 0x9F;
    return value & 0x80 ? value : value | 8 | 16;
}

static bool english_menu() {
    wchar_t value[8] = {};
    DWORD cb = sizeof(value);
    return RegGetValueW(HKEY_CURRENT_USER, L"Software\\MxZip", L"Language", RRF_RT_REG_SZ,
                        nullptr, value, &cb) == ERROR_SUCCESS && lstrcmpW(value, L"en") == 0;
}

static bool separate_archives() {
    DWORD value = 0, cb = sizeof(value);
    return RegGetValueW(HKEY_CURRENT_USER, L"Software\\MxZip", L"SeparateArchives", RRF_RT_REG_DWORD,
                        nullptr, &value, &cb) == ERROR_SUCCESS && value != 0;
}

static std::wstring icon_source() {
    wchar_t buf[2048];
    DWORD cb = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\MxZip", L"Icon", RRF_RT_REG_SZ, nullptr, buf, &cb) != ERROR_SUCCESS)
        return L"";
    return buf;
}

// ";.zip;.7z;...;" (lowercase) from --register-shell: ArchiveExts (archives proper) or ImageExts (disk images,
// installers: MxZip extracts them, but they are not "just an archive", so compressing stays offered)
static std::wstring ext_list(const wchar_t* value) {
    wchar_t buf[2048];
    DWORD cb = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\MxZip", value, RRF_RT_REG_SZ, nullptr, buf, &cb) != ERROR_SUCCESS)
        return L"";
    std::wstring s = L";" + std::wstring(buf) + L";";
    CharLowerBuffW(&s[0], (DWORD)s.size());
    return s;
}

// formats.stem + unique_dir: "C:\a\Kings.zip" -> "C:\a\Kings", or "C:\a\Kings (2)" when that name is taken;
// a volume set is named without its volume part: "x.part1.rar", "x.vol1.egg" -> "x"
static std::wstring extract_dir(const std::wstring& arc) {
    std::wstring dir = arc.substr(0, last_sep(arc) + 1), name = file_name(arc), low = name;
    CharLowerBuffW(&low[0], (DWORD)low.size());
    std::wstring stem;
    for (const wchar_t* e : {L".tar.gz", L".tar.bz2", L".tar.xz", L".tar.zst"}) {
        size_t n = wcslen(e);
        if (low.size() > n && low.compare(low.size() - n, n, e) == 0)
            stem = name.substr(0, name.size() - n);
    }
    for (const wchar_t* e : {L".rar", L".egg"}) {  // ".partN.rar" / ".volN.egg"
        size_t n = wcslen(e);
        if (stem.empty() && low.size() > n && low.compare(low.size() - n, n, e) == 0) {
            size_t dot = low.find_last_of(L'.', low.size() - n - 1);
            std::wstring mid = dot == std::wstring::npos ? L"" : low.substr(dot + 1, low.size() - n - dot - 1);
            const wchar_t* word = e[1] == L'r' ? L"part" : L"vol";
            size_t w = wcslen(word);
            if (mid.size() > w && mid.compare(0, w, word) == 0 &&
                mid.find_first_not_of(L"0123456789", w) == std::wstring::npos)
                stem = name.substr(0, dot);
        }
    }
    if (stem.empty())
        stem = file_name(strip_ext(arc));
    std::wstring out = dir + stem;
    for (int i = 2; exists(out); i++)
        out = dir + stem + L" (" + std::to_wstring(i) + L")";
    return out;
}

static std::wstring escape_amp(const std::wstring& s) {
    std::wstring esc;
    for (wchar_t c : s) {
        if (c == L'&')
            esc += L'&';
        esc += c;
    }
    return esc;
}

// 0: not something MxZip extracts, 1: an archive, 2: a disk image / installer MxZip can extract
static int extract_kind(const std::wstring& p, const std::wstring& archives, const std::wstring& images) {
    std::wstring name = file_name(p);
    size_t dot = name.find_last_of(L'.');
    if (dot == std::wstring::npos || dot == 0 || is_dir(p))
        return 0;
    std::wstring ext = L";" + name.substr(dot) + L";";
    CharLowerBuffW(&ext[0], (DWORD)ext.size());
    return archives.find(ext) != std::wstring::npos ? 1 : images.find(ext) != std::wstring::npos ? 2 : 0;
}

// 32bpp top-down DIB with alpha, which is what menus need for a crisp icon
static HBITMAP icon_bitmap(const std::wstring& src) {
    if (src.empty())
        return nullptr;
    int cx = GetSystemMetrics(SM_CXSMICON), cy = GetSystemMetrics(SM_CYSMICON);
    HICON icon = nullptr;
    if (FAILED(SHDefExtractIconW(src.c_str(), 0, 0, nullptr, &icon, MAKELONG(0, cx))) || !icon)
        return nullptr;
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = cx;
    bi.bmiHeader.biHeight = -cy;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (bmp) {
        HGDIOBJ old = SelectObject(dc, bmp);
        DrawIconEx(dc, 0, 0, icon, cx, cy, 0, nullptr, DI_NORMAL);
        SelectObject(dc, old);
    }
    DeleteDC(dc);
    DestroyIcon(icon);
    return bmp;
}

static bool write_utf8(const std::wstring& path, const std::wstring& text) {
    int n = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), &s[0], n, nullptr, nullptr);
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return false;
    DWORD w = 0;
    BOOL ok = WriteFile(h, s.data(), (DWORD)s.size(), &w, nullptr);
    CloseHandle(h);
    return ok && w == s.size();
}

class MxZipMenu : public IShellExtInit, public IContextMenu {
    long refs_ = 1;
    std::vector<std::wstring> paths_;
    std::wstring out_;
    std::wstring into_;  // one archive: the folder "폴더에 압축 풀기" names and extracts into
    std::vector<int> cmds_;  // command of each menu offset
    HBITMAP bmp_ = nullptr;

public:
    MxZipMenu() { InterlockedIncrement(&g_refs); }
    ~MxZipMenu() {
        if (bmp_)
            DeleteObject(bmp_);
        InterlockedDecrement(&g_refs);
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) {
        static const QITAB qit[] = {QITABENT(MxZipMenu, IShellExtInit), QITABENT(MxZipMenu, IContextMenu), {0}};
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() {
        long r = InterlockedDecrement(&refs_);
        if (!r)
            delete this;
        return r;
    }

    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE, IDataObject* data, HKEY) {
        if (!data)
            return E_INVALIDARG;  // folder background: nothing selected
        paths_.clear();
        FORMATETC fe = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        STGMEDIUM stg;
        if (FAILED(data->GetData(&fe, &stg)))
            return E_INVALIDARG;
        HDROP drop = (HDROP)GlobalLock(stg.hGlobal);
        if (drop) {
            UINT n = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
            for (UINT i = 0; i < n; i++) {
                UINT len = DragQueryFileW(drop, i, nullptr, 0);
                std::wstring p(len, L'\0');
                DragQueryFileW(drop, i, &p[0], len + 1);
                paths_.push_back(p);
            }
            GlobalUnlock(stg.hGlobal);
        }
        ReleaseStgMedium(&stg);
        return paths_.empty() ? E_INVALIDARG : S_OK;
    }

    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT first, UINT, UINT flags) {
        if (flags & CMF_DEFAULTONLY)
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        if (paths_.empty() || read_command().empty())
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        out_ = output_path(paths_);
        cmds_.clear();
        std::vector<std::wstring> labels;
        into_.clear();
        std::wstring arc_exts = ext_list(L"ArchiveExts"), img_exts = ext_list(L"ImageExts");
        DWORD enabled = menu_mask();
        bool english = english_menu();
        bool extractable = true, archives = true;  // every item extractable / every item an archive proper
        for (auto& p : paths_) {
            int k = extract_kind(p, arc_exts, img_exts);
            extractable = extractable && k != 0;
            archives = archives && k == 1;
        }
        if (extractable) {  // the usual archiver set: here, smart, "<name>\", and the extract window
            if (enabled & 2) {
                cmds_.push_back(CMD_HERE);
                labels.push_back(english ? L"Extract &here" : L"여기에 풀기(&Z)");
            }
            if (enabled & 8) {  // one top-level item: next to the archive; several: into "<name>\"
                cmds_.push_back(CMD_SMART);
                labels.push_back(english ? L"Smart e&xtract" : L"알아서 풀기(&Z)");
            }
            if (enabled & 4) {
                cmds_.push_back(CMD_FOLDER);
                if (paths_.size() == 1) {  // "Kings (2)\ 에 풀기": the real folder, numbered if taken
                    into_ = extract_dir(paths_[0]);
                    std::wstring n = file_name(into_);
                    if (n.size() > 52)
                        n = n.substr(0, 24) + L"…" + n.substr(n.size() - 24);
                    labels.push_back(english ? L"Extract to " + escape_amp(n) + L"\\(&E)" : escape_amp(n) + L"\\ 에 풀기(&E)");
                } else {
                    labels.push_back(english ? L"Extract to separate folders(&E)" : L"각각의 폴더에 풀기(&E)");
                }
            }
            if (enabled & 16) {
                cmds_.push_back(CMD_EXTRACTDLG);
                labels.push_back(english ? L"Extract with MxZip(&B)..." : L"MxZip으로 압축 풀기(&B)...");
            }
        }
        if (!out_.empty() && !archives && (enabled & 1)) {  // archives get the extract items only
            // like Bandizip: "<folder>.zip" (all in one, named after the folder) is always offered;
            // "each" is added next to it when several items are selected and the setting asks for it
            cmds_.push_back(CMD_COMPRESS);
            labels.push_back(english ? L"Compress to " + escape_amp(file_name(out_)) + L" - &MxZip" : menu_label(out_));
            if (paths_.size() > 1 && separate_archives()) {
                cmds_.push_back(CMD_EACH);
                labels.push_back(english ? L"Compress each item to ZIP - MxZip" : L"각각 ZIP 파일로 압축하기 - MxZip");
            }
            cmds_.push_back(CMD_DIALOG);
            labels.push_back(english ? L"Compress with MxZip(&W)..." : L"MxZip으로 압축하기(&W)...");
        }
        if (cmds_.empty())
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        if (!bmp_)
            bmp_ = icon_bitmap(icon_source());
        for (UINT i = 0; i < cmds_.size(); i++) {
            MENUITEMINFOW mii = {sizeof(mii)};
            mii.fMask = MIIM_STRING | MIIM_ID | MIIM_FTYPE | (bmp_ ? MIIM_BITMAP : 0);
            mii.fType = MFT_STRING;
            mii.wID = first + i;
            mii.dwTypeData = &labels[i][0];
            mii.hbmpItem = bmp_;
            if (!InsertMenuItemW(menu, index + i, TRUE, &mii))
                return HRESULT_FROM_WIN32(GetLastError());
        }
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, (USHORT)cmds_.size());
    }

    // which command (CMD_*) from a menu offset or a verb string, -1 if not ours
    int command_of(LPCMINVOKECOMMANDINFO pici) const {
        bool unicode = pici->cbSize == sizeof(CMINVOKECOMMANDINFOEX) && (pici->fMask & CMIC_MASK_UNICODE);
        if (unicode && HIWORD(((CMINVOKECOMMANDINFOEX*)pici)->lpVerbW)) {
            for (int i = 0; i < N_CMDS; i++)
                if (!lstrcmpiW(((CMINVOKECOMMANDINFOEX*)pici)->lpVerbW, VERBS_W[i]))
                    return i;
            return -1;
        }
        if (HIWORD(pici->lpVerb)) {
            for (int i = 0; i < N_CMDS; i++)
                if (!lstrcmpiA(pici->lpVerb, VERBS_A[i]))
                    return i;
            return -1;
        }
        UINT off = LOWORD(pici->lpVerb);
        return off < cmds_.size() ? cmds_[off] : -1;
    }

    IFACEMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) {
        int which = command_of(pici);
        if (which < 0)
            return E_FAIL;
        std::wstring cmd = read_command();
        if (cmd.empty() || paths_.empty())
            return E_FAIL;
        bool compress = which == CMD_COMPRESS || which == CMD_EACH;
        if (compress && out_.empty())
            out_ = output_path(paths_);
        if (compress && out_.empty())
            return E_FAIL;

        wchar_t tmp[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        wchar_t list[MAX_PATH];
        StringCchPrintfW(list, MAX_PATH, L"%smxzip_sel_%lu_%llu.txt", tmp, GetCurrentProcessId(), GetTickCount64());
        std::wstring text;
        for (auto& p : paths_)
            text += p + L"\n";
        if (!write_utf8(list, text))
            return E_FAIL;

        std::wstring line = compress ? cmd + L" --archive-list \"" + list + L"\" --out \"" + out_ + L"\"" +
                                           (which == CMD_EACH ? L" --each" : L"")
                          : which == CMD_DIALOG   ? cmd + L" --compress-dialog-list \"" + list + L"\""
                          : which == CMD_HERE     ? cmd + L" --extract-list here \"" + list + L"\""
                          : which == CMD_SMART    ? cmd + L" --extract-list smart \"" + list + L"\""
                          : which == CMD_EXTRACTDLG ? cmd + L" --extract-list dialog \"" + list + L"\""
                                                  : cmd + L" --extract-list folder \"" + list + L"\"" +
                                                        (into_.empty() ? L"" : L" --out \"" + into_ + L"\"");
        std::wstring dir = paths_[0].substr(0, last_sep(paths_[0]) + 1);
        STARTUPINFOW si = {sizeof(si)};
        PROCESS_INFORMATION pi = {};
        if (!CreateProcessW(nullptr, &line[0], nullptr, nullptr, FALSE, 0, nullptr, dir.c_str(), &si, &pi)) {
            DeleteFileW(list);
            return HRESULT_FROM_WIN32(GetLastError());
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return S_OK;
    }

    IFACEMETHODIMP GetCommandString(UINT_PTR id, UINT type, UINT*, LPSTR name, UINT cch) {
        if (id >= cmds_.size())
            return E_INVALIDARG;
        int c = cmds_[id];
        static const wchar_t* const HELP_W[] = {L"선택한 항목을 ZIP 파일로 바로 압축합니다", L"형식과 암호 등을 골라 압축합니다",
                                                L"압축 파일이 있는 폴더에 풉니다", L"압축 파일 이름의 폴더에 풉니다",
                                                L"선택한 항목을 각각 ZIP 파일로 압축합니다",
                                                L"최상위 항목이 하나면 그 자리에, 여럿이면 폴더를 만들어 풉니다",
                                                L"풀 위치와 옵션을 골라 풉니다"};
        static const char* const HELP_A[] = {"Compress the selection to a ZIP file", "Choose format and options, then compress",
                                             "Extract next to the archive", "Extract into a folder named after the archive",
                                             "Compress each selected item to its own ZIP file",
                                             "One top-level item: next to the archive; several: into a new folder",
                                             "Choose where and how to extract"};
        switch (type) {
        case GCS_VERBW:
            return StringCchCopyW((LPWSTR)name, cch, VERBS_W[c]);
        case GCS_VERBA:
            return StringCchCopyA(name, cch, VERBS_A[c]);
        case GCS_HELPTEXTW:
            return StringCchCopyW((LPWSTR)name, cch, HELP_W[c]);
        case GCS_HELPTEXTA:
            return StringCchCopyA(name, cch, HELP_A[c]);
        case GCS_VALIDATEW:
        case GCS_VALIDATEA:
            return S_OK;
        }
        return E_NOTIMPL;
    }
};

class Factory : public IClassFactory {
    long refs_ = 1;

public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) {
        static const QITAB qit[] = {QITABENT(Factory, IClassFactory), {0}};
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() {
        long r = InterlockedDecrement(&refs_);
        if (!r)
            delete this;
        return r;
    }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) {
        *ppv = nullptr;
        if (outer)
            return CLASS_E_NOAGGREGATION;
        MxZipMenu* m = new (std::nothrow) MxZipMenu();
        if (!m)
            return E_OUTOFMEMORY;
        HRESULT hr = m->QueryInterface(riid, ppv);
        m->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) {
        lock ? InterlockedIncrement(&g_refs) : InterlockedDecrement(&g_refs);
        return S_OK;
    }
};

BOOL APIENTRY DllMain(HMODULE mod, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_inst = mod;
        DisableThreadLibraryCalls(mod);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    *ppv = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_MxZipMenu))
        return CLASS_E_CLASSNOTAVAILABLE;
    Factory* f = new (std::nothrow) Factory();
    if (!f)
        return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_refs == 0 ? S_OK : S_FALSE; }

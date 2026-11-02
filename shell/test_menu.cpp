// Test the real DLL menu implementation against a private HKCU view.
// Compile with the same Windows SDK/libraries as mxzipshell.cpp.
#include "mxzipshell.cpp"
#include <stdio.h>

static bool verify_menu(const wchar_t* path, DWORD mask, bool archive) {
    HKEY config = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\MxZip", 0, nullptr, 0,
                        KEY_ALL_ACCESS, nullptr, &config, nullptr) != ERROR_SUCCESS)
        return false;
    const wchar_t command[] = L"MxZip.exe";
    const wchar_t exts[] = L".zip;.alz";
    bool written = RegSetValueExW(config, L"Command", 0, REG_SZ,
                                (const BYTE*)command, sizeof(command)) == ERROR_SUCCESS &&
                   RegSetValueExW(config, L"ArchiveExts", 0, REG_SZ,
                                (const BYTE*)exts, sizeof(exts)) == ERROR_SUCCESS &&
                   RegSetValueExW(config, L"MenuMask", 0, REG_DWORD,
                                (const BYTE*)&mask, sizeof(mask)) == ERROR_SUCCESS;
    RegCloseKey(config);
    if (!written)
        return false;
    PIDLIST_ABSOLUTE absolute = nullptr;
    if (FAILED(SHParseDisplayName(path, nullptr, &absolute, 0, nullptr)))
        return false;
    IShellFolder* parent = nullptr;
    PCUITEMID_CHILD child = nullptr;
    HRESULT hr = SHBindToParent(absolute, IID_PPV_ARGS(&parent), &child);
    PIDLIST_ABSOLUTE parent_id = ILCloneFull(absolute);
    ILRemoveLastID(parent_id);
    IDataObject* data = nullptr;
    if (SUCCEEDED(hr))
        hr = SHCreateDataObject(parent_id, 1, &child, nullptr, IID_PPV_ARGS(&data));
    bool ok = SUCCEEDED(hr);
    MxZipMenu* handler = new MxZipMenu();
    HMENU menu = CreatePopupMenu();
    if (ok)
        ok = SUCCEEDED(handler->Initialize(nullptr, data, nullptr));
    if (ok) {
        hr = handler->QueryContextMenu(menu, 0, 1, 100, CMF_NORMAL);
        int expected = archive ? ((mask & 2) ? 1 : 0) + ((mask & 4) ? 1 : 0) : ((mask & 1) ? 2 : 0);
        ok = SUCCEEDED(hr) && GetMenuItemCount(menu) == expected && HRESULT_CODE(hr) == expected;
        for (int i = 0; ok && i < expected; ++i) {
            wchar_t verb[128] = {};
            ok = SUCCEEDED(handler->GetCommandString(i, GCS_VERBW, nullptr, (LPSTR)verb, 128));
            if (archive)
                ok = ok && ((lstrcmpW(verb, L"MxZip.extracthere") == 0 && (mask & 2)) ||
                            (lstrcmpW(verb, L"MxZip.extractfolder") == 0 && (mask & 4)));
            else
                ok = ok && ((lstrcmpW(verb, L"MxZip.compress") == 0 ||
                             lstrcmpW(verb, L"MxZip.compressdlg") == 0) && (mask & 1));
        }
    }
    DestroyMenu(menu);
    handler->Release();
    if (data) data->Release();
    if (parent) parent->Release();
    CoTaskMemFree(parent_id);
    CoTaskMemFree(absolute);
    return ok;
}

// Two files of one folder with "SeparateArchives": "<folder>.zip으로 압축하기" (one archive named after the folder),
// then "각각 ZIP 파일로 압축하기", then the dialog item; without the setting only the folder-named item and the dialog.
static bool verify_multi(const std::wstring& a, const std::wstring& b, DWORD separate) {
    HKEY config = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\MxZip", 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &config, nullptr) != ERROR_SUCCESS)
        return false;
    DWORD mask = 7;
    bool written = RegSetValueExW(config, L"MenuMask", 0, REG_DWORD, (const BYTE*)&mask, sizeof(mask)) == ERROR_SUCCESS &&
                   RegSetValueExW(config, L"SeparateArchives", 0, REG_DWORD, (const BYTE*)&separate, sizeof(separate)) == ERROR_SUCCESS;
    RegCloseKey(config);
    if (!written)
        return false;
    PIDLIST_ABSOLUTE ida = nullptr, idb = nullptr;
    if (FAILED(SHParseDisplayName(a.c_str(), nullptr, &ida, 0, nullptr)) ||
        FAILED(SHParseDisplayName(b.c_str(), nullptr, &idb, 0, nullptr)))
        return false;
    PIDLIST_ABSOLUTE parent_id = ILCloneFull(ida);
    ILRemoveLastID(parent_id);
    PCUITEMID_CHILD children[2] = {ILFindLastID(ida), ILFindLastID(idb)};
    IDataObject* data = nullptr;
    bool ok = SUCCEEDED(SHCreateDataObject(parent_id, 2, children, nullptr, IID_PPV_ARGS(&data)));
    MxZipMenu* handler = new MxZipMenu();
    HMENU menu = CreatePopupMenu();
    ok = ok && SUCCEEDED(handler->Initialize(nullptr, data, nullptr));
    const wchar_t* expected[] = {L"MxZip.compress", L"MxZip.compresseach", L"MxZip.compressdlg"};
    std::vector<const wchar_t*> want = separate ? std::vector<const wchar_t*>{expected[0], expected[1], expected[2]}
                                                : std::vector<const wchar_t*>{expected[0], expected[2]};
    if (ok) {
        HRESULT hr = handler->QueryContextMenu(menu, 0, 1, 100, CMF_NORMAL);
        ok = SUCCEEDED(hr) && HRESULT_CODE(hr) == (int)want.size() && GetMenuItemCount(menu) == (int)want.size();
        for (int i = 0; ok && i < (int)want.size(); ++i) {
            wchar_t verb[128] = {};
            ok = SUCCEEDED(handler->GetCommandString(i, GCS_VERBW, nullptr, (LPSTR)verb, 128)) && !lstrcmpW(verb, want[i]);
        }
        // the first item names the folder the files are in: "<folder>.zip으로 압축하기 - &MxZip"
        std::wstring dir = a.substr(0, last_sep(a));
        std::wstring label(256, L'\0');
        if (ok)
            label.resize(GetMenuStringW(menu, 0, &label[0], 256, MF_BYPOSITION));
        ok = ok && label.find(file_name(dir)) == 0 && label.find(L".zip") != std::wstring::npos;
    }
    DestroyMenu(menu);
    handler->Release();
    if (data) data->Release();
    CoTaskMemFree(parent_id);
    CoTaskMemFree(ida);
    CoTaskMemFree(idb);
    return ok;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3 || !exists(argv[1]) || !exists(argv[2])) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) return 3;
    std::wstring sandbox = L"Software\\MxZip-menu-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    HKEY root = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, sandbox.c_str(), 0, nullptr, 0,
                        KEY_ALL_ACCESS, nullptr, &root, nullptr) != ERROR_SUCCESS) return 4;
    if (RegOverridePredefKey(HKEY_CURRENT_USER, root) != ERROR_SUCCESS) return 5;
    bool ok = true;
    for (DWORD mask = 0; mask < 8; ++mask) {
        bool result = verify_menu(argv[1], mask, false) && verify_menu(argv[2], mask, true);
        printf("%s native menu mask %lu\n", result ? "PASS" : "FAIL", mask);
        ok = ok && result;
    }
    std::wstring readme = argv[1], license = readme.substr(0, last_sep(readme) + 1) + L"LICENSE";
    for (DWORD separate = 0; separate < 2; ++separate) {
        bool result = verify_multi(readme, license, separate);
        printf("%s native multi-select menu separate=%lu\n", result ? "PASS" : "FAIL", separate);
        ok = ok && result;
    }
    RegOverridePredefKey(HKEY_CURRENT_USER, nullptr);
    RegCloseKey(root);
    RegDeleteTreeW(HKEY_CURRENT_USER, sandbox.c_str());
    CoUninitialize();
    return ok ? 0 : 1;
}

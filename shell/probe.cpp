// Headless probe for the yzip context-menu handler, going through the shell's own menu composition
// (IShellFolder::GetUIObjectOf -> default context menu -> registered handlers), like Explorer does.
// usage: probe list|defaultonly|invoke|invokedlg <path> [<path> ...]   (all paths in one folder)
//        probe direct-list|direct-defaultonly|direct-invoke|direct-invokedlg <dll> <path> [<path> ...]
// prints one "ITEM\t<text>\t[icon]\t<verb>" line per top-level menu item and "SEP" per separator (UTF-8);
// "invoke" runs yzip's "<name>.zip으로 압축하기", "invokedlg" runs "yzip으로 압축하기...",
// "invoke:<verb>" runs the item with that verb (e.g. invoke:yzip.extractfolder).
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <stdio.h>
#include <string>
#include <vector>

static void put(const std::wstring& s) {
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0, nullptr, nullptr);
    std::string b(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &b[0], n, nullptr, nullptr);
    fwrite(b.data(), 1, b.size(), stdout);
    fputc('\n', stdout);
    fflush(stdout);
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 3)
        return 2;
    std::wstring mode = argv[1];
    bool direct = mode.rfind(L"direct-", 0) == 0;
    if (direct) {
        mode.erase(0, 7);
        if (argc < 4)
            return 2;
    }
    int first_path = direct ? 3 : 2;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    std::vector<PIDLIST_ABSOLUTE> abs;
    for (int i = first_path; i < argc; i++) {
        PIDLIST_ABSOLUTE p = nullptr;
        if (FAILED(SHParseDisplayName(argv[i], nullptr, &p, 0, nullptr))) {
            put(L"ERROR parse " + std::wstring(argv[i]));
            return 3;
        }
        abs.push_back(p);
    }
    IShellFolder* parent = nullptr;
    PCUITEMID_CHILD first = nullptr;
    if (FAILED(SHBindToParent(abs[0], IID_PPV_ARGS(&parent), &first))) {
        put(L"ERROR bind");
        return 3;
    }
    std::vector<PCUITEMID_CHILD> kids;
    for (auto p : abs)
        kids.push_back(ILFindLastID(p));
    IContextMenu* cm = nullptr;
    HRESULT hr = S_OK;
    HMODULE module = nullptr;
    IDataObject* data = nullptr;
    IClassFactory* factory = nullptr;
    IShellExtInit* init = nullptr;
    PIDLIST_ABSOLUTE parent_abs = nullptr;
    if (direct) {
        module = LoadLibraryW(argv[2]);
        if (!module) {
            put(L"ERROR LoadLibrary");
            return 3;
        }
        using GetClassObject = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
        auto get_class_object = reinterpret_cast<GetClassObject>(GetProcAddress(module, "DllGetClassObject"));
        static const CLSID clsid = {0x7e3b1c52, 0x9a4d, 0x4f1e, {0xb6, 0xc2, 0x3d, 0x8a, 0x5f, 0x0e, 0x9c, 0x71}};
        parent_abs = ILCloneFull(abs[0]);
        if (!get_class_object || !parent_abs || !ILRemoveLastID(parent_abs))
            hr = E_FAIL;
        if (SUCCEEDED(hr))
            hr = SHCreateDataObject(parent_abs, (UINT)kids.size(), kids.data(), nullptr, IID_PPV_ARGS(&data));
        if (SUCCEEDED(hr))
            hr = get_class_object(clsid, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr))
            hr = factory->CreateInstance(nullptr, IID_PPV_ARGS(&init));
        if (SUCCEEDED(hr))
            hr = init->Initialize(parent_abs, data, nullptr);
        if (SUCCEEDED(hr))
            hr = init->QueryInterface(IID_PPV_ARGS(&cm));
        if (SUCCEEDED(hr))
            put(L"DIRECT_DLL");
    } else {
        hr = parent->GetUIObjectOf(nullptr, (UINT)kids.size(), kids.data(), IID_IContextMenu, nullptr, (void**)&cm);
    }
    if (FAILED(hr)) {
        put(L"ERROR GetUIObjectOf");
        return 3;
    }
    HMENU menu = CreatePopupMenu();
    const UINT FIRST = 1;
    hr = cm->QueryContextMenu(menu, 0, FIRST, 0x7FFF, mode == L"defaultonly" ? CMF_DEFAULTONLY : CMF_NORMAL);
    if (FAILED(hr)) {
        put(L"ERROR QueryContextMenu");
        return 3;
    }
    int target = -1;
    for (int i = 0; i < GetMenuItemCount(menu); i++) {
        wchar_t text[512] = L"";
        MENUITEMINFOW mii = {sizeof(mii)};
        mii.fMask = MIIM_STRING | MIIM_ID | MIIM_BITMAP | MIIM_FTYPE;
        mii.dwTypeData = text;
        mii.cch = 512;
        if (!GetMenuItemInfoW(menu, i, TRUE, &mii))
            continue;
        if (mii.fType & MFT_SEPARATOR) {
            put(L"SEP");
            continue;
        }
        if (!text[0])
            continue;
        // the verb, not the text, identifies our item (Bandizip adds an item with the very same text)
        wchar_t verb[128] = L"";
        if ((int)mii.wID >= (int)FIRST && (int)mii.wID < 0x7FFF)
            cm->GetCommandString(mii.wID - FIRST, GCS_VERBW, nullptr, (LPSTR)verb, 128);
        put(L"ITEM\t" + std::wstring(text) + L"\t" + (mii.hbmpItem ? L"[icon]" : L"") + L"\t" + verb);
        std::wstring want = mode == L"invokedlg" ? L"yzip.compressdlg"
                          : mode.rfind(L"invoke:", 0) == 0 ? mode.substr(7) : L"yzip.compress";
        if (lstrcmpW(verb, want.c_str()) == 0)
            target = (int)mii.wID;
    }
    if (mode.rfind(L"invoke", 0) == 0) {
        if (target < 0) {
            put(L"ERROR no yzip item");
            return 4;
        }
        CMINVOKECOMMANDINFOEX ici = {sizeof(ici)};
        ici.fMask = CMIC_MASK_UNICODE;
        ici.lpVerb = MAKEINTRESOURCEA(target - FIRST);
        ici.lpVerbW = MAKEINTRESOURCEW(target - FIRST);
        ici.nShow = SW_SHOWNORMAL;
        hr = cm->InvokeCommand((LPCMINVOKECOMMANDINFO)&ici);
        put(SUCCEEDED(hr) ? L"INVOKED" : L"ERROR invoke");
        if (FAILED(hr))
            return 5;
    }
    cm->Release();
    if (init)
        init->Release();
    if (factory)
        factory->Release();
    if (data)
        data->Release();
    if (parent_abs)
        CoTaskMemFree(parent_abs);
    if (module)
        FreeLibrary(module);
    parent->Release();
    for (auto p : abs)
        CoTaskMemFree(p);
    CoUninitialize();
    return 0;
}

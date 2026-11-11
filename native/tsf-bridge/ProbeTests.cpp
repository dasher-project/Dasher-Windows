#include "TextContext.h"

#include <cstdio>
#include <cstdlib>

namespace {

const CLSID ServiceId = {0xf34a69ad, 0x9ad0, 0x49d7, {0x80, 0x1f, 0xdd, 0x95, 0xa1, 0xbb, 0xea, 0x46}};

void Check(bool success, const char* step) {
    if (!success) {
        std::fprintf(stderr, "%s failed\n", step);
        std::exit(1);
    }
}

struct BridgeWindowSearch {
    HMODULE library;
    HWND window{};
};

BOOL CALLBACK FindBridgeWindow(HWND window, LPARAM result) {
    auto& search = *reinterpret_cast<BridgeWindowSearch*>(result);
    if (GetWindowLongPtrW(window, GWLP_HINSTANCE) != reinterpret_cast<LONG_PTR>(search.library))
        return TRUE;
    wchar_t className[64]{};
    if (!GetClassNameW(window, className, ARRAYSIZE(className)) ||
        lstrcmpW(className, L"Dasher.TsfBridge.F34A69AD") != 0) return TRUE;
    search.window = window;
    return FALSE;
}

}

int wmain(int argc, wchar_t** argv) {
    using Microsoft::WRL::ComPtr;
    Check(argc == 2, "DLL argument");
    const auto library = LoadLibraryW(argv[1]);
    Check(library != nullptr, "LoadLibrary");
    using GetFactory = HRESULT (WINAPI*)(REFCLSID, REFIID, void**);
    auto getFactory = reinterpret_cast<GetFactory>(GetProcAddress(library, "DllGetClassObject"));
    Check(getFactory != nullptr, "GetProcAddress");
    Check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)), "CoInitializeEx");
    {
        ComPtr<IClassFactory> factory;
        Check(SUCCEEDED(getFactory(ServiceId, IID_PPV_ARGS(&factory))), "GetClassObject");
        ComPtr<ITfTextInputProcessor> service;
        Check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&service))), "CreateInstance");

        ComPtr<ITfThreadMgr> manager;
        Check(SUCCEEDED(CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER,
                                         IID_PPV_ARGS(&manager))), "Create thread manager");
        TfClientId client;
        Check(SUCCEEDED(manager->Activate(&client)), "Activate manager");
        for (int run = 0; run != 2; ++run) {
            Check(SUCCEEDED(service->Activate(manager.Get(), client)), "Activate service");
            BridgeWindowSearch search{library};
            EnumThreadWindows(GetCurrentThreadId(), FindBridgeWindow, reinterpret_cast<LPARAM>(&search));
            Check(search.window != nullptr, "Find bridge window");
            Check((GetWindowLongPtrW(search.window, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0, "Bridge window style");
            Check(!IsWindowVisible(search.window) && !GetWindow(search.window, GW_CHILD), "Hidden window without controls");
            Check(SUCCEEDED(service->Deactivate()), "Deactivate service");
            Check(!IsWindow(search.window), "Close bridge window");
        }
        Check(SUCCEEDED(manager->Deactivate()), "Deactivate manager");
    }
    CoUninitialize();
}

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

BOOL CALLBACK FindProbeWindow(HWND window, LPARAM result) {
    wchar_t className[64]{};
    if (!GetClassNameW(window, className, ARRAYSIZE(className)) ||
        lstrcmpW(className, L"Dasher.TsfProbe.F34A69AD") != 0) return TRUE;
    *reinterpret_cast<HWND*>(result) = window;
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
            HWND panel = nullptr;
            EnumThreadWindows(GetCurrentThreadId(), FindProbeWindow, reinterpret_cast<LPARAM>(&panel));
            Check(panel != nullptr, "Find panel");
            Check((GetWindowLongPtrW(panel, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0, "Panel style");
            Check(SUCCEEDED(service->Deactivate()), "Deactivate service");
            Check(!IsWindow(panel), "Close panel");
        }
        Check(SUCCEEDED(manager->Deactivate()), "Deactivate manager");
    }
    CoUninitialize();
}

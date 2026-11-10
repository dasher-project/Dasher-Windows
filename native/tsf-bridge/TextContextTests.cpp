#include "TextContext.h"

#include <cstdlib>
#include <cstdio>

using namespace tsf_bridge;

void Check(bool condition) {
    if (!condition) std::abort();
}

void PumpUntil(const bool& done) {
    const auto deadline = GetTickCount64() + 3000;
    while (!done && GetTickCount64() < deadline) {
        MSG message;
        if (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        } else {
            MsgWaitForMultipleObjectsEx(0, nullptr, 10, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        }
    }
    Check(done);
}

int main() {
    Snapshot document{1, L"one\n", L"two", L" \U0001F642"};
    Check(MatchesRange(document, 4, L""));
    Check(MatchesRange(document, 4, L"two"));
    Check(MatchesRange(document, 2, L"e\ntwo"));
    Check(MatchesRange(document, 8, L"\U0001F642"));
    Check(!MatchesRange(document, 9, L""));
    Check(!MatchesRange(document, 8, std::wstring_view(document.after).substr(1, 1)));
    Check(!MatchesRange(document, 4, L"old"));
    Check(!MatchesRange(document, static_cast<size_t>(-1), L""));
    Check(!MatchesRange(document, 10, L"x"));
    Check(MatchesRange(document, 10, L""));

    TextContext context(TF_CLIENTID_NULL);
    Check(context.Read(document) == TF_E_DISCONNECTED);
    Check(document.token == 0 && document.before.empty());
    Check(context.Replace(1, 0, L"", L"x") == TF_E_DISCONNECTED);

    Check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)));
    {
        Microsoft::WRL::ComPtr<ITfThreadMgr> manager;
        Check(SUCCEEDED(CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER,
                                         IID_PPV_ARGS(&manager))));
        TfClientId client;
        Check(SUCCEEDED(manager->Activate(&client)));
        Microsoft::WRL::ComPtr<ITfDocumentMgr> documentManager;
        Check(SUCCEEDED(manager->CreateDocumentMgr(&documentManager)));
        Microsoft::WRL::ComPtr<ITfContext> editor;
        TfEditCookie cookie;
        Check(SUCCEEDED(documentManager->CreateContext(client, 0, nullptr, &editor, &cookie)));
        Check(SUCCEEDED(documentManager->Push(editor.Get())));
        TextContext bridge(client);
        bridge.SetContext(editor.Get());
        Snapshot value;
        const auto read = bridge.Read(value);
        if (read != S_OK) std::fprintf(stderr, "TSF Read HRESULT: 0x%08lx\n", static_cast<unsigned long>(read));
        Check(read == S_OK);
        Check(bridge.Replace(value.token, 0, L"", L"one one") == S_OK);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"one one");
        Check(bridge.Replace(value.token, 4, L"one", L"two") == S_OK);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"one two");
        const auto stale = value.token;
        bridge.SetContext(nullptr);
        bridge.SetContext(editor.Get());
        Check(bridge.Replace(stale, 4, L"two", L"") == S_FALSE);
        Check(bridge.Read(value) == S_OK);
        Check(bridge.Replace(value.token, 3, L" two", L"") == S_OK);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"one");
        Check(bridge.Replace(value.token, 3, L"", L"\n\U0001F642") == S_OK);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"one\n\U0001F642");
        Check(bridge.Replace(value.token, 5, L"", L"x") == S_FALSE);
        Check(bridge.Read(value) == S_OK);
        Check(bridge.Replace(value.token, 4, L"\U0001F642", L"") == S_OK);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"one\n");
        TextContext otherClient(client);
        otherClient.SetContext(editor.Get());
        Snapshot otherValue;
        Check(otherClient.Read(otherValue) == S_OK);
        Check(otherClient.Replace(otherValue.token, 0, L"one", L"two") == S_OK);
        Check(bridge.Replace(value.token, 0, L"one", L"bad") == S_FALSE);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"two\n");
        otherClient.SetContext(nullptr);
        auto asyncBridge = std::make_shared<TextContext>(client);
        asyncBridge->SetContext(editor.Get());
        bool complete = false;
        Snapshot asyncValue;
        asyncBridge->ReadAsync([&](HRESULT result, Snapshot snapshot) {
            Check(result == S_OK);
            asyncValue = std::move(snapshot);
            complete = true;
        });
        PumpUntil(complete);
        complete = false;
        asyncBridge->ReplaceAsync(asyncValue.token, 0, L"two", L"async", [&](HRESULT result) {
            Check(result == S_OK);
            complete = true;
        });
        PumpUntil(complete);
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"async\n");
        // A Direct-mode client refreshes its token after each accepted edit.
        for (const auto* word : {L" one", L" two", L" three"}) {
            Check(bridge.Read(value) == S_OK);
            const auto end = (value.before + value.selection + value.after).size() - 1;
            Check(bridge.Replace(value.token, end, L"", word) == S_OK);
        }
        for (const auto* word : {L" three", L" two", L" one"}) {
            Check(bridge.Read(value) == S_OK);
            const auto end = (value.before + value.selection + value.after).size() - 1;
            Check(bridge.Replace(value.token, end - wcslen(word), word, L"") == S_OK);
        }
        Check(bridge.Read(value) == S_OK);
        Check(value.before + value.selection + value.after == L"async\n");
        complete = false;
        asyncBridge->ReadAsync([&](HRESULT result, Snapshot snapshot) {
            Check(result == S_FALSE && snapshot.token == 0);
            complete = true;
        });
        asyncBridge->SetContext(nullptr);
        PumpUntil(complete);
        bridge.SetContext(nullptr);
        Check(SUCCEEDED(documentManager->Pop(TF_POPF_ALL)));
        Check(SUCCEEDED(manager->Deactivate()));
    }
    CoUninitialize();
}

#include "TextContext.h"

#include <cwchar>
#include <cstdio>
#include <iterator>
#include <new>
#include <utility>

namespace {
using Microsoft::WRL::ComPtr;
using tsf_bridge::Snapshot;
using tsf_bridge::TextContext;

const CLSID ServiceId = {0xf34a69ad, 0x9ad0, 0x49d7, {0x80, 0x1f, 0xdd, 0x95, 0xa1, 0xbb, 0xea, 0x46}};
const GUID ProfileId = {0x7f00df0c, 0xc312, 0x4914, {0xac, 0x5a, 0xce, 0xe0, 0x1e, 0x41, 0xb5, 0xd0}};
constexpr LANGID Languages[] = {0x0409, 0x0809};
constexpr wchar_t Title[] = L"Dasher TSF Probe";
constexpr wchar_t WindowClass[] = L"Dasher.TsfProbe.F34A69AD";
constexpr wchar_t Word[] = L"[dasher-test]";
constexpr wchar_t RegistryKey[] = L"Software\\Classes\\CLSID\\{F34A69AD-9AD0-49D7-801F-DD95A1BBEA46}";
constexpr UINT CaptureUndo = WM_APP + 1;
enum Button { Read = 100, Insert, Undo, Minimize, ConnectDasher };
HINSTANCE module;


void Trace(const char* event, HRESULT result = S_OK, size_t first = 0, size_t second = 0) {
    wchar_t path[MAX_PATH];
    const auto length = GetTempPathW(static_cast<DWORD>(std::size(path)), path);
    if (!length || length + std::size(L"dasher-tsf-probe.log") > std::size(path)) return;
    wcscat_s(path, L"dasher-tsf-probe.log");
    const auto file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    char line[200];
    const auto count = sprintf_s(line, "%llu pid=%lu tid=%lu %s hr=0x%08lX a=%zu b=%zu\r\n",
        GetTickCount64(), GetCurrentProcessId(), GetCurrentThreadId(), event,
        static_cast<unsigned long>(result), first, second);
    DWORD written;
    if (count > 0) WriteFile(file, line, static_cast<DWORD>(count), &written, nullptr);
    CloseHandle(file);
}

bool CanConnectToDasher() {
    HANDLE token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    TOKEN_ELEVATION elevation{};
    DWORD size;
    const auto ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
    CloseHandle(token);
    return ok && !elevation.TokenIsElevated;
}

class Clipboard {
public:
    explicit Clipboard(HWND owner) : open_(OpenClipboard(owner) != FALSE) {}
    ~Clipboard() { if (open_) CloseClipboard(); }

    bool Copy(std::wstring_view text) const {
        if (!open_) return false;
        const auto bytes = (text.size() + 1) * sizeof(wchar_t);
        const auto memory = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, bytes);
        if (!memory) return false;
        auto destination = static_cast<wchar_t*>(GlobalLock(memory));
        if (!destination) {
            GlobalFree(memory);
            return false;
        }
        text.copy(destination, text.size());
        GlobalUnlock(memory);
        if (EmptyClipboard() && SetClipboardData(CF_UNICODETEXT, memory)) return true;
        GlobalFree(memory);
        return false;
    }

    std::wstring Read() const {
        if (!open_) return {};
        const auto memory = GetClipboardData(CF_UNICODETEXT);
        if (!memory) return {};
        const auto capacity = GlobalSize(memory) / sizeof(wchar_t);
        const auto source = static_cast<const wchar_t*>(GlobalLock(memory));
        if (!source) return {};
        try {
            const auto length = wcsnlen_s(source, capacity);
            auto text = length < capacity ? std::wstring(source, length) : std::wstring{};
            GlobalUnlock(memory);
            return text;
        } catch (...) {
            GlobalUnlock(memory);
            throw;
        }
    }

private:
    bool open_;
};

struct EditRequest {
    std::uint32_t version;
    std::uint16_t length;
    std::uint16_t operation;
    std::uint64_t deadline;
    std::uint64_t editorEpoch;
    wchar_t text[tsf_bridge::ContextLimit];
};
static_assert(sizeof(EditRequest) == 1048);

struct EditConnection {
    HANDLE handle = INVALID_HANDLE_VALUE;
    ~EditConnection() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }

    void ReplyContext(HRESULT result, std::uint64_t epoch, std::wstring_view before = {}) const {
        struct {
            HRESULT result;
            std::uint32_t length;
            std::uint64_t epoch;
            wchar_t before[tsf_bridge::ContextLimit];
        } response{result, static_cast<std::uint32_t>(before.size()), epoch, {}};
        static_assert(sizeof(response) == 1040);
        before.copy(response.before, before.size());
        DWORD written;
        WriteFile(handle, &response, sizeof(response), &written, nullptr);
    }

    void Reply(HRESULT result, std::uint64_t epoch = 0) const {
        const std::uint64_t response[] = {static_cast<std::uint32_t>(result), epoch};
        DWORD written;
        WriteFile(handle, response, sizeof(response), &written, nullptr);
    }
};

std::wstring Text(const Snapshot& snapshot) {
    return snapshot.before + snapshot.selection + snapshot.after;
}

class Panel : public std::enable_shared_from_this<Panel> {
public:
    Panel(ITfThreadMgr* manager, TfClientId client)
        : manager_(manager), bridge_(std::make_shared<TextContext>(client)),
          previewBridge_(std::make_shared<TextContext>(client)) {}

    bool Create() {
        WNDCLASSW window{};
        window.lpfnWndProc = Dispatch;
        window.hInstance = module;
        window.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        window.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        window.lpszClassName = WindowClass;
        if (!RegisterClassW(&window) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
        hwnd_ = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            WindowClass, Title, WS_POPUP | WS_BORDER, 24, 24, 510, 228,
            nullptr, nullptr, module, this);
        if (!hwnd_) return false;
        AddControl(L"STATIC", L"Dasher TSF Probe  |  Use a scratch document", 0, 10, 8, 482, 22);
        AddControl(L"STATIC", L"Read", Read, 10, 36, 85, 28);
        AddControl(L"STATIC", L"Insert test word", Insert, 100, 36, 115, 28);
        AddControl(L"STATIC", L"Undo test insert", Undo, 220, 36, 115, 28);
        AddControl(L"STATIC", L"Minimize for 2s", Minimize, 340, 36, 155, 28);
        AddControl(L"STATIC", L"Allow Dasher in this editor", ConnectDasher, 10, 73, 190, 24);
        status_ = AddControl(L"STATIC", L"Focus a test editor, then click Read.", 0, 10, 101, 485, 34);
        preview_ = AddControl(L"STATIC", L"", 0, 10, 138, 485, 80);
        if (!status_ || !preview_ || !SetTimer(hwnd_, 1, 100, nullptr)) {
            Close();
            return false;
        }
        return true;
    }

    void Close() {
        SetDocument(nullptr);
        if (hwnd_) DestroyWindow(std::exchange(hwnd_, nullptr));
    }

    void SetDocument(ITfDocumentMgr* document, bool contextChanged = false) {
        if (!contextChanged && document == document_.Get()) return;
        Trace("document-changed", S_OK, document != nullptr, undo_.token != 0);
        ++epoch_;
        bridge_->SetContext(nullptr);
        previewBridge_->SetContext(nullptr);
        context_.Reset();
        document_ = document;
        undo_ = {};
        expected_.clear();
        busy_ = false;
        if (preview_) SetWindowTextW(preview_, L"");
        if (status_) SetWindowTextW(status_, L"Editor changed. Click Read to inspect it.");
    }

    void RefreshDocument() {
        ComPtr<ITfContext> context;
        if (document_) document_->GetTop(&context);
        if (context.Get() == context_.Get()) return;
        auto document = document_;
        SetDocument(document.Get(), true);
        context_ = std::move(context);
        bridge_->SetContext(context_.Get());
        previewBridge_->SetContext(context_.Get());
    }

private:
    HWND AddControl(const wchar_t* kind, const wchar_t* text, int id, int x, int y, int w, int h) {
        const auto child = CreateWindowExW(0, kind, text, WS_CHILD | WS_VISIBLE | (id ? SS_NOTIFY | SS_CENTER | SS_CENTERIMAGE | WS_BORDER : SS_NOPREFIX),
            x, y, w, h, hwnd_, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), module, nullptr);
        SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), FALSE);
        return child;
    }

    bool EditorForeground() const {
        DWORD process = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), &process);
        BOOL focused = FALSE;
        return process == GetCurrentProcessId() &&
            SUCCEEDED(manager_->IsThreadFocus(&focused)) && focused;
    }

    void PollEditConnection() {
        if (!canConnectToDasher_ || !context_ || !EditorForeground()) {
            connection_.reset();
            return;
        }
        if (!connection_) {
            wchar_t path[96];
            swprintf_s(path, L"\\\\.\\pipe\\Dasher.Tsf.Edit.v3.%lu", GetCurrentProcessId());
            auto connection = std::make_shared<EditConnection>();
            connection->handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                0, nullptr, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (connection->handle == INVALID_HANDLE_VALUE) return;
            ULONG serverProcess;
            if (!GetNamedPipeServerProcessId(connection->handle, &serverProcess) ||
                serverProcess == GetCurrentProcessId()) return;
            DWORD mode = PIPE_READMODE_BYTE | PIPE_NOWAIT;
            if (!SetNamedPipeHandleState(connection->handle, &mode, nullptr, nullptr)) {
                Trace("pipe-mode-failed", HRESULT_FROM_WIN32(GetLastError()));
                return;
            }
            Trace("pipe-connected");
            connection_ = std::move(connection);
        }

        DWORD available;
        if (!PeekNamedPipe(connection_->handle, nullptr, 0, nullptr, &available, nullptr)) {
            connection_.reset();
            return;
        }
        if (available < sizeof(EditRequest)) return;
        auto connection = std::exchange(connection_, nullptr);
        EditRequest request{};
        DWORD count;
        if (!ReadFile(connection->handle, &request, sizeof(request), &count, nullptr) ||
            count != sizeof(request) || request.version != 3 || (!request.length && request.operation < 2) ||
            request.length > tsf_bridge::ContextLimit || request.operation > 8 ||
            GetTickCount64() >= request.deadline || busy_) {
            if (request.operation == 2) connection->ReplyContext(E_INVALIDARG, 0);
            else connection->Reply(E_INVALIDARG);
            return;
        }

        Trace("pipe-request", S_OK, request.operation, request.length);
        if (request.operation == 2) {
            auto weak = weak_from_this();
            const auto epoch = epoch_;
            previewBridge_->ReadAsync([weak, epoch, connection, deadline = request.deadline](HRESULT result, Snapshot value) {
                const auto panel = weak.lock();
                if (!panel || panel->epoch_ != epoch || !panel->EditorForeground() || GetTickCount64() >= deadline)
                    result = S_FALSE;
                Trace("context-read", result, value.before.size(), value.selection.size());
                if (result == S_OK) connection->ReplyContext(result, epoch, value.before);
                else connection->ReplyContext(result, 0);
            });
            return;
        }

        if (request.editorEpoch && request.editorEpoch != epoch_) {
            connection->Reply(S_FALSE);
            return;
        }
        busy_ = true;
        undo_ = {};
        auto weak = weak_from_this();
        auto epoch = epoch_;
        bridge_->ReadAsync([weak, epoch, connection, request](HRESULT result, Snapshot value) {
            auto panel = weak.lock();
            if (!panel || panel->epoch_ != epoch) {
                connection->Reply(S_FALSE);
                return;
            }

            std::wstring text(request.text, request.length);
            const bool backspace = request.operation == 3;
            const bool deleteText = request.operation == 1;
            const bool move = request.operation == 4 || request.operation == 5;
            const bool copy = request.operation == 6;
            const bool cut = request.operation == 7;
            const bool paste = request.operation == 8;
            if (result != S_OK || !panel->EditorForeground() ||
                GetTickCount64() >= request.deadline || (request.operation < 2 && !value.selection.empty()) ||
                (deleteText && !value.before.ends_with(text))) {
                panel->busy_ = false;
                connection->Reply(result == S_OK ? S_FALSE : result);
                return;
            }

            if (copy || cut) {
                const auto copied = value.selection.empty() || Clipboard(panel->hwnd_).Copy(value.selection);
                if (copy || !copied || value.selection.empty()) {
                    panel->busy_ = false;
                    connection->Reply(copied ? S_OK : E_FAIL, epoch);
                    return;
                }
            }
            std::wstring replacement;
            if (paste) {
                replacement = Clipboard(panel->hwnd_).Read();
                if (replacement.empty()) {
                    panel->busy_ = false;
                    connection->Reply(S_OK, epoch);
                    return;
                }
            }

            auto offset = value.before.size();
            if (move) {
                text.clear();
                if (request.operation == 4 && value.selection.empty() && offset > 0) {
                    --offset;
                    if (offset > 0 && value.before[offset] >= 0xDC00 && value.before[offset] <= 0xDFFF)
                        --offset;
                } else if (request.operation == 5) {
                    offset += value.selection.size();
                    if (value.selection.empty() && !value.after.empty()) {
                        ++offset;
                        if (value.after.size() > 1 && value.after.front() >= 0xD800 && value.after.front() <= 0xDBFF)
                            ++offset;
                    }
                }
            } else if (backspace) {
                text = value.selection;
                if (text.empty() && !value.before.empty()) {
                    --offset;
                    if (offset > 0 && value.before[offset] >= 0xDC00 && value.before[offset] <= 0xDFFF &&
                        value.before[offset - 1] >= 0xD800 && value.before[offset - 1] <= 0xDBFF)
                        --offset;
                    text = value.before.substr(offset);
                }
                if (text.empty()) {
                    panel->busy_ = false;
                    connection->Reply(S_OK, epoch);
                    return;
                }
            } else if (deleteText) {
                offset -= text.size();
            }
            const bool removing = backspace || deleteText || cut;
            if (cut || paste) text = value.selection;
            if (!removing && !paste) replacement = std::move(text);
            panel->bridge_->ReplaceAsync(value.token, offset,
                removing || paste ? text : L"", std::move(replacement),
                [weak, epoch, connection](HRESULT editResult) {
                    if (auto panel = weak.lock(); panel && panel->epoch_ == epoch) panel->busy_ = false;
                    Trace("dasher-edit", editResult);
                    connection->Reply(editResult, epoch);
                }, [weak, deadline = request.deadline] {
                    const auto panel = weak.lock();
                    return panel && panel->EditorForeground() && GetTickCount64() < deadline;
                });
        });
    }

    void Status(const wchar_t* message, HRESULT result) {
        wchar_t text[180];
        swprintf_s(text, L"%ls (0x%08lX)", message, static_cast<unsigned long>(result));
        SetWindowTextW(status_, text);
    }

    void Command(int command) {
        Trace("command", S_OK, command, undo_.token != 0);
        if (command == ConnectDasher) {
            if (!context_ || !EditorForeground()) return;
            dasherEnabled_ = !dasherEnabled_;
            connection_.reset();
            bridge_->SetContext(context_.Get());
            previewBridge_->SetContext(context_.Get());
            ++epoch_;
            busy_ = false;
            undo_ = {};
            SetWindowTextW(GetDlgItem(hwnd_, ConnectDasher),
                dasherEnabled_ ? L"Disconnect Dasher" : L"Allow Dasher in this editor");
            Status(dasherEnabled_ ? L"Dasher connection enabled" : L"Dasher disconnected", S_OK);
            return;
        }
        if (command == Minimize) {
            restoreAt_ = GetTickCount64() + 2000;
            ShowWindow(hwnd_, SW_SHOWMINNOACTIVE);
            return;
        }
        if (busy_ || !EditorForeground()) return;
        RefreshDocument();
        if (!context_) return;
        if (command == Undo) {
            if (!undo_.token) {
                SetWindowTextW(status_, L"No verified test insertion to undo.");
                return;
            }
            busy_ = true;
            Edit(std::exchange(undo_, {}).token, offset_, Word, L"", false);
            return;
        }

        busy_ = true;
        auto weak = weak_from_this();
        auto epoch = epoch_;
        (command == Read ? previewBridge_ : bridge_)->ReadAsync([weak, epoch, command](HRESULT result, Snapshot value) {
            auto panel = weak.lock();
            if (!panel || panel->epoch_ != epoch) return;
            panel->busy_ = false;
            if (result != S_OK) { panel->Status(L"Read failed", result); return; }
            if (command == Read) {

                auto display = L"Before: " + value.before + L"\r\nSelection: " +
                    value.selection + L"\r\nAfter: " + value.after;
                SetWindowTextW(panel->preview_, display.c_str());
                panel->Status(L"Context read", S_OK);
                return;
            }
            if (!value.selection.empty() || value.before.size() >= tsf_bridge::ContextLimit - std::size(Word) ||
                value.after.size() >= tsf_bridge::ContextLimit) {
                SetWindowTextW(panel->status_, L"Use a short scratch document and collapse the selection.");
                return;
            }
            panel->offset_ = value.before.size();
            panel->expected_ = value.before + Word + value.after;
            panel->undo_ = {};
            panel->busy_ = true;
            // Queue outside the read lock. The token is rechecked in the write lock.
            panel->insertToken_ = value.token;
            PostMessageW(panel->hwnd_, CaptureUndo, 0, 0);
        });
    }

    void Edit(std::uint64_t token, size_t offset, std::wstring expected,
              std::wstring replacement, bool inserting) {
        auto weak = weak_from_this();
        auto epoch = epoch_;
        bridge_->ReplaceAsync(token, offset, std::move(expected), std::move(replacement),
            [weak, epoch, inserting](HRESULT result) {
                auto panel = weak.lock();
                if (!panel || panel->epoch_ != epoch) return;
                Trace(inserting ? "insert-complete" : "undo-complete", result);
                if (inserting && (result == S_OK || result == tsf_bridge::EditAppliedCaretUnavailable)) {
                    PostMessageW(panel->hwnd_, CaptureUndo, 1, 0);
                    return;
                }
                panel->busy_ = false;
                panel->Status(result == S_OK ? L"Test word removed" :
                    result == tsf_bridge::EditAppliedCaretUnavailable ? L"Test word removed. Caret unavailable" :
                    result == S_FALSE ? L"Editor changed. Edit skipped" : L"Edit failed", result);
            });
    }

    void ArmUndo() {
        auto weak = weak_from_this();
        auto epoch = epoch_;
        bridge_->ReadAsync([weak, epoch](HRESULT result, Snapshot value) {
            auto panel = weak.lock();
            if (!panel || panel->epoch_ != epoch) return;
            panel->busy_ = false;
            const bool verified = result == S_OK && Text(value) == panel->expected_;
            Trace(verified ? "undo-armed" : "undo-unverified", result,
                Text(value).size(), panel->expected_.size());
            if (verified) {
                panel->undo_ = std::move(value);
                SetWindowTextW(panel->status_, L"Inserted. Minimize and restore, then click Undo test insert.");
            } else {
                SetWindowTextW(panel->status_, L"Inserted. Context could not be verified for undo.");
            }
        });
    }

    static LRESULT CALLBACK Dispatch(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
        if (message == WM_NCCREATE) {
            auto create = reinterpret_cast<const CREATESTRUCTW*>(lparam);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
        }
        auto self = reinterpret_cast<Panel*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (!self) return DefWindowProcW(hwnd, message, wparam, lparam);
        try {
            switch (message) {
            case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
            case WM_TIMER:
                self->RefreshDocument();
                self->PollEditConnection();
                return 0;
            case WM_COMMAND:
                if (HIWORD(wparam) == STN_CLICKED) self->Command(LOWORD(wparam));
                return 0;
            case CaptureUndo:
                if (!self->busy_ || !self->EditorForeground()) { self->busy_ = false; return 0; }
                if (wparam) self->ArmUndo();
                else self->Edit(self->insertToken_, self->offset_, L"", Word, true);
                return 0;
            }
        } catch (...) {
            self->busy_ = false;
            self->Status(L"Probe error", E_FAIL);
            return 0;
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    ComPtr<ITfThreadMgr> manager_;
    std::shared_ptr<TextContext> bridge_, previewBridge_;
    ComPtr<ITfDocumentMgr> document_;
    ComPtr<ITfContext> context_;
    HWND hwnd_{}, status_{}, preview_{};
    std::uint64_t epoch_{GetTickCount64()}, insertToken_{}, restoreAt_{};
    size_t offset_{};
    const bool canConnectToDasher_ = CanConnectToDasher();
    std::shared_ptr<EditConnection> connection_;
    Snapshot undo_;
    std::wstring expected_;
    bool busy_{};
    bool dasherEnabled_{};
};

class Service final : public ITfTextInputProcessor, public ITfThreadMgrEventSink,
                      public ITfThreadFocusSink {
public:



    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == IID_IUnknown || id == IID_ITfTextInputProcessor)
            *result = static_cast<ITfTextInputProcessor*>(this);
        else if (id == IID_ITfThreadMgrEventSink)
            *result = static_cast<ITfThreadMgrEventSink*>(this);
        else if (id == IID_ITfThreadFocusSink)
            *result = static_cast<ITfThreadFocusSink*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE Activate(ITfThreadMgr* manager, TfClientId client) override {
        if (!manager || manager_) return E_UNEXPECTED;
        Trace("profile-activate");
        try {
            panel_ = std::make_shared<Panel>(manager, client);
            manager_ = manager;
            auto result = manager_.As(&source_);
            if (SUCCEEDED(result)) result = source_->AdviseSink(
                IID_ITfThreadMgrEventSink, static_cast<ITfThreadMgrEventSink*>(this), &documentCookie_);
            if (SUCCEEDED(result)) result = source_->AdviseSink(
                IID_ITfThreadFocusSink, static_cast<ITfThreadFocusSink*>(this), &threadCookie_);
            if (SUCCEEDED(result) && !panel_->Create()) result = E_FAIL;
            if (SUCCEEDED(result)) result = OnSetThreadFocus();
            Trace("profile-activate-result", result);
            if (FAILED(result)) Deactivate();
            return result;
        } catch (...) {
            Deactivate();
            return E_OUTOFMEMORY;
        }
    }

    HRESULT STDMETHODCALLTYPE Deactivate() override {
        if (panel_) std::exchange(panel_, nullptr)->Close();
        if (documentCookie_ != TF_INVALID_COOKIE) source_->UnadviseSink(
            std::exchange(documentCookie_, TF_INVALID_COOKIE));
        if (threadCookie_ != TF_INVALID_COOKIE) source_->UnadviseSink(
            std::exchange(threadCookie_, TF_INVALID_COOKIE));
        source_.Reset();
        manager_.Reset();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnInitDocumentMgr(ITfDocumentMgr*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnUninitDocumentMgr(ITfDocumentMgr*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnPushContext(ITfContext*) override { return RefreshDocument(); }
    HRESULT STDMETHODCALLTYPE OnPopContext(ITfContext*) override { return RefreshDocument(); }
    HRESULT STDMETHODCALLTYPE OnSetFocus(ITfDocumentMgr* document, ITfDocumentMgr*) override {
        if (panel_) panel_->SetDocument(document);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSetThreadFocus() override {
        ComPtr<ITfDocumentMgr> document;
        if (manager_) manager_->GetFocus(&document);
        return OnSetFocus(document.Get(), nullptr);
    }
    HRESULT STDMETHODCALLTYPE OnKillThreadFocus() override { return OnSetFocus(nullptr, nullptr); }

private:
    HRESULT RefreshDocument() {
        if (panel_) panel_->RefreshDocument();
        return S_OK;
    }
    ULONG refs_{1};
    ComPtr<ITfThreadMgr> manager_;
    ComPtr<ITfSource> source_;
    std::shared_ptr<Panel> panel_;
    DWORD documentCookie_{TF_INVALID_COOKIE}, threadCookie_{TF_INVALID_COOKIE};
};

class Factory final : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != IID_IUnknown && id != IID_IClassFactory) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto instance = new (std::nothrow) Service;
        if (!instance) return E_OUTOFMEMORY;
        const auto query = instance->QueryInterface(id, result);
        instance->Release();
        return query;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
private:
    ULONG refs_{1};
};

HRESULT RegisterCom(const wchar_t* path) {
    const auto key = std::wstring(RegistryKey) + L"\\InprocServer32";
    auto result = RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), nullptr, REG_SZ,
        path, static_cast<DWORD>((wcslen(path) + 1) * sizeof(wchar_t)));
    if (result != ERROR_SUCCESS) return HRESULT_FROM_WIN32(result);
    constexpr wchar_t threading[] = L"Apartment";
    return HRESULT_FROM_WIN32(RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), L"ThreadingModel",
        REG_SZ, threading, sizeof(threading)));
}

HRESULT UpdateRegistration(bool install) {
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return initialized;
    const auto result = [&]() -> HRESULT {
        ComPtr<ITfInputProcessorProfileMgr> profiles;
        auto hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&profiles));
        if (FAILED(hr)) return hr;
        ComPtr<ITfCategoryMgr> categories;
        hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&categories));
        if (FAILED(hr)) return hr;

        if (!install) {
            for (const auto language : Languages) profiles->UnregisterProfile(ServiceId, language, ProfileId, 0);
            const auto category = categories->UnregisterCategory(ServiceId, GUID_TFCAT_TIP_KEYBOARD, ServiceId);
            const auto registry = RegDeleteTreeW(HKEY_LOCAL_MACHINE, RegistryKey);

            if (FAILED(category)) return category;
            return registry == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(registry);
        }

        wchar_t path[32768];
        const auto length = GetModuleFileNameW(module, path, static_cast<DWORD>(std::size(path)));
        if (!length || length >= std::size(path)) return HRESULT_FROM_WIN32(GetLastError());
        hr = RegisterCom(path);
        if (SUCCEEDED(hr)) hr = categories->RegisterCategory(ServiceId, GUID_TFCAT_TIP_KEYBOARD, ServiceId);
        for (const auto language : Languages) {
            if (FAILED(hr)) break;
            hr = profiles->RegisterProfile(ServiceId, language, ProfileId, Title,
                static_cast<ULONG>(std::size(Title) - 1), path, length, 0, nullptr, 0, TRUE, 0);
        }
        if (FAILED(hr)) {
            for (const auto language : Languages) profiles->UnregisterProfile(ServiceId, language, ProfileId, 0);
            categories->UnregisterCategory(ServiceId, GUID_TFCAT_TIP_KEYBOARD, ServiceId);
            RegDeleteTreeW(HKEY_LOCAL_MACHINE, RegistryKey);
            return hr;
        }
        return S_OK;
    }();
    CoUninitialize();
    return result;
}
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    return TRUE;
}

// Window classes and queued edit sessions can outlive TSF deactivation.
STDAPI DllCanUnloadNow() { return S_FALSE; }
STDAPI DllGetClassObject(REFCLSID clsid, REFIID id, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != ServiceId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory;
    if (!factory) return E_OUTOFMEMORY;
    const auto query = factory->QueryInterface(id, result);
    factory->Release();
    return query;
}
STDAPI DllRegisterServer() {
    try { return UpdateRegistration(true); } catch (...) { return E_OUTOFMEMORY; }
}
STDAPI DllUnregisterServer() {
    try { return UpdateRegistration(false); } catch (...) { return E_OUTOFMEMORY; }
}

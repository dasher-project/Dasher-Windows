#include "TextContext.h"

#include <atomic>
#include <functional>
#include <limits>
#include <new>
#include <utility>

namespace tsf_bridge {
namespace {

using Microsoft::WRL::ComPtr;
std::atomic<std::uint64_t> nextToken{};

class EditSession final : public ITfEditSession {
public:
    explicit EditSession(std::function<HRESULT(TfEditCookie)> action)
        : action_(std::move(action)) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(ITfEditSession)) return E_NOINTERFACE;
        *result = static_cast<ITfEditSession*>(this);
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override {
        try {
            return action_(cookie);
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

private:
    ULONG references_{1};
    std::function<HRESULT(TfEditCookie)> action_;
};

HRESULT Run(ITfContext* context, TfClientId client, DWORD access,
            std::function<HRESULT(TfEditCookie)> action) {
    ComPtr<ITfEditSession> session;
    session.Attach(new (std::nothrow) EditSession(std::move(action)));
    if (!session) return E_OUTOFMEMORY;
    HRESULT result = E_UNEXPECTED;
    const auto request = context->RequestEditSession(client, session.Get(), access, &result);
    return FAILED(request) ? request : result;
}

HRESULT GetText(ITfRange* range, TfEditCookie cookie, std::wstring& text,
                ULONG limit = ContextLimit) {
    wchar_t buffer[3 * ContextLimit + 1];
    ULONG count = 0;
    const auto result = range->GetText(cookie, 0, buffer, limit + 1, &count);
    if (FAILED(result)) return result;
    if (count > limit) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    text.assign(buffer, count);
    return S_OK;
}

HRESULT Capture(ITfContext* context, TfEditCookie cookie, Snapshot& snapshot,
                ComPtr<ITfRange>& selection) {
    TF_SELECTION selected{};
    ULONG count = 0;
    auto result = context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selected, &count);
    selection.Attach(selected.range);
    if (FAILED(result)) return result;
    if (count != 1 || !selection) return TF_E_DISCONNECTED;

    ComPtr<ITfRange> before, after;
    LONG moved;
    if (FAILED(result = selection->Clone(&before)) ||
        FAILED(result = selection->Clone(&after)) ||
        FAILED(result = before->Collapse(cookie, TF_ANCHOR_START)) ||
        FAILED(result = before->ShiftStart(cookie, -ContextLimit, &moved, nullptr)) ||
        FAILED(result = after->Collapse(cookie, TF_ANCHOR_END)) ||
        FAILED(result = after->ShiftEnd(cookie, ContextLimit, &moved, nullptr)) ||
        FAILED(result = GetText(before.Get(), cookie, snapshot.before)) ||
        FAILED(result = GetText(selection.Get(), cookie, snapshot.selection))) return result;
    if (FAILED(result = GetText(after.Get(), cookie, snapshot.after))) return result;

    // A bounded read can start or end in the middle of a UTF-16 pair.
    // Trim only the outer edges so offsets remain relative to the selection.
    if (!snapshot.before.empty() && snapshot.before.front() >= 0xDC00 &&
        snapshot.before.front() <= 0xDFFF) snapshot.before.erase(0, 1);
    if (!snapshot.after.empty() && snapshot.after.back() >= 0xD800 &&
        snapshot.after.back() <= 0xDBFF) snapshot.after.pop_back();
    return S_OK;
}

} // namespace

bool MatchesRange(const Snapshot& snapshot, size_t start, std::wstring_view expected) {
    const auto text = snapshot.before + snapshot.selection + snapshot.after;
    if (start > text.size() || expected.size() > text.size() - start) return false;
    const auto splitsSurrogate = [&](size_t offset) {
        return offset > 0 && offset < text.size() &&
               text[offset - 1] >= 0xD800 && text[offset - 1] <= 0xDBFF &&
               text[offset] >= 0xDC00 && text[offset] <= 0xDFFF;
    };
    return !splitsSurrogate(start) && !splitsSurrogate(start + expected.size()) &&
           std::wstring_view(text).substr(start, expected.size()) == expected;
}

void TextContext::SetContext(ITfContext* context) {
    ++contextEpoch_;
    context_ = context;
    selection_.Reset();
    snapshot_ = {};
}

HRESULT TextContext::ReadLocked(ITfContext* context, TfEditCookie cookie, Snapshot& snapshot,
                                bool preserveUnchangedToken) {
    Snapshot captured;
    ComPtr<ITfRange> selection;
    const auto result = Capture(context, cookie, captured, selection);
    if (FAILED(result)) return result;
    LONG start = 0, end = 0;
    const bool unchanged = preserveUnchangedToken && selection_ &&
        captured.before == snapshot_.before && captured.selection == snapshot_.selection &&
        captured.after == snapshot_.after &&
        SUCCEEDED(selection->CompareStart(cookie, selection_.Get(), TF_ANCHOR_START, &start)) &&
        SUCCEEDED(selection->CompareEnd(cookie, selection_.Get(), TF_ANCHOR_END, &end)) &&
        start == 0 && end == 0;
    captured.token = unchanged ? snapshot_.token : ++nextToken;
    snapshot = captured;
    snapshot_ = std::move(captured);
    selection_ = std::move(selection);
    return S_OK;
}

HRESULT TextContext::Read(Snapshot& snapshot) {
    snapshot = {};
    selection_.Reset();
    snapshot_ = {};
    auto context = context_;
    if (!context) return TF_E_DISCONNECTED;
    return Run(context.Get(), clientId_, TF_ES_SYNC | TF_ES_READ, [&](TfEditCookie cookie) {
        return ReadLocked(context.Get(), cookie, snapshot);
    });
}

HRESULT TextContext::Replace(std::uint64_t token, size_t start, std::wstring_view expected,
                             std::wstring_view replacement) {
    auto context = context_;
    if (!context) return TF_E_DISCONNECTED;
    return Run(context.Get(), clientId_, TF_ES_SYNC | TF_ES_READWRITE, [&](TfEditCookie cookie) {
        return ReplaceLocked(context.Get(), cookie, token, start, expected, replacement);
    });
}

void TextContext::ReadAsync(ReadCompletion completion, bool preserveUnchangedToken) {
    auto weak = weak_from_this();
    auto context = context_;
    if (!context) { completion(TF_E_DISCONNECTED, {}); return; }
    if (weak.expired()) { completion(E_UNEXPECTED, {}); return; }
    auto epoch = contextEpoch_;
    const auto result = Run(context.Get(), clientId_, TF_ES_ASYNC | TF_ES_READ,
        [weak, epoch, completion, preserveUnchangedToken](TfEditCookie cookie) {
            Snapshot snapshot;
            auto self = weak.lock();
            auto result = self && epoch == self->contextEpoch_
                ? self->ReadLocked(self->context_.Get(), cookie, snapshot, preserveUnchangedToken) : S_FALSE;
            completion(result, std::move(snapshot));
            return result;
        });
    if (FAILED(result)) completion(result, {});
}

void TextContext::ReplaceAsync(std::uint64_t token, size_t start, std::wstring expected,
                               std::wstring replacement, EditCompletion completion,
                               std::function<bool()> canApply, bool captureAfterEdit) {
    auto weak = weak_from_this();
    auto context = context_;
    if (!context) { completion(TF_E_DISCONNECTED); return; }
    if (weak.expired()) { completion(E_UNEXPECTED); return; }
    auto epoch = contextEpoch_;
    const auto result = Run(context.Get(), clientId_, TF_ES_ASYNC | TF_ES_READWRITE,
        [weak, epoch, token, start, expected = std::move(expected),
         replacement = std::move(replacement), completion, canApply = std::move(canApply),
         captureAfterEdit](TfEditCookie cookie) {
            auto self = weak.lock();
            auto result = self && epoch == self->contextEpoch_ && (!canApply || canApply())
                ? self->ReplaceLocked(self->context_.Get(), cookie, token, start, expected, replacement) : S_FALSE;
            if (result == S_OK && captureAfterEdit) {
                Snapshot snapshot;
                if (self->ReadLocked(self->context_.Get(), cookie, snapshot) != S_OK)
                    result = EditAppliedCaretUnavailable;
            }
            completion(result);
            return result;
        });
    if (FAILED(result)) completion(result);
}

HRESULT TextContext::ReplaceLocked(ITfContext* context, TfEditCookie cookie, std::uint64_t token,
                                    size_t start, std::wstring_view expected,
                                    std::wstring_view replacement) {
    if (!selection_ || token != snapshot_.token) return S_FALSE;
    auto oldSelection = std::move(selection_);
    auto previous = std::exchange(snapshot_, {});
    if (replacement.size() > static_cast<size_t>(std::numeric_limits<LONG>::max())) return E_INVALIDARG;

    Snapshot current;
    ComPtr<ITfRange> range;
    auto result = Capture(context, cookie, current, range);
    if (FAILED(result)) return result;
    LONG startComparison, endComparison;
    if (FAILED(result = range->CompareStart(cookie, oldSelection.Get(), TF_ANCHOR_START, &startComparison)) ||
        FAILED(result = range->CompareEnd(cookie, oldSelection.Get(), TF_ANCHOR_END, &endComparison))) return result;
    if (startComparison || endComparison || current.before != previous.before ||
        current.selection != previous.selection || current.after != previous.after ||
        !MatchesRange(current, start, expected)) return S_FALSE;

    const auto offset = static_cast<LONG>(start) - static_cast<LONG>(current.before.size());
    LONG moved;
    if (FAILED(result = range->Collapse(cookie, TF_ANCHOR_START)) ||
        FAILED(result = offset < 0
        ? range->ShiftStart(cookie, offset, &moved, nullptr)
        : range->ShiftEnd(cookie, offset, &moved, nullptr))) return result;
    if (moved != offset) return S_FALSE;
    if (FAILED(result = range->Collapse(cookie, offset < 0 ? TF_ANCHOR_START : TF_ANCHOR_END)) ||
        FAILED(result = range->ShiftEnd(cookie, static_cast<LONG>(expected.size()), &moved, nullptr))) return result;
    if (moved != static_cast<LONG>(expected.size())) return S_FALSE;

    std::wstring actual;
    if (FAILED(result = GetText(range.Get(), cookie, actual, static_cast<ULONG>(expected.size())))) return result;
    if (actual != expected) return S_FALSE;
    if (expected.empty() && replacement.empty()) {
        TF_SELECTION caret{range.Get(), {TF_AE_NONE, FALSE}};
        return context->SetSelection(cookie, 1, &caret);
    }

    result = range->SetText(cookie, 0, replacement.empty() ? L"" : replacement.data(),
                            static_cast<LONG>(replacement.size()));
    if (result != S_OK) return result;

    // The next edit is relative to the caret. Providers can leave the
    // selection at its old location after SetText, so place it explicitly.
    // A caret failure here follows a committed edit and must never be retried.
    result = range->Collapse(cookie, TF_ANCHOR_END);
    if (FAILED(result)) return EditAppliedCaretUnavailable;
    TF_SELECTION caret{range.Get(), {TF_AE_NONE, FALSE}};
    return FAILED(context->SetSelection(cookie, 1, &caret)) ? EditAppliedCaretUnavailable : S_OK;
}

}

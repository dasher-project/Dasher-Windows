#pragma once

#include <msctf.h>
#include <wrl/client.h>

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <string_view>

namespace tsf_bridge {

inline constexpr LONG ContextLimit = 512;

struct Snapshot {
    std::uint64_t token{};
    std::wstring before;
    std::wstring selection;
    std::wstring after;
};

// Positions are UTF-16 code units in before + selection + after.
bool MatchesRange(const Snapshot& snapshot, size_t start, std::wstring_view expected);

// Use from the owning TSF apartment. Supply only the active, editable
// document context and call SetContext on focus changes (including loss).
class TextContext : public std::enable_shared_from_this<TextContext> {
public:
    explicit TextContext(TfClientId clientId) : clientId_(clientId) {}
    TextContext(const TextContext&) = delete;
    TextContext& operator=(const TextContext&) = delete;

    void SetContext(ITfContext* context);
    HRESULT Read(Snapshot& snapshot);
    // S_FALSE means the snapshot or expected text is stale. Re-read before
    // attempting another edit. TSF lock failures are returned to the caller.
    HRESULT Replace(std::uint64_t token, size_t start, std::wstring_view expected,
                    std::wstring_view replacement);

    // Asynchronous requests require a shared-owned context. Completion runs on
    // the TSF apartment, including on immediate request failure.
    using ReadCompletion = std::function<void(HRESULT, Snapshot)>;
    using EditCompletion = std::function<void(HRESULT)>;
    void ReadAsync(ReadCompletion completion);
    void ReplaceAsync(std::uint64_t token, size_t start, std::wstring expected,
                      std::wstring replacement, EditCompletion completion);

private:
    HRESULT ReadLocked(ITfContext* context, TfEditCookie cookie, Snapshot& snapshot);
    HRESULT ReplaceLocked(ITfContext* context, TfEditCookie cookie, std::uint64_t token,
                          size_t start, std::wstring_view expected, std::wstring_view replacement);
    std::uint64_t contextEpoch_{};
    TfClientId clientId_;
    Microsoft::WRL::ComPtr<ITfContext> context_;
    Microsoft::WRL::ComPtr<ITfRange> selection_;
    Snapshot snapshot_;
    std::uint64_t nextToken_{};
};

}

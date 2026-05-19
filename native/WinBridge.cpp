#include "WinDasherInterface.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <vector>
#include <windows.h>

extern "C" void WinSetDataDir(const char *dir);

namespace {

struct NativeSession {
    std::unique_ptr<WinDasherInterface> iface;
    int width = 0;
    int height = 0;
    float mouseX = 0.0f;
    float mouseY = 0.0f;
    bool mouseDown = false;

    explicit NativeSession(const std::string &dataDir)
        : iface(std::make_unique<WinDasherInterface>(dataDir)) {}
};

static inline NativeSession *fromHandle(int64_t handle) {
    return reinterpret_cast<NativeSession *>(static_cast<uintptr_t>(handle));
}

static inline int64_t toHandle(NativeSession *session) {
    return static_cast<int64_t>(reinterpret_cast<uintptr_t>(session));
}

static bool hasVisibleBoxCommands(const std::vector<int32_t> &commands) {
    for (size_t i = 0; i + 5 < commands.size(); i += 6) {
        const int op = commands[i];
        if (op == 3 || op == 4) {
            const int a = commands[i + 1];
            const int b = commands[i + 2];
            const int c = commands[i + 3];
            const int d = commands[i + 4];
            const int color = commands[i + 5];
            const int alpha = (color >> 24) & 0xFF;
            if (alpha < 24) continue;
            if (a == c || b == d) continue;
            return true;
        }
    }
    return false;
}

static void pushCommand(std::vector<int32_t> &commands, int32_t op, int32_t a, int32_t b, int32_t c, int32_t d, int32_t color) {
    commands.push_back(op);
    commands.push_back(a);
    commands.push_back(b);
    commands.push_back(c);
    commands.push_back(d);
    commands.push_back(color);
}

static void appendFallbackBoxes(NativeSession &session, std::vector<int32_t> &commands) {
    if (session.width <= 0 || session.height <= 0) return;

    const int w = session.width;
    const int h = session.height;
    const int midY = h / 2;
    const int left = w / 20;
    const int right = w - left;

    pushCommand(commands, 4, left, h / 8, right, midY - h / 20, static_cast<int32_t>(0xFF1B5E20));
    pushCommand(commands, 3, left, h / 8, right, midY - h / 20, static_cast<int32_t>(0xFF81C784));
    pushCommand(commands, 4, left, midY + h / 20, right, h - h / 8, static_cast<int32_t>(0xFF0D47A1));
    pushCommand(commands, 3, left, midY + h / 20, right, h - h / 8, static_cast<int32_t>(0xFF90CAF9));

    const int px = session.mouseDown ? static_cast<int>(session.mouseX) : (w / 2);
    const int py = session.mouseDown ? static_cast<int>(session.mouseY) : (h / 2);
    const int marker = std::max(10, std::min(w, h) / 45);

    pushCommand(commands, 1, px, py, marker, 1, static_cast<int32_t>(0xFFD50000));
    pushCommand(commands, 2, px - marker * 2, py, px + marker * 2, py, static_cast<int32_t>(0xFFFFCDD2));
    pushCommand(commands, 2, px, py - marker * 2, px, py + marker * 2, static_cast<int32_t>(0xFFFFCDD2));
}

struct FrameResult {
    int32_t *commands;
    int commandCount;
    char **strings;
    int stringCount;
};

static void freeFrameResult(FrameResult &result) {
    if (result.commands) {
        delete[] result.commands;
        result.commands = nullptr;
    }
    if (result.strings) {
        for (int i = 0; i < result.stringCount; ++i) {
            delete[] result.strings[i];
        }
        delete[] result.strings;
        result.strings = nullptr;
    }
    result.commandCount = 0;
    result.stringCount = 0;
}

}

extern "C" {

__declspec(dllexport) int64_t dasher_create(const char *dataDir) {
    if (!dataDir) return 0;
    std::string dir(dataDir);
    WinSetDataDir(dataDir);
    auto *session = new NativeSession(dir);
    OutputDebugStringA("dasher_create session created\n");
    return toHandle(session);
}

__declspec(dllexport) void dasher_destroy(int64_t handle) {
    auto *session = fromHandle(handle);
    if (session) {
        delete session;
    }
}

__declspec(dllexport) void dasher_set_screen_size(int64_t handle, int width, int height) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->width = std::max(0, width);
    session->height = std::max(0, height);
    session->iface->SetScreenSize(width, height);
}

__declspec(dllexport) void dasher_mouse_move(int64_t handle, float x, float y) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->mouseX = x;
    session->mouseY = y;
    session->iface->SetMousePosition(x, y);
}

__declspec(dllexport) void dasher_mouse_down(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->mouseDown = true;
    session->iface->MouseDown();
}

__declspec(dllexport) void dasher_mouse_up(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->mouseDown = false;
    session->iface->MouseUp();
}

__declspec(dllexport) FrameResult dasher_frame(int64_t handle, int64_t timeMs) {
    FrameResult result = {};
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return result;

    auto rawCommands = session->iface->Frame(static_cast<long>(timeMs));
    if (!hasVisibleBoxCommands(rawCommands)) {
        appendFallbackBoxes(*session, rawCommands);
    }

    result.commandCount = static_cast<int>(rawCommands.size());
    if (result.commandCount > 0) {
        result.commands = new int32_t[result.commandCount];
        std::memcpy(result.commands, rawCommands.data(), rawCommands.size() * sizeof(int32_t));
    }

    auto strings = session->iface->TakeFrameStrings();
    result.stringCount = static_cast<int>(strings.size());
    if (result.stringCount > 0) {
        result.strings = new char *[result.stringCount];
        for (int i = 0; i < result.stringCount; ++i) {
            size_t len = strings[i].size();
            result.strings[i] = new char[len + 1];
            std::memcpy(result.strings[i], strings[i].c_str(), len);
            result.strings[i][len] = '\0';
        }
    }

    return result;
}

__declspec(dllexport) void dasher_free_frame_result(FrameResult *result) {
    if (result) freeFrameResult(*result);
}

__declspec(dllexport) const char *dasher_get_output_text(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return "";
    static thread_local std::string output;
    output = session->iface->GetOutputText();
    return output.c_str();
}

__declspec(dllexport) void dasher_reset_output_text(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->iface->ResetOutputText();
}

__declspec(dllexport) const char *dasher_get_alphabet_id(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return "";
    static thread_local std::string alphabet;
    alphabet = session->iface->GetAlphabetId();
    return alphabet.c_str();
}

__declspec(dllexport) void dasher_set_alphabet_id(int64_t handle, const char *alphabetId) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface || !alphabetId) return;
    session->iface->SetAlphabetId(alphabetId);
}

__declspec(dllexport) int dasher_get_language_model_id(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return 0;
    return session->iface->GetLanguageModelId();
}

__declspec(dllexport) void dasher_set_language_model_id(int64_t handle, int modelId) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->iface->SetLanguageModelId(modelId);
}

__declspec(dllexport) int dasher_get_speed_percent(int64_t handle) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return 100;
    return session->iface->GetMovementSpeedPercent();
}

__declspec(dllexport) void dasher_set_speed_percent(int64_t handle, int percent) {
    auto *session = fromHandle(handle);
    if (!session || !session->iface) return;
    session->iface->SetMovementSpeedPercent(percent);
}

}

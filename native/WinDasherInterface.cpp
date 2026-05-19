#include "WinDasherInterface.h"
#include "WinCommandScreen.h"

#include "DasherCore/DasherInput.h"
#include "DasherCore/DasherScreen.h"
#include "DasherCore/ModuleManager.h"
#include "DasherCore/Parameters.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <utility>
#include <windows.h>

class WinPointerInput : public Dasher::CScreenCoordInput {
public:
    WinPointerInput() : CScreenCoordInput("Windows Mouse Input") {}

    void SetBounds(int width, int height) {
        m_width = std::max(1, width);
        m_height = std::max(1, height);
        if (!m_hasPosition) {
            m_x = m_width / 2;
            m_y = m_height / 2;
        }
    }

    void SetPosition(float x, float y) {
        m_hasPosition = true;
        const int maxX = std::max(0, m_width - 1);
        const int maxY = std::max(0, m_height - 1);
        m_x = std::clamp(static_cast<int>(std::lround(x)), 0, maxX);
        m_y = std::clamp(static_cast<int>(std::lround(y)), 0, maxY);
    }

    bool GetScreenCoords(Dasher::screenint &iX, Dasher::screenint &iY, Dasher::CDasherView *) override {
        iX = static_cast<Dasher::screenint>(m_x);
        iY = static_cast<Dasher::screenint>(m_y);
        return true;
    }

private:
    int m_width = 1;
    int m_height = 1;
    int m_x = 0;
    int m_y = 0;
    bool m_hasPosition = false;
};

static unsigned long nowMs() {
    const auto now = std::chrono::steady_clock::now().time_since_epoch();
    return static_cast<unsigned long>(std::chrono::duration_cast<std::chrono::milliseconds>(now).count());
}

WinSettingsHolder::WinSettingsHolder(const std::string &settingsPath) {
    settings = std::make_unique<Dasher::XmlSettingsStore>(settingsPath, nullptr);
    settings->Load();
}

WinDasherInterface::WinDasherInterface(const std::string &dataDir)
    : WinSettingsHolder(dataDir + "\\dasher_settings.xml")
    , CDashIntfScreenMsgs(WinSettingsHolder::settings.get()) {
    OutputDebugStringA("WinDasherInterface created\n");
}

WinDasherInterface::~WinDasherInterface() = default;

void WinDasherInterface::CallNewFrame(unsigned long timeMs, bool forceRedraw) {
    NewFrame(timeMs, forceRedraw);
}

void WinDasherInterface::CreateModules() {
    CDashIntfScreenMsgs::CreateModules();
    auto input = std::make_unique<WinPointerInput>();
    m_input = input.get();
    GetModuleManager()->RegisterInputDeviceModule(std::move(input), true);
}

void WinDasherInterface::SetScreenSize(int width, int height) {
    if (width <= 0 || height <= 0) return;

    if (!m_screen) {
        m_screen = std::make_unique<WinCommandScreen>(width, height);
        ChangeScreen(m_screen.get());
    } else {
        m_screen->SetSize(width, height);
        ScreenResized(m_screen.get());
    }

    if (!m_realized) {
        Realize(nowMs());
        m_realized = true;
        if (!m_pendingAlphabetId.empty()) {
            const std::string pending = m_pendingAlphabetId;
            m_pendingAlphabetId.clear();
            SetAlphabetId(pending);
        }
    }

    if (m_input) {
        m_input->SetBounds(width, height);
    }
}

void WinDasherInterface::SetMousePosition(float x, float y) {
    if (!m_input) return;
    m_input->SetPosition(x, y);
}

void WinDasherInterface::MouseDown() {
    if (m_mouseDown) return;
    m_mouseDown = true;
    SetBoolParameter(Dasher::BP_START_MOUSE, true);
    KeyDown(nowMs(), Dasher::Keys::Primary_Input);
}

void WinDasherInterface::MouseUp() {
    if (!m_mouseDown) return;
    m_mouseDown = false;
    KeyDown(nowMs(), Dasher::Keys::Primary_Input);
}

std::vector<int32_t> WinDasherInterface::Frame(long timeMs) {
    if (!m_realized || !m_screen) return {};
    m_screen->BeginFrame();
    CallNewFrame(static_cast<unsigned long>(std::max(0L, timeMs)), false);
    return m_screen->TakeCommands();
}

std::vector<std::string> WinDasherInterface::TakeFrameStrings() {
    if (!m_screen) return {};
    return m_screen->TakeStrings();
}

std::string WinDasherInterface::GetAlphabetId() const {
    return GetStringParameter(Dasher::SP_ALPHABET_ID);
}

void WinDasherInterface::SetAlphabetId(const std::string &alphabetId) {
    if (alphabetId.empty()) return;
    m_editBuffer.clear();
    if (!m_realized) {
        m_pendingAlphabetId = alphabetId;
        return;
    }
    if (GetStringParameter(Dasher::SP_ALPHABET_ID) == alphabetId) return;
    if (m_mouseDown) {
        KeyDown(nowMs(), Dasher::Keys::Primary_Input);
        m_mouseDown = false;
    }
    SetStringParameter(Dasher::SP_ALPHABET_ID, alphabetId);
}

int WinDasherInterface::GetLanguageModelId() const {
    return static_cast<int>(GetLongParameter(Dasher::LP_LANGUAGE_MODEL_ID));
}

void WinDasherInterface::SetLanguageModelId(int modelId) {
    const int resolved = (modelId == 0 || modelId == 2 || modelId == 3 || modelId == 4 || modelId == 5) ? modelId : 0;
    if (GetLongParameter(Dasher::LP_LANGUAGE_MODEL_ID) == static_cast<long>(resolved)) return;
    if (m_mouseDown) {
        KeyDown(nowMs(), Dasher::Keys::Primary_Input);
        m_mouseDown = false;
    }
    SetLongParameter(Dasher::LP_LANGUAGE_MODEL_ID, static_cast<long>(resolved));
}

int WinDasherInterface::GetMovementSpeedPercent() const {
    constexpr double kBaseBitrate = 160.0;
    const auto bitrate = static_cast<double>(GetLongParameter(Dasher::LP_MAX_BITRATE));
    const int percent = static_cast<int>(std::lround((bitrate / kBaseBitrate) * 100.0));
    return std::clamp(percent, 20, 400);
}

void WinDasherInterface::SetMovementSpeedPercent(int percent) {
    constexpr double kBaseBitrate = 160.0;
    const int resolvedPercent = std::clamp(percent, 20, 400);
    const long requested = static_cast<long>(std::lround((resolvedPercent / 100.0) * kBaseBitrate));
    const long bitrate = std::max(1L, requested);
    if (GetLongParameter(Dasher::LP_MAX_BITRATE) == bitrate) return;
    SetLongParameter(Dasher::LP_MAX_BITRATE, bitrate);
}

unsigned int WinDasherInterface::ctrlMove(bool, Dasher::EditDistance) {
    return static_cast<unsigned int>(m_editBuffer.size());
}

unsigned int WinDasherInterface::ctrlDelete(bool, Dasher::EditDistance) {
    return static_cast<unsigned int>(m_editBuffer.size());
}

void WinDasherInterface::editOutput(const std::string &strText, Dasher::CDasherNode *pCause) {
    m_editBuffer += strText;
    CDashIntfScreenMsgs::editOutput(strText, pCause);
}

void WinDasherInterface::editDelete(const std::string &strText, Dasher::CDasherNode *pCause) {
    if (!strText.empty() && m_editBuffer.size() >= strText.size()) {
        m_editBuffer.erase(m_editBuffer.size() - strText.size());
    }
    CDashIntfScreenMsgs::editDelete(strText, pCause);
}

std::string WinDasherInterface::GetContext(unsigned int iStart, unsigned int iLength) {
    if (iStart >= m_editBuffer.size()) return {};
    return m_editBuffer.substr(iStart, iLength);
}

std::string WinDasherInterface::GetAllContext() {
    return m_editBuffer;
}

int WinDasherInterface::GetAllContextLenght() {
    return static_cast<int>(m_editBuffer.size());
}

std::string WinDasherInterface::GetOutputText() const {
    return m_editBuffer;
}

void WinDasherInterface::ResetOutputText() {
    m_editBuffer.clear();
}

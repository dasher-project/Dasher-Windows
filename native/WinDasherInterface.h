#pragma once

#include "DasherCore/DashIntfScreenMsgs.h"
#include "DasherCore/XmlSettingsStore.h"

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

class WinCommandScreen;
class WinPointerInput;

struct WinSettingsHolder {
    std::unique_ptr<Dasher::XmlSettingsStore> settings;
    explicit WinSettingsHolder(const std::string &settingsPath);
};

class WinDasherInterface final
    : private WinSettingsHolder
    , public Dasher::CDashIntfScreenMsgs {
public:
    explicit WinDasherInterface(const std::string &dataDir);
    ~WinDasherInterface() override;

    void CallNewFrame(unsigned long timeMs, bool forceRedraw = false);
    void SetScreenSize(int width, int height);
    void SetMousePosition(float x, float y);
    void MouseDown();
    void MouseUp();

    std::vector<int32_t> Frame(long timeMs);
    std::vector<std::string> TakeFrameStrings();

    std::string GetAlphabetId() const;
    void SetAlphabetId(const std::string &alphabetId);

    int GetLanguageModelId() const;
    void SetLanguageModelId(int modelId);

    int GetMovementSpeedPercent() const;
    void SetMovementSpeedPercent(int percent);

    unsigned int ctrlMove(bool bForwards, Dasher::EditDistance dist) override;
    unsigned int ctrlDelete(bool bForwards, Dasher::EditDistance dist) override;
    void editOutput(const std::string &strText, Dasher::CDasherNode *pCause) override;
    void editDelete(const std::string &strText, Dasher::CDasherNode *pCause) override;
    std::string GetContext(unsigned int iStart, unsigned int iLength) override;
    std::string GetAllContext() override;
    int GetAllContextLenght() override;

    std::string GetOutputText() const;
    void ResetOutputText();

protected:
    void CreateModules() override;

private:
    std::string m_editBuffer;
    std::unique_ptr<WinCommandScreen> m_screen;
    WinPointerInput *m_input = nullptr;
    bool m_realized = false;
    bool m_mouseDown = false;
    std::string m_pendingAlphabetId;
};

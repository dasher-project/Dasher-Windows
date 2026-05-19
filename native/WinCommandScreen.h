#pragma once

#include "DasherCore/DasherScreen.h"

#include <cstdint>
#include <string>
#include <vector>

class WinCommandScreen final : public Dasher::CDasherScreen {
public:
    WinCommandScreen(int width, int height);

    void SetSize(int width, int height);
    void BeginFrame();
    std::vector<int32_t> TakeCommands();
    std::vector<std::string> TakeStrings();

    std::pair<Dasher::screenint, Dasher::screenint> TextSize(Label *label, unsigned int iFontSize) override;
    void DrawString(Label *label, Dasher::screenint x, Dasher::screenint y, unsigned int iFontSize, const Dasher::ColorPalette::Color &color) override;
    void DrawRectangle(Dasher::screenint x1, Dasher::screenint y1, Dasher::screenint x2, Dasher::screenint y2, const Dasher::ColorPalette::Color &color, const Dasher::ColorPalette::Color &outlineColor, int iThickness) override;
    void DrawCircle(Dasher::screenint iCX, Dasher::screenint iCY, Dasher::screenint iR, const Dasher::ColorPalette::Color &fillColor, const Dasher::ColorPalette::Color &lineColor, int iLineWidth) override;
    void Polyline(Dasher::point *Points, int Number, int iWidth, const Dasher::ColorPalette::Color &color) override;
    void Polygon(Dasher::point *Points, int Number, const Dasher::ColorPalette::Color &fillColor, const Dasher::ColorPalette::Color &outlineColor, int lineWidth) override;
    void Display() override;
    bool IsPointVisible(Dasher::screenint, Dasher::screenint) override;

private:
    void push(int op, int a, int b, int c, int d, int32_t color);

    std::vector<int32_t> m_commands;
    std::vector<std::string> m_strings;
};

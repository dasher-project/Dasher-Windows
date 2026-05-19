#include "WinCommandScreen.h"

#include <algorithm>
#include <cmath>

static int32_t toArgb(const Dasher::ColorPalette::Color &color) {
    int a = color.Alpha;
    int r = color.Red;
    int g = color.Green;
    int b = color.Blue;

    const bool normalized = (a >= 0 && a <= 1) && (r >= 0 && r <= 1) && (g >= 0 && g <= 1) && (b >= 0 && b <= 1);
    if (normalized) {
        a = static_cast<int>(a * 255);
        r = static_cast<int>(r * 255);
        g = static_cast<int>(g * 255);
        b = static_cast<int>(b * 255);
    }

    a = std::clamp(a, 0, 255);
    r = std::clamp(r, 0, 255);
    g = std::clamp(g, 0, 255);
    b = std::clamp(b, 0, 255);

    if (a > 0 && a < 24) {
        a = 255;
    }

    return static_cast<int32_t>((a << 24) | (r << 16) | (g << 8) | b);
}

static bool isUsableColor(const Dasher::ColorPalette::Color &color) {
    if (color.Red < 0 || color.Green < 0 || color.Blue < 0 || color.Alpha < 0) {
        return false;
    }
    return color.Alpha > 0;
}

WinCommandScreen::WinCommandScreen(int width, int height)
    : CDasherScreen(static_cast<Dasher::screenint>(width), static_cast<Dasher::screenint>(height)) {}

void WinCommandScreen::SetSize(int width, int height) {
    resize(static_cast<Dasher::screenint>(width), static_cast<Dasher::screenint>(height));
}

void WinCommandScreen::BeginFrame() {
    m_commands.clear();
    m_strings.clear();
    push(0, 0, 0, 0, 0, static_cast<int32_t>(0xFF0D1117));
}

std::vector<int32_t> WinCommandScreen::TakeCommands() {
    return std::move(m_commands);
}

std::vector<std::string> WinCommandScreen::TakeStrings() {
    return std::move(m_strings);
}

std::pair<Dasher::screenint, Dasher::screenint> WinCommandScreen::TextSize(Label *label, unsigned int iFontSize) {
    if (!label) return {0, 0};
    const int width = static_cast<int>(label->m_strText.size()) * static_cast<int>(iFontSize) / 2;
    const int height = static_cast<int>(iFontSize);
    return {static_cast<Dasher::screenint>(width), static_cast<Dasher::screenint>(height)};
}

void WinCommandScreen::DrawString(Label *label, Dasher::screenint x, Dasher::screenint y, unsigned int iFontSize, const Dasher::ColorPalette::Color &color) {
    if (!label || label->m_strText.empty() || iFontSize == 0) return;
    const int idx = static_cast<int>(m_strings.size());
    m_strings.push_back(label->m_strText);
    push(5, static_cast<int>(x), static_cast<int>(y), static_cast<int>(iFontSize), idx, toArgb(color));
}

void WinCommandScreen::DrawRectangle(Dasher::screenint x1, Dasher::screenint y1, Dasher::screenint x2, Dasher::screenint y2, const Dasher::ColorPalette::Color &color, const Dasher::ColorPalette::Color &outlineColor, int iThickness) {
    if (isUsableColor(color)) {
        push(4, x1, y1, x2, y2, toArgb(color));
    }
    if (iThickness > 0 && isUsableColor(outlineColor)) {
        push(3, x1, y1, x2, y2, toArgb(outlineColor));
    }
}

void WinCommandScreen::DrawCircle(Dasher::screenint iCX, Dasher::screenint iCY, Dasher::screenint iR, const Dasher::ColorPalette::Color &fillColor, const Dasher::ColorPalette::Color &lineColor, int iLineWidth) {
    if (isUsableColor(fillColor)) {
        push(1, iCX, iCY, iR, 1, toArgb(fillColor));
    }
    if (iLineWidth > 0 && isUsableColor(lineColor)) {
        push(1, iCX, iCY, iR, 0, toArgb(lineColor));
    }
}

void WinCommandScreen::Polyline(Dasher::point *Points, int Number, int, const Dasher::ColorPalette::Color &color) {
    if (!Points || Number < 2 || !isUsableColor(color)) return;
    const int32_t argb = toArgb(color);
    for (int i = 1; i < Number; ++i) {
        push(2, Points[i - 1].x, Points[i - 1].y, Points[i].x, Points[i].y, argb);
    }
}

void WinCommandScreen::Polygon(Dasher::point *Points, int Number, const Dasher::ColorPalette::Color &, const Dasher::ColorPalette::Color &outlineColor, int lineWidth) {
    if (!Points || Number < 2 || lineWidth <= 0 || !isUsableColor(outlineColor)) return;
    const int32_t argb = toArgb(outlineColor);
    for (int i = 1; i < Number; ++i) {
        push(2, Points[i - 1].x, Points[i - 1].y, Points[i].x, Points[i].y, argb);
    }
    push(2, Points[Number - 1].x, Points[Number - 1].y, Points[0].x, Points[0].y, argb);
}

void WinCommandScreen::Display() {}

bool WinCommandScreen::IsPointVisible(Dasher::screenint, Dasher::screenint) {
    return true;
}

void WinCommandScreen::push(int op, int a, int b, int c, int d, int32_t color) {
    m_commands.push_back(op);
    m_commands.push_back(a);
    m_commands.push_back(b);
    m_commands.push_back(c);
    m_commands.push_back(d);
    m_commands.push_back(color);
}

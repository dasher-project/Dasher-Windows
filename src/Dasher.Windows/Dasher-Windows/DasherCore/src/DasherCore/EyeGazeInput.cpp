// Eye Gaze Input Device for DasherCore
//
// This adds eye tracker support to DasherCore's input device system,
// making "Eye Gaze" appear in Preferences → Input → Input Device dropdown
//
// ARCHITECTURE:
// - CEyeGazeInput implements CDasherInput interface like other input devices
// - Stores latest eye gaze coordinates from C# layer
// - Provides coordinates to DasherCore on demand via GetScreenCoords()
// - Thread-safe for real-time eye tracker updates
//
// INTEGRATION:
// - Registered in CAPI.cpp CreateModules()
// - Coordinates set via dasher_set_gaze_position() from C#
// - Appears as "Eye Gaze" in input device dropdown
// - Works with all existing Dasher input filters

#include "DasherCore/DasherInput.h"
#include <mutex>
#include <atomic>

namespace Dasher {

/// <summary>
/// Eye gaze input device that provides real-time coordinates from eye trackers.
/// Similar to PointerInput but optimized for high-frequency eye tracker data.
/// </summary>
class CEyeGazeInput : public CScreenCoordInput {
public:
    CEyeGazeInput() : CScreenCoordInput("Eye Gaze") {}

    /// <summary>
    /// Set the screen bounds for coordinate mapping.
    /// Called when Dasher window is resized.
    /// </summary>
    void SetBounds(int width, int height) {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_width = (width > 0) ? width : 1;
        m_height = (height > 0) ? height : 1;

        // Initialize to center if no position received yet
        if (!m_hasPosition) {
            m_x = m_width / 2;
            m_y = m_height / 2;
        }
    }

    /// <summary>
    /// Update gaze position from eye tracker (called from C# layer).
    /// Thread-safe for high-frequency updates (30-60Hz from eye trackers).
    /// </summary>
    void SetGazePosition(float x, float y) {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_hasPosition = true;
        m_x = static_cast<int>(x);
        m_y = static_cast<int>(y);

        // Clamp to screen bounds
        if (m_x < 0) m_x = 0;
        if (m_x >= m_width) m_x = m_width - 1;
        if (m_y < 0) m_y = 0;
        if (m_y >= m_height) m_y = m_height - 1;
    }

    /// <summary>
    /// Get current gaze position in screen coordinates.
    /// Called by DasherCore during each frame to determine where user is looking.
    /// </summary>
    bool GetScreenCoords(screenint &iX, screenint &iY, CDasherView *) override {
        std::lock_guard<std::mutex> lock(m_mutex);
        iX = static_cast<screenint>(m_x);
        iY = static_cast<screenint>(m_y);
        return m_hasPosition;
    }

    /// <summary>
    /// Check if valid gaze data is available.
    /// </summary>
    bool HasPosition() const {
        return m_hasPosition;
    }

    /// <summary>
    /// Reset position state (e.g., when eye tracker disconnects).
    /// </summary>
    void ClearPosition() {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_hasPosition = false;
        m_x = m_width / 2;
        m_y = m_height / 2;
    }

private:
    mutable std::mutex m_mutex;
    int m_width = 1;
    int m_height = 1;
    int m_x = 0;
    int m_y = 0;
    bool m_hasPosition = false;
};

} // namespace Dasher
// Eye Gaze Input Device for DasherCore
//
// Header file for CEyeGazeInput class
//
// This adds eye tracker support to DasherCore's input device system,
// making "Eye Gaze" appear in Preferences → Input → Input Device dropdown

#pragma once

#include "DasherInput.h"

namespace Dasher {

/// <summary>
/// Eye gaze input device that provides real-time coordinates from eye trackers.
/// Similar to PointerInput but optimized for high-frequency eye tracker data.
/// </summary>
class CEyeGazeInput : public CScreenCoordInput {
public:
    CEyeGazeInput();

    /// <summary>
    /// Set the screen bounds for coordinate mapping.
    /// Called when Dasher window is resized.
    /// </summary>
    void SetBounds(int width, int height);

    /// <summary>
    /// Update gaze position from eye tracker (called from C# layer).
    /// Thread-safe for high-frequency updates (30-60Hz from eye trackers).
    /// </summary>
    void SetGazePosition(float x, float y);

    /// <summary>
    /// Get current gaze position in screen coordinates.
    /// Called by DasherCore during each frame to determine where user is looking.
    /// </summary>
    bool GetScreenCoords(screenint &iX, screenint &iY, CDasherView *) override;

    /// <summary>
    /// Check if valid gaze data is available.
    /// </summary>
    bool HasPosition() const;

    /// <summary>
    /// Reset position state (e.g., when eye tracker disconnects).
    /// </summary>
    void ClearPosition();

private:
    class Impl;
    Impl* pImpl;
};

} // namespace Dasher
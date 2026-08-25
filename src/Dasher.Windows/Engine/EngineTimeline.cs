using System;

namespace Dasher.Windows.Engine;

/// <summary>
/// The monotonic timeline fed to dasher_frame. The engine consumes raw
/// deltas as zoom amount, so scheduler hiccups and pause gaps (settings
/// open) must never reach it as multi-second jumps (#35).
/// </summary>
public static class EngineTimeline
{
    public const long MinStepMs = 1;
    public const long MaxStepMs = 50;

    public static long StepFor(double wallDeltaMs) => (long)Math.Clamp(wallDeltaMs, MinStepMs, MaxStepMs);
}

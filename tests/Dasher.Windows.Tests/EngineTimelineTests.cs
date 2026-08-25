using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

public class EngineTimelineTests
{
    [Theory]
    [InlineData(16.0, 16)]     // normal frame
    [InlineData(16.7, 16)]     // 60 Hz
    [InlineData(0.0, 1)]       // duplicate/degenerate tick -> minimum step
    [InlineData(-5.0, 1)]      // clock rewind -> minimum step
    [InlineData(49.9, 49)]
    public void StepFor_normal_deltas_pass_through_clamped(double delta, long expected)
    {
        Assert.Equal(expected, EngineTimeline.StepFor(delta));
    }

    [Fact]
    public void StepFor_caps_pause_gaps()
    {
        // Settings open for minutes must not arrive as one huge zoom
        // amount (the engine consumes raw deltas) — #35.
        Assert.Equal(EngineTimeline.MaxStepMs, EngineTimeline.StepFor(5000));
        Assert.Equal(EngineTimeline.MaxStepMs, EngineTimeline.StepFor(double.MaxValue));
    }

    [Fact]
    public void StepFor_never_returns_nonPositive()
    {
        Assert.True(EngineTimeline.StepFor(double.MinValue) >= EngineTimeline.MinStepMs);
        Assert.True(EngineTimeline.StepFor(0.4) >= EngineTimeline.MinStepMs);
    }
}

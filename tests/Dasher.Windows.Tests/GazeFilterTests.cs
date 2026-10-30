using Dasher.Windows.EyeGaze;

namespace Dasher.Windows.Tests;

/// <summary>
/// GazeFilter (dead zone + velocity gate + settle window) — the transit-zoom
/// fix from the eyegaze diagnostic work (Juan's report; validated against the
/// eyegaze.log field session). Pure class, deterministic timestamps.
/// </summary>
public class GazeFilterTests
{
    private static long T(int ms) => 1_000_000 + ms; // arbitrary epoch offset

    [Fact]
    public void First_sample_with_no_settle_sent_immediately()
    {
        var f = new GazeFilter { DeadZonePx = 0, MaxVelocityPxPerMs = 0, SettleMs = 0 };
        Assert.True(f.Process(100, 100, T(0), out var x, out var y));
        Assert.Equal(100, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void Dead_zone_blocks_jitter_around_last_sent()
    {
        var f = new GazeFilter { DeadZonePx = 10, MaxVelocityPxPerMs = 0, SettleMs = 0 };
        Assert.True(f.Process(100, 100, T(0), out _, out _)); // sent at (100,100)
        Assert.False(f.Process(105, 103, T(16), out _, out _)); // 5.8px < 10px: jitter
        Assert.False(f.Process(108, 104, T(32), out _, out _)); // still inside
        Assert.Equal(1, f.SentSamples);
        Assert.Equal(2, f.DeadZoneBlocks);
    }

    [Fact]
    public void Movement_beyond_dead_zone_passes()
    {
        var f = new GazeFilter { DeadZonePx = 10, MaxVelocityPxPerMs = 0, SettleMs = 0 };
        Assert.True(f.Process(100, 100, T(0), out _, out _));
        Assert.True(f.Process(130, 100, T(16), out var x, out _)); // 30px jump
        Assert.Equal(130, x);
    }

    [Fact]
    public void Velocity_gate_holds_position_during_saccade_transit()
    {
        var f = new GazeFilter { DeadZonePx = 0, MaxVelocityPxPerMs = 2.0, SettleMs = 0 };
        Assert.True(f.Process(100, 100, T(0), out _, out _));
        // 100px in 16ms = 6.25 px/ms — well over the gate. The smoothed
        // velocity (0.7*0 + 0.3*6.25 = 1.9) is still under on the FIRST
        // transit sample, then crosses it.
        Assert.True(f.Process(200, 100, T(16), out _, out _)); // smoothing lags
        Assert.False(f.Process(300, 100, T(32), out _, out _)); // gate active
        Assert.False(f.Process(400, 100, T(48), out _, out _)); // still in transit
        // Arrive: dwell at the target. The smoothed velocity decays
        // exponentially (alpha 0.3) — needs a few stationary samples to
        // cross back under the gate.
        var sent = false;
        for (var t = 100; t <= 300 && !sent; t += 16)
            sent = f.Process(401, 100, T(t), out _, out _);
        Assert.True(sent);
        Assert.True(f.VelocityBlocks >= 1);
    }

    [Fact]
    public void Settle_window_requires_dwell_before_sending()
    {
        var f = new GazeFilter { DeadZonePx = 8, MaxVelocityPxPerMs = 0, SettleMs = 80 };
        // First sample becomes the settle CANDIDATE — nothing is sent until
        // the gaze dwells on it for SettleMs.
        Assert.False(f.Process(100, 100, T(0), out _, out _));
        Assert.False(f.Process(102, 101, T(40), out _, out _)); // dwelling, 40ms < 80ms
        // 90ms dwell on the same candidate: sent — at the CANDIDATE
        // position where the dwell began, not the latest sample.
        Assert.True(f.Process(103, 101, T(90), out var x, out var y));
        Assert.Equal(100, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void Settle_candidate_resets_when_gaze_moves_on()
    {
        var f = new GazeFilter { DeadZonePx = 8, MaxVelocityPxPerMs = 0, SettleMs = 80 };
        f.Process(100, 100, T(0), out _, out _); // candidate (100,100)
        f.Process(250, 100, T(40), out _, out _); // moved way off: NEW candidate
        // 60ms into the new candidate's dwell — still not settled
        Assert.False(f.Process(251, 100, T(100), out _, out _));
        // ...and the original (100,100) never got sent at 90ms (it was
        // cancelled when the gaze moved).
        Assert.Equal(0, f.SentSamples);
    }

    [Fact]
    public void Reset_clears_state_for_a_fresh_session()
    {
        var f = new GazeFilter { DeadZonePx = 0, MaxVelocityPxPerMs = 0, SettleMs = 0 };
        Assert.True(f.Process(100, 100, T(0), out _, out _));
        Assert.Equal(1, f.SentSamples);
        f.Reset();
        Assert.Equal(0, f.SentSamples);
        Assert.Equal(0, f.TotalSamples);
        // After reset the filter behaves as new: first sample sends (no
        // dead-zone reference, no velocity baseline).
        Assert.True(f.Process(500, 500, T(1000), out _, out _));
    }

    [Fact]
    public void First_sample_is_not_measured_against_the_origin()
    {
        // Regression (found while writing these tests): the first sample
        // after construction/Reset computed velocity from the ORIGIN (0,0) —
        // a phantom ~8.8 px/ms saccade for a first gaze at (100,100) that
        // velocity-blocked the first real positions after every gaze-loss
        // recovery. With the gate ON and settle off, the first sample must
        // send immediately.
        var f = new GazeFilter { DeadZonePx = 0, MaxVelocityPxPerMs = 2.0, SettleMs = 0 };
        Assert.True(f.Process(100, 100, T(0), out _, out _));
        Assert.Equal(0, f.LastVelocity, precision: 6);
        // And after Reset (every gaze-loss recovery):
        f.Reset();
        Assert.True(f.Process(400, 300, T(1000), out _, out _));
    }

    [Fact]
    public void Zero_dead_zone_with_settle_still_completes_the_dwell()
    {
        // Regression (review finding): the settle candidate check reused
        // DeadZonePx as its tolerance — with the dead zone set to 0 (the
        // settings slider's minimum), 1px jitter restarted the dwell timer
        // forever and NOTHING was ever sent.
        var f = new GazeFilter { DeadZonePx = 0, MaxVelocityPxPerMs = 0, SettleMs = 50 };
        Assert.False(f.Process(100, 100, T(0), out _, out _)); // candidate
        Assert.False(f.Process(101, 100, T(20), out _, out _)); // 1px jitter
        Assert.False(f.Process(100, 101, T(40), out _, out _)); // more jitter
        Assert.True(f.Process(101, 100, T(60), out _, out _)); // dwell completed despite jitter
    }

    [Fact]
    public void Velocity_gate_cancels_pending_settle()
    {
        // Dwelling at a candidate completes and sends; a saccade after a NEW
        // candidate begins must cancel the dwell — nothing sent mid-transit.
        var f = new GazeFilter { DeadZonePx = 8, MaxVelocityPxPerMs = 1.0, SettleMs = 50 };
        Assert.False(f.Process(100, 100, T(0), out _, out _)); // candidate starts
        Assert.True(f.Process(100, 100, T(60), out _, out _)); // dwell completed: sent
        Assert.Equal(1, f.SentSamples);
        // New target: candidate starts, then transit begins before dwell
        f.Process(400, 100, T(80), out _, out _); // fast move: gate or new candidate
        var sentBefore = f.SentSamples;
        Assert.False(f.Process(500, 100, T(96), out _, out _)); // transit: held
        Assert.Equal(sentBefore, f.SentSamples);
    }
}

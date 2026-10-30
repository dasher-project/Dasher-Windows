using System;
using System.Collections.Generic;

namespace Dasher.Windows.EyeGaze;

/// <summary>
/// Filters raw gaze data before it reaches the engine's zoom. Solves the
/// "transit zoom" problem reported by eye-tracker users (Juan, Argentina,
/// Tobii PCEye + TD Control): when the user looks at a letter, the tracker
/// takes milliseconds to move its cursor there, and every intermediate
/// position was sent to dasher_mouse_move — zooming the canvas toward the
/// wrong places during transit.
///
/// Three filters, in order:
///
/// 1. DEAD ZONE: if the gaze hasn't moved more than N screen px from the
///    last SENT position, don't send — reduces jitter noise.
///
/// 2. VELOCITY GATE: if the gaze is moving faster than N px/ms, it's in
///    transit (saccade) — hold the last sent position until velocity drops.
///
/// 3. SETTLE WINDOW: the gaze must stay within the dead zone for N ms
///    before the position is sent — ensures the user has ARRIVED at their
///    target, not just passing through.
///
/// All thresholds configurable for experimentation; 0 disables each filter.
/// </summary>
public sealed class GazeFilter
{
    // ── Configuration (all tunable; 0 = disabled) ─────────────────────────

    /// <summary>Minimum px movement before sending (0 = always send). Default 8.</summary>
    public double DeadZonePx { get; set; } = 8;

    /// <summary>Maximum velocity px/ms before positions are held (0 = disabled). Default 3.0.</summary>
    public double MaxVelocityPxPerMs { get; set; } = 3.0;

    /// <summary>Ms the gaze must dwell before sending (0 = send immediately). Default 80.</summary>
    public double SettleMs { get; set; } = 80;

    // ── State ───────────────────────────────────────────────────────────────

    private double _lastSentX, _lastSentY;
    private double _candidateX, _candidateY;
    private long _candidateStartMs;
    private bool _hasCandidate;
    private long _lastSampleMs;
    private double _lastSampleX, _lastSampleY;
    private double _smoothedVelocity;
    private bool _hasSent;

    // Diagnostics (for the eyegaze log)
    public int TotalSamples { get; private set; }
    public int SentSamples { get; private set; }
    public int DeadZoneBlocks { get; private set; }
    public int VelocityBlocks { get; private set; }
    public int SettleBlocks { get; private set; }
    public double LastVelocity { get; private set; }

    /// <summary>
    /// Process a raw gaze sample. Returns true when the position should be
    /// sent to the engine (passed all filters), with the filtered position
    /// in the out parameters.
    /// </summary>
    public bool Process(double x, double y, long timestampMs, out double outX, out double outY)
    {
        outX = x; outY = y;
        TotalSamples++;

        // Compute instantaneous velocity (px per ms). The FIRST sample after
        // construction/Reset must not be measured against the origin (0,0) —
        // that phantom "saccade" (e.g. 8.8 px/ms for a first gaze at
        // (100,100)) blocked the first real positions after every gaze-loss
        // recovery; found by GazeFilterTests while pinning the semantics.
        double velocity = 0;
        if (_lastSampleMs != 0)
        {
            var dtMs = Math.Max(1, timestampMs - _lastSampleMs);
            var dx = x - _lastSampleX;
            var dy = y - _lastSampleY;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            velocity = distance / dtMs;
            // Exponential smoothing (alpha=0.3) — single-sample spikes don't
            // trigger the gate; sustained transit does
            _smoothedVelocity = _smoothedVelocity * 0.7 + velocity * 0.3;
        }
        else
        {
            _smoothedVelocity = 0;
        }
        LastVelocity = _smoothedVelocity;
        _lastSampleMs = timestampMs;
        _lastSampleX = x;
        _lastSampleY = y;

        // FILTER 1: velocity gate — in transit, hold position
        if (MaxVelocityPxPerMs > 0 && _smoothedVelocity > MaxVelocityPxPerMs)
        {
            VelocityBlocks++;
            _hasCandidate = false; // cancel any pending settle
            return false;
        }

        // FILTER 2: dead zone — too close to last sent position
        if (_hasSent && DeadZonePx > 0)
        {
            var sentDx = x - _lastSentX;
            var sentDy = y - _lastSentY;
            if (Math.Sqrt(sentDx * sentDx + sentDy * sentDy) < DeadZonePx)
            {
                DeadZoneBlocks++;
                _hasCandidate = false; // within dead zone — reset settle
                return false;
            }
        }

        // FILTER 3: settle window — must dwell for N ms
        if (SettleMs <= 0)
        {
            // No settle required — send immediately
            Send(x, y, ref outX, ref outY);
            return true;
        }

        if (!_hasCandidate || Math.Abs(x - _candidateX) > DeadZonePx || Math.Abs(y - _candidateY) > DeadZonePx)
        {
            // New candidate position — start the settle timer
            _candidateX = x;
            _candidateY = y;
            _candidateStartMs = timestampMs;
            _hasCandidate = true;
            SettleBlocks++;
            return false;
        }

        // Same candidate — check if settled long enough
        if (timestampMs - _candidateStartMs >= SettleMs)
        {
            Send(_candidateX, _candidateY, ref outX, ref outY);
            _hasCandidate = false;
            return true;
        }

        SettleBlocks++;
        return false;
    }

    private void Send(double x, double y, ref double outX, ref double outY)
    {
        _lastSentX = x;
        _lastSentY = y;
        _hasSent = true;
        SentSamples++;
        outX = x;
        outY = y;
    }

    /// <summary>Reset all state (mode switch, re-calibration).</summary>
    public void Reset()
    {
        _hasCandidate = false;
        _hasSent = false;
        _lastSampleMs = 0;
        _smoothedVelocity = 0;
        TotalSamples = SentSamples = DeadZoneBlocks = VelocityBlocks = SettleBlocks = 0;
    }
}

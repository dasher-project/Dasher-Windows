using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dasher.Windows.Engine;
using Dasher.Windows.EyeGaze;

namespace Dasher.Windows.Controls
{
    public partial class DasherCanvas
    {
        private volatile bool _eyeTrackActive;
        private long _lastGazeTicks;
        private static readonly TimeSpan GazeTimeout = TimeSpan.FromMilliseconds(500);

        public bool IsEyeTracking => _useEyeGazeInput;
        public bool IsEyeTrackActive => _eyeTrackActive;

        public async Task<bool> InitializeEyeGazeAsync(EyeGazeIntegration.TrackerType trackerType, int udpPort = 5555)
        {
            EyeGazeLogger.Log($"=== InitializeEyeGazeAsync: {trackerType} (UDP port {udpPort}) ===");

            // Shut down any existing eye gaze integration first (prevents double-init conflict)
            if (_useEyeGazeInput)
            {
                EyeGazeLogger.Log("InitializeEyeGazeAsync: shutting down existing eye gaze first");
                DisableEyeGaze();
            }

            var settings = new EyeGazeIntegration.Settings
            {
                Type = trackerType,
                UdpPort = udpPort
            };

            _eyeGazeIntegration = new EyeGazeIntegration();
            var ok = await _eyeGazeIntegration.InitializeAsync(settings);

            EyeGazeLogger.Log($"InitializeEyeGazeAsync result: {ok}");

            if (ok)
            {
                _useEyeGazeInput = true;
                _eyeGazeIntegration.GazePositionChanged += OnEyeGazePositionChanged;
                _eyeGazeIntegration.Warning += OnEyeTrackerWarning;
                _gazePausedForLoss = false;
                _lastGazeTicks = 0;

                // Eye gaze NEEDS stop-outside semantics: when tracking is
                // lost (blink, dropout, tracker losing the eyes) the last
                // gaze position must stop driving the zoom. Without it,
                // every invalid gap is a runaway zoom at the last spot —
                // the eyegaze.log field session showed a 100-minute
                // 0%-valid stretch where nothing told the engine to stop.
                var stopKey = NativeBridge.dasher_find_parameter_key("BP_STOP_OUTSIDE");
                if (stopKey >= 0 && NativeBridge.dasher_get_bool_parameter(_handle, stopKey) == 0)
                {
                    NativeBridge.dasher_set_bool_parameter(_handle, stopKey, 1);
                    EyeGazeLogger.Log("Eye gaze: enabled 'Stop When Pointer Leaves' (required for gaze-loss pausing)");
                }

                EyeGazeLogger.Log("Eye gaze input active — indicator should appear (green/red dot top-right)");
            }
            return ok;
        }

        /// <summary>Tracker-side degradation notice (thread-pool thread):
        /// log + one toast per session, marshalled to the UI thread.</summary>
        private bool _gazeWarned;
        private void OnEyeTrackerWarning(string message)
        {
            EyeGazeLogger.Log($"Tracker warning: {message}");
            if (_gazeWarned) return;
            _gazeWarned = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Services.ToastNotifier.Show("Eye tracker notice", message, isWarning: true));
        }

        public void DisableEyeGaze()
        {
            DisableEyeGaze(blocking: true);
        }

        public void DisableEyeGazeNonBlocking()
        {
            DisableEyeGaze(blocking: false);
        }

        private void DisableEyeGaze(bool blocking)
        {
            _useEyeGazeInput = false;
            _eyeTrackActive = false;
            _gazePausedForLoss = false;
            _gazeWarned = false;
            if (_eyeGazeIntegration != null)
            {
                _eyeGazeIntegration.GazePositionChanged -= OnEyeGazePositionChanged;
                _eyeGazeIntegration.Warning -= OnEyeTrackerWarning;
                _eyeGazeIntegration.Shutdown(blocking);
                _eyeGazeIntegration = null;
            }
        }

        // Cached on the UI thread (Render) so the gaze callback (background thread)
        // can convert screen pixels to canvas-local DIPs without calling Avalonia APIs.
        private float _cachedOriginX;
        private float _cachedOriginY;

        private GazeFilter? _gazeFilter;
        private long _gazeLogStartMs;
        private int _gazeLogInterval; // log every Nth sample (throttles the log)

        /// <summary>
        /// Configure the gaze filter (dead zone, velocity gate, settle window).
        /// Null disables all filtering (raw passthrough — pre-Juan behaviour).
        /// </summary>
        public void ConfigureGazeFilter(double? deadZonePx = null, double? maxVelocity = null, double? settleMs = null)
        {
            if (deadZonePx == null && maxVelocity == null && settleMs == null)
            {
                _gazeFilter = null; // disable
                EyeGazeLogger.Log("Gaze filter DISABLED (raw passthrough)");
                return;
            }

            _gazeFilter ??= new GazeFilter();
            if (deadZonePx.HasValue) _gazeFilter.DeadZonePx = deadZonePx.Value;
            if (maxVelocity.HasValue) _gazeFilter.MaxVelocityPxPerMs = maxVelocity.Value;
            if (settleMs.HasValue) _gazeFilter.SettleMs = settleMs.Value;
            EyeGazeLogger.Log($"Gaze filter: deadZone={_gazeFilter.DeadZonePx}px vel={_gazeFilter.MaxVelocityPxPerMs}px/ms settle={_gazeFilter.SettleMs}ms");
        }

        private void OnEyeGazePositionChanged(object? sender, GazePoint gazePoint)
        {
            if (!_useEyeGazeInput || _handle == IntPtr.Zero) return;

            if (!gazePoint.IsValid)
            {
                // Tracker says eyes not found: pause immediately (the same
                // stop-outside idiom as the pointer leaving the canvas).
                // Dropping invalid samples silently left the last gaze
                // position driving the zoom (the eyegaze.log field session).
                PauseForGazeLoss("invalid sample");
                return;
            }

            _lastGazeTicks = DateTimeOffset.UtcNow.Ticks;
            _eyeTrackActive = true;
            if (_gazePausedForLoss)
            {
                _gazePausedForLoss = false;
                EyeGazeLogger.Log("Gaze resumed — zoom continues");
            }

            float x = gazePoint.X;
            float y = gazePoint.Y;

            if (gazePoint.IsScreenCoordinates)
            {
                x = (float)(gazePoint.X - _cachedOriginX);
                y = (float)(gazePoint.Y - _cachedOriginY);
            }

            // Apply the gaze filter (dead zone → velocity gate → settle)
            if (_gazeFilter != null)
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!_gazeFilter.Process(x, y, nowMs, out var filteredX, out var filteredY))
                    return; // filtered out (in transit, jitter, or not settled)

                // Diagnostic: log every 30th sample (≈once per 500ms at 60Hz)
                _gazeLogInterval++;
                if (_gazeLogInterval >= 30)
                {
                    _gazeLogInterval = 0;
                    var f = _gazeFilter;
                    EyeGazeLogger.Log($"gaze filter: sent=({filteredX:F0},{filteredY:F0}) vel={f.LastVelocity:F1}px/ms " +
                        $"[total={f.TotalSamples} sent={f.SentSamples} dz={f.DeadZoneBlocks} vel_blk={f.VelocityBlocks} settle_blk={f.SettleBlocks}]");
                }

                x = (float)filteredX;
                y = (float)filteredY;
            }

            NativeBridge.dasher_mouse_move(_handle, x, y);
        }

        /// <summary>True while the engine is paused because gaze was lost;
        /// the next valid sample clears it.</summary>
        private volatile bool _gazePausedForLoss;

        /// <summary>
        /// Pause the engine because gaze was lost: send the off-canvas
        /// position (the engine's stop-outside idiom — enabled at gaze init),
        /// flag the indicator red, and reset the gaze filter so stale
        /// transit state doesn't fire the moment gaze returns. Called from
        /// the gaze callback thread (invalid samples) and the UI thread
        /// (staleness backstop in Render).
        /// </summary>
        private void PauseForGazeLoss(string reason)
        {
            _eyeTrackActive = false;
            _gazeFilter?.Reset();
            if (_gazePausedForLoss) return; // already paused
            _gazePausedForLoss = true;
            EyeGazeLogger.Log($"Gaze lost ({reason}) — pausing zoom");
            try
            {
                NativeBridge.dasher_mouse_move(_handle, -10000f, -10000f);
            }
            catch (Exception ex)
            {
                EyeGazeLogger.Log($"PauseForGazeLoss: mouse_move failed: {ex.Message}");
            }
        }

        /// <summary>Staleness backstop: called from Render (UI thread, RAF
        /// cadence) — if no valid sample arrived within GazeTimeout (e.g.
        /// the tracker stopped delivering entirely), pause. The indicator's
        /// red dot rides the same check.</summary>
        private void CheckGazeStaleness()
        {
            if (!_useEyeGazeInput) return;
            if (_lastGazeTicks == 0 || DateTimeOffset.UtcNow - new DateTimeOffset(_lastGazeTicks, TimeSpan.Zero) <= GazeTimeout)
                return;
            PauseForGazeLoss("no gaze data for " + GazeTimeout.TotalMilliseconds + "ms");
        }

        private void DrawEyeTrackIndicator(DrawingContext context)
        {
            if (!_useEyeGazeInput) return;

            // Check for stale data — no gaze received in 500ms = lost tracking
            var lastTime = new DateTimeOffset(_lastGazeTicks, TimeSpan.Zero);
            if (_lastGazeTicks == 0 || DateTimeOffset.UtcNow - lastTime > GazeTimeout)
                _eyeTrackActive = false;

            // Draw in top-right corner: green dot (tracking) or red dot (lost)
            var dotSize = 10.0;
            var margin = 12.0;
            var x = Bounds.Width - dotSize - margin;
            var y = margin;

            var color = _eyeTrackActive
                ? Color.FromRgb(80, 200, 80)   // green
                : Color.FromRgb(200, 60, 60)   // red
                ;

            context.DrawEllipse(
                new SolidColorBrush(color),
                null,
                new Point(x + dotSize / 2, y + dotSize / 2),
                dotSize / 2, dotSize / 2);
        }
    }
}


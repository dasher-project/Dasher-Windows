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
                EyeGazeLogger.Log("Eye gaze input active — indicator should appear (green/red dot top-right)");
            }
            return ok;
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
            if (_eyeGazeIntegration != null)
            {
                _eyeGazeIntegration.GazePositionChanged -= OnEyeGazePositionChanged;
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

            _lastGazeTicks = DateTimeOffset.UtcNow.Ticks;
            _eyeTrackActive = true;

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
                : Color.FromRgb(200, 60, 60);  // red

            context.DrawEllipse(
                new SolidColorBrush(color),
                null,
                new Point(x + dotSize / 2, y + dotSize / 2),
                dotSize / 2, dotSize / 2);
        }
    }
}


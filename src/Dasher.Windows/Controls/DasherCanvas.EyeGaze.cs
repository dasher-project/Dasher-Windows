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
            var settings = new EyeGazeIntegration.Settings
            {
                Type = trackerType,
                UdpPort = udpPort
            };

            _eyeGazeIntegration = new EyeGazeIntegration();
            var ok = await _eyeGazeIntegration.InitializeAsync(settings);

            if (ok)
            {
                _useEyeGazeInput = true;
                _eyeGazeIntegration.GazePositionChanged += OnEyeGazePositionChanged;
            }
            return ok;
        }

        public void DisableEyeGaze()
        {
            _useEyeGazeInput = false;
            _eyeTrackActive = false;
            if (_eyeGazeIntegration != null)
            {
                _eyeGazeIntegration.GazePositionChanged -= OnEyeGazePositionChanged;
                _eyeGazeIntegration.Shutdown();
                _eyeGazeIntegration = null;
            }
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
                var screenOriginPx = Avalonia.VisualExtensions.PointToScreen(this, new Point(0, 0));
                var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                var originDips = new Point(screenOriginPx.X / scaling, screenOriginPx.Y / scaling);
                x = (float)(gazePoint.X - originDips.X);
                y = (float)(gazePoint.Y - originDips.Y);
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


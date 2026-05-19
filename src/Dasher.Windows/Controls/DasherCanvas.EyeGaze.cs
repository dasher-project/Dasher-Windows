using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Dasher.Windows.Engine;
using Dasher.Windows.EyeGaze;

namespace Dasher.Windows.Controls
{
    public partial class DasherCanvas
    {
        public async Task InitializeEyeGazeAsync(EyeGazeIntegration.TrackerType trackerType, int udpPort = 5555)
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
        }

        public void DisableEyeGaze()
        {
            _useEyeGazeInput = false;
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
    }
}

using System;
using System.Threading.Tasks;
using Dasher.Windows.EyeGaze;

namespace Dasher.Windows.Engine
{
    public class EyeGazeIntegration
    {
        private EyeGazeInputDevice? _device;

        public enum TrackerType
        {
            None,
            WindowsNative,
            UdpGazeTracker,
            Custom
        }

        public class Settings
        {
            public TrackerType Type { get; set; } = TrackerType.None;
            public int UdpPort { get; set; } = 5555;
            public IEyeTrackerService? CustomTracker { get; set; }
        }

        public event EventHandler<GazePoint>? GazePositionChanged;
        public bool IsActive => _device?.IsEnabled ?? false;

        public async Task<bool> InitializeAsync(Settings settings)
        {
            try
            {
                var tracker = CreateTracker(settings.Type, settings);
                if (tracker == null) return false;

                _device = new EyeGazeInputDevice();
                var ok = await _device.InitializeAsync(tracker);
                if (ok)
                {
                    _device.GazePositionChanged += OnGaze;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Eye gaze init failed: {ex.Message}");
                return false;
            }
        }

        public void Shutdown()
        {
            if (_device != null)
            {
                _device.GazePositionChanged -= OnGaze;
                _device.Shutdown();
                _device = null;
            }
        }

        private void OnGaze(object? sender, GazePoint p)
        {
            GazePositionChanged?.Invoke(this, p);
        }

        private static IEyeTrackerService? CreateTracker(TrackerType type, Settings s)
        {
            return type switch
            {
                TrackerType.WindowsNative => new WindowsGazeTracker(),
                TrackerType.UdpGazeTracker => new UdpGazeTracker { UdpPort = s.UdpPort },
                TrackerType.Custom => s.CustomTracker,
                _ => null
            };
        }
    }
}

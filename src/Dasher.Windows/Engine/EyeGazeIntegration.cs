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
            Eyetuitive,
            TobiiStreamEngine,
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
                EyeGazeLogger.Log($"InitializeAsync: tracker type = {settings.Type}");

                var tracker = CreateTracker(settings.Type, settings);
                if (tracker == null)
                {
                    EyeGazeLogger.Log("InitializeAsync: CreateTracker returned null");
                    return false;
                }

                _device = new EyeGazeInputDevice();
                var ok = await _device.InitializeAsync(tracker);
                if (ok)
                {
                    _device.GazePositionChanged += OnGaze;
                    EyeGazeLogger.Log("InitializeAsync: success — eye gaze active");
                    return true;
                }
                EyeGazeLogger.Log("InitializeAsync: device initialization failed");
                return false;
            }
            catch (Exception ex)
            {
                EyeGazeLogger.Log($"InitializeAsync exception: {ex}");
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
                TrackerType.Eyetuitive => new EyetuitiveTracker(),
                TrackerType.TobiiStreamEngine => new TobiiStreamEngineTracker(),
                TrackerType.Custom => s.CustomTracker,
                _ => null
            };
        }
    }
}

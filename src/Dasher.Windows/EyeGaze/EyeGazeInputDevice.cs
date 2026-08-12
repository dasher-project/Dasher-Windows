using System;
using System.Threading.Tasks;

namespace Dasher.Windows.EyeGaze
{
    public class EyeGazeInputDevice
    {
        private IEyeTrackerService? _tracker;
        private GazePoint? _lastPosition;
        private readonly object _lock = new();

        public event EventHandler<GazePoint>? GazePositionChanged;
        public bool IsEnabled { get; private set; }

        public async Task<bool> InitializeAsync(IEyeTrackerService tracker)
        {
            try
            {
                _tracker = tracker;
                EyeGazeLogger.Log($"EyeGazeInputDevice: connecting to {_tracker.GetType().Name}...");
                var connected = await tracker.ConnectAsync();
                if (!connected)
                {
                    EyeGazeLogger.Log("EyeGazeInputDevice: ConnectAsync returned false");
                    return false;
                }
                EyeGazeLogger.Log("EyeGazeInputDevice: connected, starting tracking");
                tracker.GazeDataReceived += OnGazeData;
                tracker.StartTracking();
                IsEnabled = true;
                EyeGazeLogger.Log("EyeGazeInputDevice: tracking started");
                return true;
            }
            catch (Exception ex)
            {
                EyeGazeLogger.Log($"EyeGazeInputDevice init exception: {ex}");
                return false;
            }
        }

        public GazePoint? LastPosition
        {
            get { lock (_lock) { return _lastPosition; } }
        }

        public void Shutdown() => Shutdown(blocking: true);

        public void Shutdown(bool blocking)
        {
            IsEnabled = false;
            if (_tracker != null)
            {
                _tracker.GazeDataReceived -= OnGazeData;
                if (_tracker is TobiiStreamEngineTracker tobii)
                    tobii.Dispose(blocking);
                else
                    _tracker.Dispose();
                _tracker = null;
            }
        }

        private void OnGazeData(object? sender, GazePoint point)
        {
            if (!IsEnabled || !point.IsValid) return;
            lock (_lock) { _lastPosition = point; }
            GazePositionChanged?.Invoke(this, point);
        }
    }
}

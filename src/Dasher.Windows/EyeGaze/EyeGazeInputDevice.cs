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
                var connected = await tracker.ConnectAsync();
                if (connected)
                {
                    tracker.GazeDataReceived += OnGazeData;
                    tracker.StartTracking();
                    IsEnabled = true;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Eye tracker init failed: {ex.Message}");
                return false;
            }
        }

        public GazePoint? LastPosition
        {
            get { lock (_lock) { return _lastPosition; } }
        }

        public void Shutdown()
        {
            IsEnabled = false;
            if (_tracker != null)
            {
                _tracker.GazeDataReceived -= OnGazeData;
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

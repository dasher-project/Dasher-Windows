using System;
using System.Threading.Tasks;
using Windows.Devices.Input.Preview;

namespace Dasher.Windows.EyeGaze
{
    public sealed class WindowsGazeTracker : IEyeTrackerService
    {
        private GazeInputSourcePreview? _source;
        private bool _isTracking;

        public event EventHandler<GazePoint>? GazeDataReceived;
        public string TrackerName => "Windows Eye Tracker";
        public bool IsConnected { get; private set; }

        public Task<bool> ConnectAsync()
        {
            try
            {
                _source = GazeInputSourcePreview.GetForCurrentView();
                if (_source == null) return Task.FromResult(false);

                IsConnected = true;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows gaze connect failed: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public void StartTracking()
        {
            if (_source == null || _isTracking) return;
            _source.GazeMoved += OnGazeMoved;
            _isTracking = true;
        }

        public void StopTracking()
        {
            if (_source == null || !_isTracking) return;
            _source.GazeMoved -= OnGazeMoved;
            _isTracking = false;
        }

        public void Dispose()
        {
            StopTracking();
            _source = null;
            IsConnected = false;
        }

        private void OnGazeMoved(GazeInputSourcePreview sender, GazeMovedPreviewEventArgs args)
        {
            if (!_isTracking) return;

            var point = args.CurrentPoint;
            var pos = point.EyeGazePosition;
            if (pos == null) return;

            var coords = pos.Value;
            GazeDataReceived?.Invoke(this, new GazePoint(
                (float)coords.X, (float)coords.Y,
                DateTimeOffset.UtcNow, true, isScreenCoordinates: true));
        }
    }
}

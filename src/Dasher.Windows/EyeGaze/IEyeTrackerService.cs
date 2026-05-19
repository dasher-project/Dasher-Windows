using System;
using System.Threading.Tasks;

namespace Dasher.Windows.EyeGaze
{
    public interface IEyeTrackerService : IDisposable
    {
        event EventHandler<GazePoint> GazeDataReceived;
        string TrackerName { get; }
        bool IsConnected { get; }
        Task<bool> ConnectAsync();
        void StartTracking();
        void StopTracking();
    }

    public class GazePoint
    {
        public float X { get; }
        public float Y { get; }
        public DateTimeOffset Timestamp { get; }
        public bool IsValid { get; }
        public bool IsScreenCoordinates { get; }

        public GazePoint(float x, float y, DateTimeOffset timestamp, bool isValid = true, bool isScreenCoordinates = false)
        {
            X = x;
            Y = y;
            Timestamp = timestamp;
            IsValid = isValid;
            IsScreenCoordinates = isScreenCoordinates;
        }
    }
}

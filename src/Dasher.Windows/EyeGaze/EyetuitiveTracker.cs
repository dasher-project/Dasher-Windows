using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using GazeFirst;

namespace Dasher.Windows.EyeGaze;

/// <summary>
/// Eye tracker driver for eyetuitive devices (GazeFirst).
/// Connects via gRPC to the eyetuitive hardware.
/// Uses raw (unfiltered) gaze data — Dasher's inference engine handles noise.
/// </summary>
public sealed class EyetuitiveTracker : IEyeTrackerService
{
    private GazeFirst.eyetuitive? _device;
    private bool _disposed;

    public event EventHandler<GazePoint>? GazeDataReceived;
    public string TrackerName => "eyetuitive";
    public bool IsConnected { get; private set; }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            _device = new GazeFirst.eyetuitive();
            IsConnected = await _device.ConnectAsync(timeoutInSeconds: 10);
            return IsConnected;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"eyetuitive connect failed: {ex.Message}");
            IsConnected = false;
            return false;
        }
    }

    public void StartTracking()
    {
        if (_device == null || !IsConnected) return;
        // filtered: false → raw gaze data. Dasher handles noise better than
        // hardware smoothing because its zooming model averages over time.
        _device.Gaze.StartGazeTracking(OnGazeData, filtered: false);
    }

    public void StopTracking()
    {
        if (_device == null || !IsConnected) return;
        _device.Gaze.StopGazeTracking(OnGazeData);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTracking();
        _device?.Dispose();
        _device = null;
        IsConnected = false;
    }

    /// <summary>
    /// Check if an eyetuitive device is connected via USB (Windows only).
    /// </summary>
    public static bool IsAvailable()
    {
        try { return GazeFirst.eyetuitive.IsAvailable(); }
        catch { return false; }
    }

    private void OnGazeData(object? sender, GazeEventArgs e)
    {
        if (!e.userPresent) return;

        // eyetuitive returns normalized 0.0-1.0 screen-relative coordinates.
        // Convert to screen pixels using the actual screen dimensions.
        GetScreenSize(out int screenW, out int screenH);

        var x = (float)(e.gazePoint.X * screenW);
        var y = (float)(e.gazePoint.Y * screenH);

        GazeDataReceived?.Invoke(this, new GazePoint(x, y,
            DateTimeOffset.UtcNow, isValid: true, isScreenCoordinates: true));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static void GetScreenSize(out int width, out int height)
    {
        const int SM_CXSCREEN = 0;
        const int SM_CYSCREEN = 1;
        width = GetSystemMetrics(SM_CXSCREEN);
        height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;
    }
}

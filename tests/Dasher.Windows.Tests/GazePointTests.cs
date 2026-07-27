using Dasher.Windows.EyeGaze;

namespace Dasher.Windows.Tests;

public class GazePointTests
{
    [Fact]
    public void Default_gaze_point_is_invalid()
    {
        var point = new GazePoint(0, 0, DateTimeOffset.UtcNow, isValid: false);
        Assert.False(point.IsValid);
    }

    [Fact]
    public void Valid_gaze_point_preserves_coordinates()
    {
        var ts = DateTimeOffset.UtcNow;
        var point = new GazePoint(100.5f, 200.3f, ts, isValid: true);
        Assert.Equal(100.5f, point.X);
        Assert.Equal(200.3f, point.Y);
        Assert.Equal(ts, point.Timestamp);
        Assert.True(point.IsValid);
    }

    [Fact]
    public void Screen_coordinate_flag_defaults_false()
    {
        var point = new GazePoint(100, 200, DateTimeOffset.UtcNow);
        Assert.False(point.IsScreenCoordinates);
    }

    [Fact]
    public void Screen_coordinate_flag_can_be_set()
    {
        var point = new GazePoint(100, 200, DateTimeOffset.UtcNow, isValid: true, isScreenCoordinates: true);
        Assert.True(point.IsScreenCoordinates);
    }

    [Theory]
    [InlineData(0.0f, 0.0f)]       // top-left corner
    [InlineData(0.5f, 0.5f)]         // center
    [InlineData(1.0f, 1.0f)]         // bottom-right
    [InlineData(0.25f, 0.75f)]       // arbitrary
    public void Eyetuitive_normalized_to_screen_pixels(float normX, float normY)
    {
        // Simulates what EyetuitiveTracker does: normalized 0-1 → screen pixels
        int screenW = 1920;
        int screenH = 1080;
        var px = normX * screenW;
        var py = normY * screenH;
        
        var point = new GazePoint(px, py, DateTimeOffset.UtcNow, isValid: true, isScreenCoordinates: true);
        
        Assert.Equal(normX * screenW, point.X, 1);
        Assert.Equal(normY * screenH, point.Y, 1);
        Assert.True(point.IsScreenCoordinates);
    }

    [Theory]
    [InlineData(-1f, -1f, false)]    // Tobii "lost tracking" signal
    [InlineData(960f, 540f, true)]    // valid gaze at screen center
    [InlineData(0f, 0f, true)]        // top-left is valid
    public void Tobii_invalid_coordinates_are_filtered(float x, float y, bool expectedValid)
    {
        // Simulates TobiiStreamEngineTracker's filter logic
        var isValid = x >= 0 && y >= 0;
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void IEyeTrackerService_implementations_share_interface()
    {
        // Verify all tracker types implement the same interface
        Assert.True(typeof(IEyeTrackerService).IsAssignableFrom(typeof(EyetuitiveTracker)));
        Assert.True(typeof(IEyeTrackerService).IsAssignableFrom(typeof(TobiiStreamEngineTracker)));
        Assert.True(typeof(IEyeTrackerService).IsAssignableFrom(typeof(WindowsGazeTracker)));
        Assert.True(typeof(IEyeTrackerService).IsAssignableFrom(typeof(UdpGazeTracker)));
    }

    [Fact]
    public void Tracker_names_are_distinct()
    {
        var names = new[]
        {
            new WindowsGazeTracker().TrackerName,
            new UdpGazeTracker().TrackerName,
            new EyetuitiveTracker().TrackerName,
            new TobiiStreamEngineTracker().TrackerName,
        };
        var distinct = names.Distinct().Count();
        Assert.Equal(names.Length, distinct);
    }
}

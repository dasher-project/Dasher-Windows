using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

public class UpdateCheckSettingsTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "dasher-tests", $"update-check-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void RecordCheck_preserves_opt_out_made_while_request_in_flight()
    {
        // Arrange: the startup path loaded settings when Enabled was true...
        var staleSnapshot = new UpdateCheckSettings { Enabled = true, LastCheckEpochMs = 0 };
        // ...then the user disabled the check in Settings > Privacy mid-flight.
        var optedOut = new UpdateCheckSettings { Enabled = false, LastCheckEpochMs = 0 };
        optedOut.Save(_path);
        Assert.False(UpdateCheckSettings.Load(_path).Enabled);

        // Act: the network request completes and the startup path records the check
        // on its STALE instance (Enabled = true).
        staleSnapshot.RecordCheck(_path);

        // Assert: the opt-out survives; only the timestamp was touched.
        var persisted = UpdateCheckSettings.Load(_path);
        Assert.False(persisted.Enabled, "RecordCheck must not resurrect a mid-flight opt-out");
        Assert.True(persisted.LastCheckEpochMs > 0, "the check timestamp should still be recorded");
    }

    [Fact]
    public void RecordCheck_preserves_skipped_version_from_disk()
    {
        var stale = new UpdateCheckSettings { Enabled = true };
        new UpdateCheckSettings { Enabled = true, SkippedVersion = "v9.9.9" }.Save(_path);

        stale.RecordCheck(_path);

        Assert.Equal("v9.9.9", UpdateCheckSettings.Load(_path).SkippedVersion);
    }

    [Fact]
    public void ShouldCheck_respects_weekly_throttle()
    {
        var justChecked = new UpdateCheckSettings
        {
            Enabled = true,
            LastCheckEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        Assert.False(justChecked.ShouldCheck);

        var lastWeek = new UpdateCheckSettings
        {
            Enabled = true,
            LastCheckEpochMs = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds(),
        };
        Assert.True(lastWeek.ShouldCheck);

        Assert.False(new UpdateCheckSettings { Enabled = false }.ShouldCheck);
    }
}

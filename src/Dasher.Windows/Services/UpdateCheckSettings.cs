using System;
using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Services;

/// <summary>
/// RFC 0017: persisted state for the passive update check. Weekly throttle,
/// opt-out, and skip-version. Stored in %APPDATA%\Dasher\update-check.json.
/// </summary>
public class UpdateCheckSettings
{
    public bool Enabled { get; set; } = true;
    public long LastCheckEpochMs { get; set; }
    public string? SkippedVersion { get; set; }

    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "update-check.json");

    public const long CheckIntervalMs = 7L * 24 * 60 * 60 * 1000; // 7 days

    public bool ShouldCheck =>
        Enabled && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastCheckEpochMs) >= CheckIntervalMs;

    public static UpdateCheckSettings Load(string? path = null)
    {
        path ??= DefaultSettingsPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<UpdateCheckSettings>(json) ?? new UpdateCheckSettings();
            }
        }
        catch { }
        return new UpdateCheckSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultSettingsPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    /// <summary>
    /// Record that a check just ran, touching ONLY the timestamp. Re-reads
    /// the file first: the caller's instance can be stale by the time the
    /// network await completes (the user may have toggled the opt-out
    /// mid-flight), and writing the stale snapshot would silently re-enable
    /// the check (PR #44 review finding).
    /// </summary>
    public void RecordCheck(string? path = null)
    {
        var fresh = Load(path);
        fresh.LastCheckEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        fresh.Save(path);
    }
}

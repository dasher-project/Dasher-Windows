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

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "update-check.json");

    public const long CheckIntervalMs = 7L * 24 * 60 * 60 * 1000; // 7 days

    public bool ShouldCheck =>
        Enabled && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastCheckEpochMs) >= CheckIntervalMs;

    public static UpdateCheckSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<UpdateCheckSettings>(json) ?? new UpdateCheckSettings();
            }
        }
        catch { }
        return new UpdateCheckSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    public void RecordCheck()
    {
        LastCheckEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Save();
    }
}

using System;
using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Services;

public class AnalyticsSettings
{
    public bool OptedIn { get; set; }
    public bool PromptShown { get; set; }
    public string? AnonymousId { get; set; }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "analytics_settings.json");

    public static AnalyticsSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AnalyticsSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { }
        return new AnalyticsSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public string GetOrCreateAnonymousId()
    {
        if (!string.IsNullOrEmpty(AnonymousId))
            return AnonymousId!;
        AnonymousId = Guid.NewGuid().ToString();
        Save();
        return AnonymousId;
    }

    public void ResetAnonymousId()
    {
        AnonymousId = Guid.NewGuid().ToString();
        Save();
    }
}

using System;
using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Services;

/// <summary>Frontend UI preferences persisted to %APPDATA%\Dasher\ui.json.</summary>
public class UiSettings
{
    /// <summary>Progressive disclosure (RFC 0006): false = Simple (common params
    /// only), true = Advanced (all params including advanced/expert tiers).</summary>
    public bool ShowAdvanced { get; set; }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "ui.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<UiSettings>(json) ?? new UiSettings();
            }
        }
        catch { }
        return new UiSettings();
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
}

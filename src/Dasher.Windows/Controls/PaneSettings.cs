using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Controls;

public class PaneSettings
{
    public string PanePosition { get; set; } = "Right";

    private static readonly string SettingsPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Dasher", "pane_settings.json");

    public static PaneSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<PaneSettings>(json) ?? new PaneSettings();
            }
        }
        catch { }
        return new PaneSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}

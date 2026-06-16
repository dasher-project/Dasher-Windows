using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Controls;

public class OutputTextSettings
{
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontSize { get; set; } = 18;
    public double KeyboardOpacity { get; set; } = 0.85;

    private static readonly string SettingsPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Dasher", "output_settings.json");

    public static OutputTextSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<OutputTextSettings>(json) ?? new OutputTextSettings();
            }
        }
        catch { }
        return new OutputTextSettings();
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

using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Controls;

/// <summary>
/// Window geometry persistence (issue #46). Separate bounds for normal and
/// direct (keyboard) mode: quitting in direct mode saves the shrunken
/// canvas-only window, which must not corrupt the next normal-mode launch.
/// Maximized is recorded but only restored for normal mode (direct mode is
/// a deliberately small overlay).
/// </summary>
public class WindowSettings
{
    // Normal-mode bounds (pixels, screen coordinates).
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; } = 1024;
    public double Height { get; set; } = 768;
    public bool Maximized { get; set; }

    // Direct (keyboard) mode bounds.
    public double DirectX { get; set; } = double.NaN;
    public double DirectY { get; set; } = double.NaN;
    public double DirectWidth { get; set; } = double.NaN;
    public double DirectHeight { get; set; } = double.NaN;

    public bool HasNormalBounds => !double.IsNaN(X) && !double.IsNaN(Y);
    public bool HasDirectBounds => !double.IsNaN(DirectX) && !double.IsNaN(DirectY);

    private static readonly string SettingsPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Dasher", "window_settings.json");

    public static WindowSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<WindowSettings>(json) ?? new WindowSettings();
            }
        }
        catch { }
        return new WindowSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

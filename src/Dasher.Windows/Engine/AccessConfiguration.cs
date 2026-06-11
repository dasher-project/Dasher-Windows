using System;
using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Engine;

public class AccessConfiguration
{
    public AccessMethod Method { get; set; } = AccessMethod.Pointer;
    public SelectionMethod Selection { get; set; } = SelectionMethod.Continuous;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher", "access.json");

    public void Apply(IntPtr handle)
    {
        NativeBridge.dasher_set_string_parameter(handle, ParameterKeys.SP_INPUT_FILTER,
            Selection.FilterName());

        if (Selection == SelectionMethod.Dwell)
            NativeBridge.dasher_set_bool_parameter(handle, 17, 1);
    }

    public static AccessConfiguration Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AccessConfiguration>(json) ?? new AccessConfiguration();
            }
        }
        catch { }
        return new AccessConfiguration();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

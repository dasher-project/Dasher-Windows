using System;
using System.IO;
using System.Text.Json;

namespace Dasher.Windows.Engine;

public class AccessConfiguration
{
    public AccessMethod Method { get; set; } = AccessMethod.Pointer;
    public SelectionMethod Selection { get; set; } = SelectionMethod.Continuous;
    public string EyeTrackerType { get; set; } = "WindowsNative";
    public int UdpPort { get; set; } = 5555;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher", "access.json");

    public void Apply(IntPtr handle)
    {
        try
        {
            NativeBridge.dasher_set_string_parameter(handle, ParameterKeys.SP_INPUT_FILTER,
                Selection.FilterName());

            if (Selection == SelectionMethod.Dwell)
                NativeBridge.dasher_set_bool_parameter(handle, 17, 1);

            // Auto-calibration is Dasher's 2004 "enhanced eyetracking mode" —
            // it corrects systematic eye-tracker Y error, and only belongs on
            // for gaze access: for pointers, deliberate off-centre steering
            // reads as bias and drifts the target offset (DasherCore #64).
            // Enabled per access method every startup, which also migrates
            // the old persisted default-true for pointer users.
            NativeBridge.dasher_set_bool_parameter(handle, ParameterKeys.BP_AUTOCALIBRATE,
                Method == AccessMethod.EyeGaze ? 1 : 0);
        }
        catch { }
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

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

    /// <summary>
    /// The engine input filter (SP_INPUT_FILTER). MIRROR ONLY — the engine's
    /// own persistence (dasher_settings.xml, DasherCore #76 / v0.2.16+) owns
    /// this value and survives restarts by itself. <see cref="Apply"/> no
    /// longer pushes it: access.json can go stale (raw parameter row, method
    /// changes) and pushing a stale value at every startup clobbered the
    /// engine's persisted choice — the "always defaults to Normal Control"
    /// report. It is still written when the access UI changes the filter, so
    /// the file remains a useful diagnostic of the last explicit choice.
    /// </summary>
    public string? InputFilter { get; set; }

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher", "access.json");

    public void Apply(IntPtr handle)
    {
        try
        {
            // NOTE: deliberately does NOT touch SP_INPUT_FILTER. The engine
            // persists that parameter itself; a startup push from here (which
            // runs BEFORE realize) overwrote whatever the user last chose
            // whenever access.json held an older value. The interactive
            // settings UI pushes the filter explicitly on user actions.

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

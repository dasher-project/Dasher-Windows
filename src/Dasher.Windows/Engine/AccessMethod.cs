namespace Dasher.Windows.Engine;

public enum AccessMethod
{
    Pointer,
    Touch,
    EyeGaze,
    Joystick,
    SwitchesOnly,
}

public static class AccessMethodExtensions
{
    public static string DisplayName(this AccessMethod method) => method switch
    {
        AccessMethod.Pointer => "Mouse / Trackpad",
        AccessMethod.Touch => "Touch",
        AccessMethod.EyeGaze => "Eye Tracker",
        AccessMethod.Joystick => "Joystick / Gamepad",
        AccessMethod.SwitchesOnly => "Switches Only",
        _ => method.ToString(),
    };

    public static string Subtitle(this AccessMethod method) => method switch
    {
        AccessMethod.Pointer => "Standard mouse or trackpad",
        AccessMethod.Touch => "Direct touch input",
        AccessMethod.EyeGaze => "Eye tracker camera",
        AccessMethod.Joystick => "Gamepad or joystick device",
        AccessMethod.SwitchesOnly => "No continuous steering",
        _ => "",
    };

    public static bool HasContinuousInput(this AccessMethod method) => method != AccessMethod.SwitchesOnly;

    public static AccessMethod[] AvailableOnWindows() =>
        [AccessMethod.Pointer, AccessMethod.Touch, AccessMethod.EyeGaze, AccessMethod.Joystick, AccessMethod.SwitchesOnly];
}

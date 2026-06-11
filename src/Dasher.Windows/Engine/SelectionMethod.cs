namespace Dasher.Windows.Engine;

public enum SelectionMethod
{
    Continuous,
    PressToMove,
    ClickToZoom,
    Dwell,
    OneSwitch,
    TwoSwitches,
    TwoPush,
    Scanning,
    DirectBoxes,
}

public static class SelectionMethodExtensions
{
    public static string DisplayName(this SelectionMethod method) => method switch
    {
        SelectionMethod.Continuous => "Continuous",
        SelectionMethod.PressToMove => "Press to Move",
        SelectionMethod.ClickToZoom => "Click to Zoom",
        SelectionMethod.Dwell => "Dwell",
        SelectionMethod.OneSwitch => "1 Switch",
        SelectionMethod.TwoSwitches => "2 Switches",
        SelectionMethod.TwoPush => "2 Push",
        SelectionMethod.Scanning => "Scanning",
        SelectionMethod.DirectBoxes => "Direct Boxes",
        _ => method.ToString(),
    };

    public static string Subtitle(this SelectionMethod method) => method switch
    {
        SelectionMethod.Continuous => "Always follows pointer",
        SelectionMethod.PressToMove => "Hold to move, release to pause",
        SelectionMethod.ClickToZoom => "Click to zoom into area",
        SelectionMethod.Dwell => "Hold still to select",
        SelectionMethod.OneSwitch => "Single switch, dynamic timing",
        SelectionMethod.TwoSwitches => "Two switches, up/down",
        SelectionMethod.TwoPush => "Single switch, push timing",
        SelectionMethod.Scanning => "Auto-scan boxes, press to select",
        SelectionMethod.DirectBoxes => "Press to select a box",
        _ => "",
    };

    public static string FilterName(this SelectionMethod method) => method switch
    {
        SelectionMethod.Continuous => "Normal Control",
        SelectionMethod.PressToMove => "Press Mode",
        SelectionMethod.ClickToZoom => "Click Mode",
        SelectionMethod.Dwell => "Normal Control",
        SelectionMethod.OneSwitch => "One Button Dynamic Mode",
        SelectionMethod.TwoSwitches => "Two Button Dynamic Mode",
        SelectionMethod.TwoPush => "Two Push Dynamic Mode",
        SelectionMethod.Scanning => "Menu Mode",
        SelectionMethod.DirectBoxes => "Direct Mode",
        _ => "Normal Control",
    };

    public static bool IsSwitchBased(this SelectionMethod method) => method switch
    {
        SelectionMethod.OneSwitch => true,
        SelectionMethod.TwoSwitches => true,
        SelectionMethod.TwoPush => true,
        SelectionMethod.Scanning => true,
        SelectionMethod.DirectBoxes => true,
        _ => false,
    };

    public static SelectionMethod[] ValidFor(this AccessMethod method) => method switch
    {
        AccessMethod.Pointer or AccessMethod.Touch =>
            [SelectionMethod.Continuous, SelectionMethod.PressToMove, SelectionMethod.ClickToZoom,
             SelectionMethod.Dwell, SelectionMethod.OneSwitch, SelectionMethod.TwoSwitches,
             SelectionMethod.TwoPush, SelectionMethod.Scanning, SelectionMethod.DirectBoxes],
        AccessMethod.EyeGaze =>
            [SelectionMethod.Continuous, SelectionMethod.Dwell, SelectionMethod.OneSwitch,
             SelectionMethod.TwoSwitches, SelectionMethod.Scanning, SelectionMethod.DirectBoxes],
        AccessMethod.Joystick =>
            [SelectionMethod.Continuous, SelectionMethod.PressToMove, SelectionMethod.ClickToZoom,
             SelectionMethod.OneSwitch, SelectionMethod.TwoSwitches, SelectionMethod.Scanning, SelectionMethod.DirectBoxes],
        AccessMethod.SwitchesOnly =>
            [SelectionMethod.OneSwitch, SelectionMethod.TwoSwitches, SelectionMethod.TwoPush,
             SelectionMethod.Scanning, SelectionMethod.DirectBoxes],
        _ => [SelectionMethod.Continuous],
    };
}

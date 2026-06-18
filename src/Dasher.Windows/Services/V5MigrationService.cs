using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Services;

public class V5MigrationResult
{
    public List<string> Imported { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public List<string> CopiedFiles { get; set; } = new();
    public List<(int key, string value)> DeferredParameters { get; set; } = new();
    public bool HasData { get; set; }
    public string Alphabet { get; set; } = "";
    public string Colour { get; set; } = "";
    public string Speed { get; set; } = "";
    public bool ControlMode { get; set; }
    public int CustomFileCount { get; set; }
}

/// <summary>
/// Detects and imports Dasher v5 settings and user data on Windows.
/// v5 stores data in %APPDATA%\dasher.rc\ — v6 uses %APPDATA%\Dasher\.
/// </summary>
public static class V5MigrationService
{
    private static readonly string V5Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dasher.rc");
    private static readonly string V5SettingsFile = Path.Combine(V5Dir, "settings.xml");

    private static readonly string V6Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher");

    private static readonly string MigrationFlagFile = Path.Combine(V6Dir, "v5_migration_completed");

    public static bool HasBeenOffered => File.Exists(MigrationFlagFile);
    public static bool HasV5Data => File.Exists(V5SettingsFile);

    // v5 regName → v6 enum name for bool parameters
    private static readonly Dictionary<string, string> BoolMappings = new()
    {
        ["DrawMouseLine"] = "BP_DRAW_MOUSE_LINE",
        ["DrawMouse"] = "BP_DRAW_MOUSE",
        ["CurveMouseLine"] = "BP_CURVE_MOUSE_LINE",
        ["StartOnLeft"] = "BP_START_MOUSE",
        ["StartOnSpace"] = "BP_START_SPACE",
        ["ControlMode"] = "BP_CONTROL_MODE",
        ["PaletteChange"] = "BP_PALETTE_CHANGE",
        ["TurboMode"] = "BP_TURBO_MODE",
        ["ExactDynamics"] = "BP_EXACT_DYNAMICS",
        ["Autocalibrate"] = "BP_AUTOCALIBRATE",
        ["RemapXtreme"] = "BP_REMAP_XTREME",
        ["AutoSpeedControl"] = "BP_AUTO_SPEEDCONTROL",
        ["LMAdaptive"] = "BP_LM_ADAPTIVE",
        ["NonlinearY"] = "BP_NONLINEAR_Y",
        ["PauseOutside"] = "BP_STOP_OUTSIDE",
        ["BackoffButton"] = "BP_BACKOFF_BUTTON",
        ["TwoButtonReverse"] = "BP_TWOBUTTON_REVERSE",
        ["TwoButtonInvertDouble"] = "BP_2B_INVERT_DOUBLE",
        ["SlowStart"] = "BP_SLOW_START",
        ["CopyOnStop"] = "BP_COPY_ALL_ON_STOP",
        ["SpeakOnStop"] = "BP_SPEAK_ALL_ON_STOP",
        ["SpeakWords"] = "BP_SPEAK_WORDS",
        ["SlowControlBox"] = "BP_SLOW_CONTROL_BOX",
    };

    // All parameters must be deferred until after Realize() — some trigger
    // handlers that dereference m_pDasherModel/m_pNCManager which are null
    // before Realize. Deferring everything is safest.
    // (Previously only 3 were deferred, but LP_NODE_BUDGET etc. also crash.)

    // v5 regName → v6 enum name for long parameters
    private static readonly Dictionary<string, string> LongMappings = new()
    {
        ["ScreenOrientation"] = "LP_ORIENTATION",
        ["MaxBitRateTimes100"] = "LP_MAX_BITRATE",
        ["UniformTimes1000"] = "LP_UNIFORM",
        ["LMAlpha"] = "LP_LM_ALPHA",
        ["LMBeta"] = "LP_LM_BETA",
        ["LMMaxOrder"] = "LP_LM_MAX_ORDER",
        ["LMExclusion"] = "LP_LM_EXCLUSION",
        ["LMUpdateExclusion"] = "LP_LM_UPDATE_EXCLUSION",
        ["LMMixture"] = "LP_LM_MIXTURE",
        ["LineWidth"] = "LP_LINE_WIDTH",
        ["Zoomsteps"] = "LP_ZOOMSTEPS",
        ["NodeBudget"] = "LP_NODE_BUDGET",
        ["MarginWidth"] = "LP_MARGIN_WIDTH",
        ["TargetOffset"] = "LP_TARGET_OFFSET",
        ["XLimitSpeed"] = "LP_X_LIMIT_SPEED",
        ["MinNodeSize"] = "LP_MIN_NODE_SIZE",
        ["OutlineWidth"] = "LP_OUTLINE_WIDTH",
        ["NonLinearX"] = "LP_NONLINEAR_X",
        ["AutospeedSensitivity"] = "LP_AUTOSPEED_SENSITIVITY",
        ["Geometry"] = "LP_GEOMETRY",
        ["WordAlpha"] = "LP_LM_WORD_ALPHA",
        ["MessageFontSize"] = "LP_MESSAGE_FONTSIZE",
        ["RenderStyle"] = "LP_SHAPE_TYPE",
        ["CirclePercent"] = "LP_CIRCLE_PERCENT",
        ["TwoButtonOffset"] = "LP_TWO_BUTTON_OFFSET",
        ["HoldTime"] = "LP_HOLD_TIME",
        ["MultipressTime"] = "LP_MULTIPRESS_TIME",
        ["SlowStartTime"] = "LP_SLOW_START_TIME",
        ["TapTime"] = "LP_TAP_TIME",
        ["ClickMaxZoom"] = "LP_MAXZOOM",
        ["DynamicSpeedInc"] = "LP_DYNAMIC_SPEED_INC",
        ["DynamicSpeedFreq"] = "LP_DYNAMIC_SPEED_FREQ",
        ["DynamicSpeedDec"] = "LP_DYNAMIC_SPEED_DEC",
        ["MousePositionBoxDistance"] = "LP_MOUSEPOSDIST",
    };

    // v5 regName → v6 enum name for string parameters
    private static readonly Dictionary<string, string> StringMappings = new()
    {
        ["AlphabetID"] = "SP_ALPHABET_ID",
        ["DasherFont"] = "SP_DASHER_FONT",
        ["GameTextFile"] = "SP_GAME_TEXT_FILE",
        ["InputFilter"] = "SP_INPUT_FILTER",
        ["InputDevice"] = "SP_INPUT_DEVICE",
        ["Alphabet1"] = "SP_ALPHABET_1",
        ["Alphabet2"] = "SP_ALPHABET_2",
        ["Alphabet3"] = "SP_ALPHABET_3",
        ["Alphabet4"] = "SP_ALPHABET_4",
    };

    // v5 platform-specific keys with no v6 equivalent
    private static readonly HashSet<string> PlatformOnlyKeys = new()
    {
        "AppStyle", "EditFont", "EditFontSize", "EditHeight", "EditWidth",
        "FileEncodingFormat", "FullScreen", "MirrorLayout", "PopupEnable",
        "PopupFont", "PopupFullScreen", "PopupInfront", "ScreenHeight",
        "ScreenHeightH", "ScreenWidth", "ScreenWidthH", "TimeStampNewFiles",
        "ToolbarID", "ViewStatusbar", "ViewToolbar", "WindowState",
        "XPosition", "YPosition", "ConfirmUnsavedFiles",
        "Button0", "Button1", "Button2", "Button3", "Button4", "Button10",
        "ButtonCompassModeRightZoom", "ButtonMenuBoxes", "ButtonMenuSafety",
        "ButtonMenuScanTime", "ButtonModeNonuniformity", "DemoNoiseMag",
        "DemoNoiseMem", "DemoSpring", "DynamicButtonLag", "EditSize",
        "FrameRate", "GameHelpDistance", "GameHelpTime", "LanguageModelID",
        "MessageTime", "PYProbabilitySortThreshold", "SocketInputXMaxTimes1000",
        "SocketInputXMinTimes1000", "SocketInputYMaxTimes1000",
        "SocketInputYMinTimes1000", "SocketPort", "Static1BTime", "Static1BZoom",
        "TwoPushLong", "TwoPushOuter", "TwoPushShort", "TwoPushTolerance",
        "UserLogLevelMask", "YScaling", "SocketInputDebug", "GlobalKeyboard",
        "GameDrawPath", "TwoPushReleaseTime", "ControlBoxID", "JoystickDevice",
        "SocketInputXLabel", "SocketInputYLabel",
    };

    /// <summary>
    /// Scan for v5 data without importing. Returns summary for display.
    /// </summary>
    public static V5MigrationResult Scan()
    {
        var result = new V5MigrationResult();

        if (!File.Exists(V5SettingsFile))
            return result;

        result.HasData = true;

        try
        {
            var doc = XDocument.Load(V5SettingsFile);
            var settings = doc.Root!;

            foreach (var el in settings.Elements())
            {
                var name = el.Attribute("name")?.Value ?? "";
                var value = el.Attribute("value")?.Value ?? "";

                if (name == "AlphabetID") result.Alphabet = value;
                else if (name == "ColourID" && !string.IsNullOrEmpty(value)) result.Colour = value;
                else if (name == "MaxBitRateTimes100") result.Speed = (int.Parse(value) / 100.0).ToString("F1") + "x";
                else if (name == "ControlMode" && value == "True") result.ControlMode = true;
            }
        }
        catch { }

        // Count custom files
        result.CustomFileCount = CountCustomFiles();

        return result;
    }

    private static int CountCustomFiles()
    {
        int count = 0;
        try
        {
            foreach (var f in Directory.GetFiles(V5Dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("alphabet.") || name.StartsWith("colour.") ||
                    name.StartsWith("color.") || name.StartsWith("control.") ||
                    name.StartsWith("training_"))
                    count++;
            }
        }
        catch { }
        return count;
    }

    /// <summary>
    /// Import v5 settings. Must be called BEFORE dasher_set_screen_size().
    /// Returns deferred parameters to apply AFTER screen size is set.
    /// </summary>
    public static V5MigrationResult Import(IntPtr handle)
    {
        var result = Scan();
        if (!result.HasData)
            return result;

        try
        {
            var doc = XDocument.Load(V5SettingsFile);
            var settings = doc.Root!;

            foreach (var el in settings.Elements())
            {
                var name = el.Attribute("name")?.Value ?? "";
                var value = el.Attribute("value")?.Value ?? "";
                var tag = el.Name.LocalName;

                switch (tag)
                {
                    case "bool" when BoolMappings.TryGetValue(name, out var enumName):
                        ImportBool(handle, result, enumName, value == "True", name);
                        break;

                    case "long" when LongMappings.TryGetValue(name, out var enumName):
                        ImportLong(handle, result, enumName, value, name);
                        break;

                    case "string" when StringMappings.TryGetValue(name, out var enumName):
                        ImportString(handle, result, enumName, value, name);
                        break;

                    case "long" when name == "DasherFontSize":
                        ImportFontSize(handle, result, value);
                        break;

                    // Start mode from two bools
                    case "bool" when name == "CircleStart" && value == "True":
                        SetStartMode(handle, result, 2);
                        break;
                    case "bool" when name == "StartOnMousePosition" && value == "True":
                        SetStartMode(handle, result, 1);
                        break;

                    // Colour ID
                    case "string" when name == "ColourID" && !string.IsNullOrEmpty(value):
                        ImportColourId(handle, result, value);
                        break;

                    // Skip platform-only and unknown keys
                    default:
                        if (!PlatformOnlyKeys.Contains(name) &&
                            !BoolMappings.ContainsKey(name) &&
                            !LongMappings.ContainsKey(name) &&
                            !StringMappings.ContainsKey(name) &&
                            name != "DasherFontSize" && name != "ColourID" &&
                            name != "CircleStart" && name != "StartOnMousePosition")
                        {
                            result.Skipped.Add(name);
                        }
                        break;
                }
            }
        }
        catch { }

        // Copy user data files
        CopyUserDataFiles(result);

        // Mark as completed
        MarkCompleted();

        return result;
    }

    private static void ImportBool(IntPtr handle, V5MigrationResult result, string enumName, bool value, string regName)
    {
        var key = NativeBridge.dasher_find_parameter_key(enumName);
        if (key < 0) { result.Skipped.Add(regName); return; }

        result.DeferredParameters.Add((key, value ? "true" : "false"));
        result.Imported.Add($"{regName} = {value}");
    }

    private static void ImportLong(IntPtr handle, V5MigrationResult result, string enumName, string valueStr, string regName)
    {
        if (!int.TryParse(valueStr, out var value)) { result.Skipped.Add(regName); return; }
        var key = NativeBridge.dasher_find_parameter_key(enumName);
        if (key < 0) { result.Skipped.Add(regName); return; }

        result.DeferredParameters.Add((key, value.ToString()));
        result.Imported.Add($"{regName} = {value}");
    }

    private static void ImportString(IntPtr handle, V5MigrationResult result, string enumName, string value, string regName)
    {
        var key = NativeBridge.dasher_find_parameter_key(enumName);
        if (key < 0) { result.Skipped.Add(regName); return; }

        result.DeferredParameters.Add((key, value));
        result.Imported.Add($"{regName} = \"{value}\"");
    }

    private static void ImportFontSize(IntPtr handle, V5MigrationResult result, string valueStr)
    {
        if (!int.TryParse(valueStr, out var index)) return;
        var points = index switch
        {
            0 => 14, 1 => 18, 2 => 22, 3 => 28, 4 => 36,
            _ => Math.Min(index * 8, 72),
        };
        var key = NativeBridge.dasher_find_parameter_key("LP_DASHER_FONTSIZE");
        if (key >= 0)
        {
            result.DeferredParameters.Add((key, points.ToString()));
            result.Imported.Add($"DasherFontSize: index {index} → {points}pt");
        }
    }

    private static void SetStartMode(IntPtr handle, V5MigrationResult result, int mode)
    {
        var key = NativeBridge.dasher_find_parameter_key("LP_START_MODE");
        if (key >= 0)
        {
            result.DeferredParameters.Add((key, mode.ToString()));
            result.Imported.Add($"StartMode = {mode}");
        }
    }

    private static void ImportColourId(IntPtr handle, V5MigrationResult result, string value)
    {
        var key = NativeBridge.dasher_find_parameter_key("SP_COLOUR_ID");
        if (key >= 0)
        {
            result.DeferredParameters.Add((key, value));
            result.Imported.Add($"ColourID = \"{value}\"");
        }
    }

    private static void CopyUserDataFiles(V5MigrationResult result)
    {
        try
        {
            Directory.CreateDirectory(V6Dir);

            foreach (var f in Directory.GetFiles(V5Dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                var shouldCopy = name.StartsWith("alphabet.") ||
                                 name.StartsWith("colour.") ||
                                 name.StartsWith("color.") ||
                                 name.StartsWith("control.") ||
                                 name.StartsWith("training_");
                if (!shouldCopy) continue;

                var dest = Path.Combine(V6Dir, name);
                if (File.Exists(dest))
                {
                    result.Skipped.Add($"File: {name} (already exists)");
                }
                else
                {
                    File.Copy(f, dest);
                    result.CopiedFiles.Add(name);
                }
            }
        }
        catch { }
    }

    public static void MarkCompleted()
    {
        try { File.WriteAllText(MigrationFlagFile, DateTime.UtcNow.ToString("o")); }
        catch { }
    }
}

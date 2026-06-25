using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // v5 system directory — custom alphabets/colours/controls may be installed here
    private static readonly string[] V5SystemDirs =
    [
        @"C:\Program Files (x86)\Dasher\Dasher 5.00\system.rc",
        @"C:\Program Files\Dasher\Dasher 5.00\system.rc",
        @"C:\Program Files (x86)\Dasher\Dasher 5.0\system.rc",
    ];

    private static readonly string V6Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher");

    private static readonly string MigrationFlagFile = Path.Combine(V6Dir, "v5_migration_completed");

    /// <summary>
    /// Only re-offer if the app version changed since last migration.
    /// This ensures users who ran a broken migration get re-prompted on update.
    /// </summary>
    public static bool HasBeenOffered
    {
        get
        {
            if (!File.Exists(MigrationFlagFile)) return false;
            try
            {
                var flagVersion = File.ReadAllText(MigrationFlagFile).Trim();
                var currentVersion = UpdateChecker.GetCurrentVersion();
                return flagVersion == currentVersion;
            }
            catch { return false; }
        }
    }
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
        var dirs = new List<string> { V5Dir };
        foreach (var sysDir in V5SystemDirs)
            if (Directory.Exists(sysDir)) dirs.Add(sysDir);

        int count = 0;
        var seen = new HashSet<string>();
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(f);
                    if ((name.StartsWith("alphabet.") || name.StartsWith("colour.") ||
                         name.StartsWith("color.") || name.StartsWith("control.") ||
                         name.StartsWith("training_")) && seen.Add(name))
                        count++;
                }
            }
            catch { }
        }
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
        // Scan both user dir and system dirs for custom files
        var sourceDirs = new List<string> { V5Dir };
        foreach (var sysDir in V5SystemDirs)
        {
            if (Directory.Exists(sysDir))
                sourceDirs.Add(sysDir);
        }

        foreach (var sourceDir in sourceDirs)
        {
            try
            {
                if (!Directory.Exists(sourceDir)) continue;

                foreach (var f in Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(f);
                    string? destSubdir = null;

                    if (name.StartsWith("alphabet."))
                        destSubdir = "alphabets";
                    else if (name.StartsWith("colour.") || name.StartsWith("color."))
                        destSubdir = "colours";
                    else if (name.StartsWith("control."))
                        destSubdir = "control";
                    else if (name.StartsWith("training_"))
                        destSubdir = "training";
                    else
                        continue;

                    var destDir = destSubdir != null ? Path.Combine(V6Dir, destSubdir) : V6Dir;
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, name);

                    var overwrite = name.Equals("control.xml", StringComparison.OrdinalIgnoreCase);

                    if (File.Exists(dest) && !overwrite)
                    {
                        // Already exists — skip silently (user dir checked first, wins)
                    }
                    else
                    {
                        // Convert v5 alphabet XML to v6 format if needed
                        if (name.StartsWith("alphabet.") && name.EndsWith(".xml"))
                        {
                            var converted = ConvertV5AlphabetToV6(f);
                            File.WriteAllText(dest, converted);
                        }
                        else
                        {
                            File.Copy(f, dest, overwrite);
                        }
                        if (!result.CopiedFiles.Contains(name))
                            result.CopiedFiles.Add(name);
                    }
                }
            }
            catch { }
        }
    }

    public static void MarkCompleted()
    {
        try { File.WriteAllText(MigrationFlagFile, UpdateChecker.GetCurrentVersion()); }
        catch { }
    }

    /// <summary>
    /// Converts a v5 alphabet XML file to v6 format.
    /// v5: <alphabets> root, <s d="a" t="a" b="10"/> symbols, <train>/<palette>/<orientation> children
    /// v6: <alphabet> root, <node label="a"><textCharAction/></node> symbols, attributes for metadata
    /// </summary>
    private static string ConvertV5AlphabetToV6(string sourcePath)
    {
        var content = File.ReadAllText(sourcePath);

        // If already v6 format (<alphabet> root), return as-is
        if (content.Contains("<alphabet ") && !content.Contains("<alphabets"))
            return content;

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(content);
            var root = doc.Root;

            // Find the first <alphabet> child (v5 uses <alphabets> wrapper)
            var alphNode = root?.Name == "alphabets"
                ? root.Element("alphabet")
                : (root?.Name == "alphabet" ? root : null);

            if (alphNode == null) return content; // can't parse, return original

            // Build v6 alphabet element
            var v6 = new System.Xml.Linq.XElement("alphabet");

            // Name attribute
            var name = alphNode.Attribute("name")?.Value ?? "Imported";
            v6.SetAttributeValue("name", name);

            // Training file — from <train> child or trainingFilename attribute
            var train = alphNode.Element("train")?.Value ?? alphNode.Attribute("trainingFilename")?.Value ?? "";
            if (!string.IsNullOrEmpty(train))
                v6.SetAttributeValue("trainingFilename", train);

            // Colour palette — from <palette> child or colorsName attribute
            var palette = alphNode.Element("palette")?.Value ?? alphNode.Attribute("colorsName")?.Value ?? "";
            if (!string.IsNullOrEmpty(palette))
                v6.SetAttributeValue("colorsName", palette);

            // Orientation — from <orientation type="LR"/> child or attribute
            var orient = alphNode.Element("orientation")?.Attribute("type")?.Value
                         ?? alphNode.Attribute("orientation")?.Value ?? "LR";
            v6.SetAttributeValue("orientation", orient);

            // Convert <s> elements inside <group> elements to <node> elements
            foreach (var group in alphNode.Elements("group"))
            {
                var v6Group = new System.Xml.Linq.XElement("group");
                v6Group.SetAttributeValue("name", group.Attribute("name")?.Value ?? "");
                v6Group.SetAttributeValue("colorInfoName", group.Attribute("colorInfoName")?.Value
                    ?? (group.Attribute("b")?.Value != null ? "lowercase" : "lowercase"));

                foreach (var s in group.Elements("s"))
                {
                    var display = s.Attribute("d")?.Value ?? "";
                    var text = s.Attribute("t")?.Value ?? display;

                    var node = new System.Xml.Linq.XElement("node");
                    node.SetAttributeValue("label", display);
                    if (text != display)
                        node.SetAttributeValue("text", text);

                    var action = new System.Xml.Linq.XElement("textCharAction");
                    node.Add(action);
                    v6Group.Add(node);
                }

                if (v6Group.HasElements)
                    v6.Add(v6Group);
            }

            // Handle <space>, <paragraph> elements (v5 special characters)
            ConvertSpecialChar(alphNode, v6, "space", "□", " ");
            ConvertSpecialChar(alphNode, v6, "paragraph", "¶", "\n");

            var result = new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XDeclaration("1.0", "UTF-8", null),
                v6);
            return result.ToString();
        }
        catch
        {
            return content; // conversion failed, return original
        }
    }

    private static void ConvertSpecialChar(System.Xml.Linq.XElement v5Alphabet,
        System.Xml.Linq.XElement v6Alphabet, string elementName, string defaultDisplay, string defaultText)
    {
        var el = v5Alphabet.Element(elementName);
        if (el == null) return;

        var display = el.Attribute("d")?.Value ?? defaultDisplay;
        var text = el.Attribute("t")?.Value ?? defaultText;

        // Find or create a paragraphSpace group
        var group = v6Alphabet.Elements("group").FirstOrDefault(g =>
            g.Attribute("name")?.Value == "paragraphSpace");
        if (group == null)
        {
            group = new System.Xml.Linq.XElement("group");
            group.SetAttributeValue("name", "paragraphSpace");
            group.SetAttributeValue("colorInfoName", "paragraphSpace");
            v6Alphabet.Add(group);
        }

        var node = new System.Xml.Linq.XElement("node");
        node.SetAttributeValue("label", display);
        if (text != display)
            node.SetAttributeValue("text", text);
        node.Add(new System.Xml.Linq.XElement("textCharAction"));
        group.Add(node);
    }
}

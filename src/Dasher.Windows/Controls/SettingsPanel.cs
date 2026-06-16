using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Dasher.Windows.Engine;
using Dasher.Windows.Speech;

namespace Dasher.Windows.Controls;

public class ParameterDisplayInfo
{
    public int Key;
    public string Name = "";
    public string Desc = "";
    public int Type;
    public int UiType;
    public int MinVal;
    public int MaxVal;
    public int Step;
    public int Advanced;
    public string Group = "";
    public string Subgroup = "";
}

public class SettingsPanel : Control
{
    private IntPtr _handle;
    private readonly StackPanel _panel;
    private readonly ScrollViewer _scrollViewer;
    private string _currentCategory = "";
    private readonly Dictionary<string, List<ParameterDisplayInfo>> _groups = new();

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SettingsPanel, string>(nameof(Title));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public SettingsPanel()
    {
        _panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10,
            Margin = new Thickness(16, 10, 16, 10),
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        VisualChildren.Add(_scrollViewer);
        LogicalChildren.Add(_scrollViewer);
    }

    public void Initialize(IntPtr handle)
    {
        _handle = handle;
        LoadParameterGroups();
    }

    public List<string> GetCategoryNames()
    {
        var names = new List<string>(_groups.Keys) { "Speech" };
        names.Sort((a, b) =>
        {
            var order = new Dictionary<string, int>
            {
                ["Input"] = 0, ["Language"] = 1, ["Customization"] = 2,
                ["Speed"] = 3, ["Output"] = 4, ["Speech"] = 5,
                ["Advanced"] = 6, ["Other"] = 7, ["Appearance"] = 8,
            };
            int oa = order.TryGetValue(a, out var va) ? va : 99;
            int ob = order.TryGetValue(b, out var vb) ? vb : 99;
            return oa.CompareTo(ob);
        });
        return names;
    }

    public void ShowCategory(string category)
    {
        try
        {
            ShowCategoryCore(category);
        }
        catch (Exception ex)
        {
            _panel.Children.Clear();
            _panel.Children.Add(new TextBlock
            {
                Text = $"Error loading settings: {ex.Message}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0x5B, 0x5C)),
                Margin = new Thickness(16),
            });
        }
    }

    private void ShowCategoryCore(string category)
    {
        if (_handle == IntPtr.Zero) return;
        _currentCategory = category;
        Title = category;

        _panel.Children.Clear();

        var backBtn = new Button
        {
            Content = "\u2190 Back",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9A)),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(0),
        };
        backBtn.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
        _panel.Children.Add(backBtn);

        if (category == "Input")
        {
            var inputSourceRow = BuildInputSourceRow();
            if (inputSourceRow != null)
                _panel.Children.Add(inputSourceRow);
        }

        if (category == "Language")
        {
            var localeRow = BuildLocaleRow();
            if (localeRow != null)
                _panel.Children.Add(localeRow);
        }

        if (category == "Speech")
        {
            BuildSpeechSettings();
            return;
        }

        if (category == "Output")
        {
            var outputFontRow = BuildOutputFontRow();
            if (outputFontRow != null)
                _panel.Children.Add(outputFontRow);

            var transparencyRow = BuildKeyboardTransparencyRow();
            if (transparencyRow != null)
                _panel.Children.Add(transparencyRow);
        }

        if (!_groups.TryGetValue(category, out var parameters)) return;

        if (category == "Input")
            parameters = FilterByActiveInputFilter(parameters);

        if (category == "Language")
            parameters = FilterByActiveLanguageModel(parameters);

        var grouped = parameters
            .GroupBy(p => string.IsNullOrEmpty(p.Subgroup) ? "" : p.Subgroup)
            .ToList();

        foreach (var group in grouped)
        {
            if (!string.IsNullOrEmpty(group.Key) && grouped.Count > 1)
            {
                var header = new TextBlock
                {
                    Text = group.Key,
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
                    Margin = new Thickness(0, 10, 0, 4),
                };
                _panel.Children.Add(header);
            }

            foreach (var info in group)
            {
                try
                {
                    // Skip colour palette - rendered as swatch picker at top
                    if (IsColourPaletteParameter(info)) continue;

                    var row = BuildParameterRow(info);
                    if (row != null)
                        _panel.Children.Add(row);
                }
                catch { }
            }
        }

        // Add colour palette swatch picker at top of Customization
        if (category == "Customization")
        {
            var paletteRow = BuildPaletteSwatchPicker();
            if (paletteRow != null)
                _panel.Children.Insert(1, paletteRow);
        }
    }

    public event EventHandler? BackRequested;
    public event EventHandler<(EyeGazeIntegration.TrackerType trackerType, int udpPort)>? InputSourceChanged;
    public event EventHandler? JoystickRequested;
    public event Action<string, int>? OutputFontChanged;
    public event Action<double>? KeyboardOpacityChanged;

    private static readonly string[] OutputFontPresets =
    [
        "Segoe UI", "Arial", "Calibri", "Cambria", "Comic Sans MS",
        "Consolas", "Courier New", "Georgia", "Tahoma", "Times New Roman",
        "Trebuchet MS", "Verdana",
    ];

    private Control? BuildOutputFontRow()
    {
        var settings = OutputTextSettings.Load();
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };

        var fontLabel = new TextBlock
        {
            Text = "Output Font",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
        };
        panel.Children.Add(fontLabel);

        var fontCombo = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };
        foreach (var font in OutputFontPresets)
        {
            fontCombo.Items.Add(font);
            if (font == settings.FontFamily)
                fontCombo.SelectedIndex = fontCombo.Items.Count - 1;
        }
        if (fontCombo.SelectedIndex < 0) fontCombo.SelectedIndex = 0;

        panel.Children.Add(fontCombo);

        var sizeLabel = new TextBlock
        {
            Text = "Output Font Size",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(sizeLabel);

        var sizeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var sizeDown = new Button { Content = "\u2212", Width = 28, Height = 28, FontSize = 14, FontWeight = FontWeight.Bold };
        var sizeValue = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xF2, 0xF1)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            MinWidth = 50,
            Child = new TextBlock
            {
                Text = settings.FontSize.ToString(),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        var sizeUp = new Button { Content = "+", Width = 28, Height = 28, FontSize = 14, FontWeight = FontWeight.Bold };

        int fontSize = settings.FontSize;
        void UpdateFontSize()
        {
            (sizeValue.Child as TextBlock)!.Text = fontSize.ToString();
            settings.FontSize = fontSize;
            settings.Save();
            OutputFontChanged?.Invoke(settings.FontFamily, fontSize);
        }

        sizeDown.Click += (s, e) => { fontSize = Math.Max(8, fontSize - 1); UpdateFontSize(); };
        sizeUp.Click += (s, e) => { fontSize = Math.Min(72, fontSize + 1); UpdateFontSize(); };

        sizeStack.Children.Add(sizeDown);
        sizeStack.Children.Add(sizeValue);
        sizeStack.Children.Add(sizeUp);
        panel.Children.Add(sizeStack);

        fontCombo.SelectionChanged += (s, e) =>
        {
            if (fontCombo.SelectedIndex >= 0 && fontCombo.SelectedIndex < OutputFontPresets.Length)
            {
                settings.FontFamily = OutputFontPresets[fontCombo.SelectedIndex];
                settings.Save();
                OutputFontChanged?.Invoke(settings.FontFamily, fontSize);
            }
        };

        return panel;
    }

    private Control? BuildKeyboardTransparencyRow()
    {
        var settings = OutputTextSettings.Load();
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };

        var label = new TextBlock
        {
            Text = "Keyboard Mode Opacity",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
        };
        panel.Children.Add(label);

        var sliderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var slider = new Slider
        {
            Minimum = 0.2,
            Maximum = 1.0,
            Value = settings.KeyboardOpacity,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 200,
        };

        var valueLabel = new TextBlock
        {
            Text = $"{(int)(settings.KeyboardOpacity * 100)}%",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };

        slider.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                var pct = (int)(slider.Value * 100);
                valueLabel.Text = $"{pct}%";
                settings.KeyboardOpacity = slider.Value;
                settings.Save();
                KeyboardOpacityChanged?.Invoke(slider.Value);
            }
        };

        sliderRow.Children.Add(slider);
        sliderRow.Children.Add(valueLabel);
        panel.Children.Add(sliderRow);

        var help = new TextBlock
        {
            Text = "Window transparency when in Keyboard/Direct mode",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9A)),
        };
        panel.Children.Add(help);

        return panel;
    }

    private Control? BuildInputSourceRow()
    {
        var panel = new StackPanel { Spacing = 8 };

        var config = AccessConfiguration.Load();

        var methodLabel = new TextBlock
        {
            Text = "Steering Method",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
        };
        panel.Children.Add(methodLabel);

        var methodCombo = new ComboBox        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };
        var methods = Engine.AccessMethodExtensions.AvailableOnWindows();
        foreach (var m in methods)
        {
            methodCombo.Items.Add(m.DisplayName());
            if (m == config.Method) methodCombo.SelectedIndex = methodCombo.Items.Count - 1;
        }
        if (methodCombo.SelectedIndex < 0) methodCombo.SelectedIndex = 0;

        var selectionLabel = new TextBlock
        {
            Text = "Selection Method",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var selectionCombo = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };

        void PopulateSelections(AccessMethod method)
        {
            selectionCombo.Items.Clear();
            var valid = method.ValidFor();
            foreach (var s in valid)
            {
                selectionCombo.Items.Add(s.DisplayName());
                if (s == config.Selection) selectionCombo.SelectedIndex = selectionCombo.Items.Count - 1;
            }
            if (selectionCombo.SelectedIndex < 0) selectionCombo.SelectedIndex = 0;
        }

        PopulateSelections(config.Method);

        var subtitle = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9A)),
            Text = config.Selection.Subtitle(),
            Margin = new Thickness(0, 2, 0, 0),
        };

        // ── Eye tracker sub-picker (visible only when EyeGaze is selected) ──────────
        var trackerLabel = new TextBlock
        {
            Text = "Eye Tracker Device",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var trackerCombo = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };
        trackerCombo.Items.Add("Windows Eye Tracker (native)");
        trackerCombo.Items.Add("UDP Gaze Tracker (network)");
        trackerCombo.SelectedIndex = config.EyeTrackerType == "UdpGaze" ? 1 : 0;

        var portPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var portLabel = new TextBlock
        {
            Text = "UDP Port",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var portBox = new NumericUpDown
        {
            Value = config.UdpPort,
            Minimum = 1,
            Maximum = 65535,
            Width = 100,
            FontSize = 12,
        };
        portPanel.Children.Add(portLabel);
        portPanel.Children.Add(portBox);

        var trackerHelp = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9A)),
            Text = "UDP tracker listens for STREAM_DATA or GazePoint messages",
            Margin = new Thickness(0, 2, 0, 0),
        };

        void UpdateTrackerVisibility()
        {
            var isEyeGaze = methods[methodCombo.SelectedIndex] == AccessMethod.EyeGaze;
            trackerLabel.IsVisible = isEyeGaze;
            trackerCombo.IsVisible = isEyeGaze;
            var isUdp = isEyeGaze && trackerCombo.SelectedIndex == 1;
            portPanel.IsVisible = isUdp;
            trackerHelp.IsVisible = isUdp;
        }

        UpdateTrackerVisibility();

        methodCombo.SelectionChanged += (s, e) =>
        {
            var method = methods[methodCombo.SelectedIndex];
            config.Method = method;
            PopulateSelections(method);
            var sel = method.ValidFor();
            config.Selection = sel[selectionCombo.SelectedIndex];
            subtitle.Text = config.Selection.Subtitle();
            UpdateTrackerVisibility();
            ApplyAccessConfig(config);
        };

        selectionCombo.SelectionChanged += (s, e) =>
        {
            var method = methods[methodCombo.SelectedIndex];
            var valid = method.ValidFor();
            if (selectionCombo.SelectedIndex >= 0 && selectionCombo.SelectedIndex < valid.Length)
            {
                config.Selection = valid[selectionCombo.SelectedIndex];
                subtitle.Text = config.Selection.Subtitle();
                ApplyAccessConfig(config);
            }
        };

        trackerCombo.SelectionChanged += (s, e) =>
        {
            config.EyeTrackerType = trackerCombo.SelectedIndex == 1 ? "UdpGaze" : "WindowsNative";
            UpdateTrackerVisibility();
            ApplyAccessConfig(config);
        };

        portBox.ValueChanged += (s, e) =>
        {
            config.UdpPort = (int)portBox.Value;
            ApplyAccessConfig(config);
        };

        panel.Children.Add(methodCombo);
        panel.Children.Add(selectionLabel);
        panel.Children.Add(selectionCombo);
        panel.Children.Add(subtitle);
        panel.Children.Add(trackerLabel);
        panel.Children.Add(trackerCombo);
        panel.Children.Add(portPanel);
        panel.Children.Add(trackerHelp);

        return panel;
    }

    private void ApplyAccessConfig(AccessConfiguration config)
    {
        config.Apply(_handle);
        config.Save();

        var trackerType = config.Method switch
        {
            AccessMethod.EyeGaze => config.EyeTrackerType switch
            {
                "UdpGaze" => EyeGazeIntegration.TrackerType.UdpGazeTracker,
                _ => EyeGazeIntegration.TrackerType.WindowsNative,
            },
            _ => EyeGazeIntegration.TrackerType.None,
        };
        InputSourceChanged?.Invoke(this, (trackerType, config.UdpPort));

        if (config.Method == AccessMethod.Joystick)
        {
            JoystickRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static readonly Dictionary<string, string> AvailableLocales = new()
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["es"] = "Espanol",
        ["fr"] = "Francais",
        ["it"] = "Italiano",
        ["pt"] = "Portugues",
        ["pt-PT"] = "Portugues (Portugal)",
        ["ar"] = "Arabic",
        ["el"] = "Greek",
        ["zh-CN"] = "Chinese (Simplified)",
    };

    private Control? BuildLocaleRow()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var label = new TextBlock
        {
            Text = "App Language",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 180,
        };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        var combo = new ComboBox
        {
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };

        var currentLocalePtr = NativeBridge.dasher_get_locale(_handle);
        var currentLocale = currentLocalePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(currentLocalePtr) ?? "en" : "en";

        foreach (var kvp in AvailableLocales)
        {
            combo.Items.Add(kvp.Value);
            if (kvp.Key == currentLocale)
                combo.SelectedIndex = combo.Items.Count - 1;
        }

        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;

        combo.SelectionChanged += (s, e) =>
        {
            var idx = combo.SelectedIndex;
            var code = AvailableLocales.Keys.ElementAt(idx);
            NativeBridge.dasher_set_locale(_handle, code);
            LoadParameterGroups();
        };

        row.Children.Add(combo);
        return row;
    }

    private static readonly Dictionary<string, HashSet<string>> FilterToSubgroup = new()
    {
        ["Normal Control"] = ["CDefaultFilter", "CDynamicFilter", "CDynamicButtons", "Control"],
        ["Press Mode"] = ["CDefaultFilter", "CPressFilter", "Control"],
        ["Click Mode"] = ["CDefaultFilter", "CClickFilter", "Control"],
        ["Compass Mode"] = ["CDefaultFilter", "CCompassMode", "Control"],
        ["Button Mode"] = ["CDefaultFilter", "CButtonMode", "CDasherButtons", "Control"],
        ["Direct Mode"] = ["CDefaultFilter", "Control"],
        ["Menu Mode"] = ["CDefaultFilter", "CButtonMode", "CDasherButtons", "Control"],
        ["One Button Mode"] = ["CDefaultFilter", "COneButtonFilter", "COneButtonDynamicFilter", "Control"],
        ["One Button Dynamic Mode"] = ["CDefaultFilter", "COneButtonDynamicFilter", "Control"],
        ["Two Button Mode"] = ["CDefaultFilter", "CTwoButtonDynamicFilter", "Control"],
        ["Two Button Dynamic Mode"] = ["CDefaultFilter", "CTwoButtonDynamicFilter", "Control"],
        ["Two Push Dynamic Mode"] = ["CDefaultFilter", "CTwoPushDynamicFilter", "Control"],
        ["Smoothing Mode"] = ["CDefaultFilter", "CSmoothingFilter", "Control"],
        ["Stylus Control"] = ["CDefaultFilter", "CStylusFilter", "Control"],
    };

    private List<ParameterDisplayInfo> FilterByActiveInputFilter(List<ParameterDisplayInfo> parameters)
    {
        var filterPtr = NativeBridge.dasher_get_string_parameter(_handle, ParameterKeys.SP_INPUT_FILTER);
        var currentFilter = filterPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(filterPtr) ?? "" : "";

        var activeSubgroups = FilterToSubgroup.GetValueOrDefault(currentFilter, new HashSet<string>());

        return parameters.Where(p =>
        {
            if (string.IsNullOrEmpty(p.Subgroup)) return true;
            return activeSubgroups.Contains(p.Subgroup);
        }).ToList();
    }

    private List<ParameterDisplayInfo> FilterByActiveLanguageModel(List<ParameterDisplayInfo> parameters)
    {
        var lmId = NativeBridge.dasher_get_language_model_id(_handle);
        var paramCount = NativeBridge.dasher_get_language_model_param_count(lmId);
        var lmKeys = new HashSet<int>();
        for (int i = 0; i < paramCount; i++)
            lmKeys.Add(NativeBridge.dasher_get_language_model_param_key(lmId, i));

        lmKeys.Add(ParameterKeys.BP_LM_ADAPTIVE);
        lmKeys.Add(NativeBridge.dasher_find_parameter_key("LP_LANGUAGE_MODEL_ID"));

        return parameters.Where(p =>
        {
            if (p.Subgroup != "Learning") return true;
            return lmKeys.Contains(p.Key);
        }).ToList();
    }

    private void LoadParameterGroups()
    {
        _groups.Clear();
        int count = NativeBridge.dasher_get_parameter_count();
        for (int i = 0; i < count; i++)
        {
            if (NativeBridge.dasher_get_parameter_info(i, out var raw) != 0) continue;

            var group = Marshal.PtrToStringUTF8(raw.Group) ?? "";
            if (string.IsNullOrEmpty(group))
                group = "Input";

            var info = new ParameterDisplayInfo
            {
                Key = raw.Key,
                Name = Marshal.PtrToStringUTF8(raw.Name) ?? "",
                Desc = Marshal.PtrToStringUTF8(raw.Desc) ?? "",
                Type = raw.Type,
                UiType = raw.UiType,
                MinVal = raw.MinVal,
                MaxVal = raw.MaxVal,
                Step = raw.Step,
                Advanced = raw.Advanced,
                Group = group,
                Subgroup = Marshal.PtrToStringUTF8(raw.Subgroup) ?? "",
            };

            if (!_groups.TryGetValue(info.Group, out var list))
            {
                list = new List<ParameterDisplayInfo>();
                _groups[info.Group] = list;
            }
            list.Add(info);
        }
    }

    private static bool IsColourPaletteParameter(ParameterDisplayInfo info)
    {
        var name = info.Name.ToLowerInvariant();
        return name.Contains("colour") && name.Contains("palette") ||
               name.Contains("color") && name.Contains("palette");
    }

    private Control? BuildPaletteSwatchPicker()
    {
        if (_handle == IntPtr.Zero) return null;

        var paletteCount = NativeBridge.dasher_get_palette_count(_handle);
        if (paletteCount == 0) return null;

        var currentPalettePtr = NativeBridge.dasher_get_current_palette(_handle);
        var currentPalette = currentPalettePtr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(currentPalettePtr) ?? ""
            : "";

        var section = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 12) };

        var header = new TextBlock
        {
            Text = "Colour Theme",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
        };
        section.Children.Add(header);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };

        var colors = new int[4];
        for (int i = 0; i < paletteCount; i++)
        {
            var namePtr = NativeBridge.dasher_get_palette_name(_handle, i);
            var name = namePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(namePtr) ?? "" : $"Palette {i}";
            NativeBridge.dasher_get_palette_preview_colors(_handle, i, colors);

            var isSelected = name == currentPalette;

            var card = new Button
            {
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = name,
            };

            var cardContent = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };

            var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            for (int c = 0; c < 4; c++)
            {
                var swatch = new Border
                {
                    Width = 16,
                    Height = 24,
                    CornerRadius = new CornerRadius(2),
                    Background = ArgbToBrush(colors[c]),
                };
                swatchRow.Children.Add(swatch);
            }

            var swatchContainer = new Border
            {
                Child = swatchRow,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x53, 0x7F))
                    : new SolidColorBrush(Color.FromArgb(0x4D, 0x80, 0x80, 0x80)),
                Padding = new Thickness(1),
            };
            cardContent.Children.Add(swatchContainer);

            var nameLabel = new TextBlock
            {
                Text = name,
                FontSize = 9,
                Foreground = isSelected
                    ? new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75))
                    : new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            cardContent.Children.Add(nameLabel);

            card.Content = cardContent;
            card.Width = 80;

            card.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is string paletteName)
                    NativeBridge.dasher_set_palette(_handle, paletteName);
            };

            strip.Children.Add(card);
        }

        scroll.Content = strip;
        section.Children.Add(scroll);
        return section;
    }

    private static SolidColorBrush ArgbToBrush(int argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        if (a == 0) a = 255;
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    private Control? BuildParameterRow(ParameterDisplayInfo info)
    {
        var labelText = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : info.Desc;

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var label = new TextBlock
        {
            Text = labelText,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(label, info.Desc);
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        var type = (ParameterType)info.Type;
        var uiType = (UIControlType)info.UiType;

        // String parameters with Enum UI type: use string dropdown from engine values
        if (type == ParameterType.String)
        {
            if (IsColourPaletteParameter(info))
                return null; // rendered as swatch picker

            if (IsFontParameter(info))
                return BuildRowWithEditor(info, row, BuildFontDropdown(info));

            if (uiType == UIControlType.Enum)
                return BuildRowWithEditor(info, row, BuildStringDropdown(info));

            return BuildRowWithEditor(info, row, BuildTextField(info));
        }

        // Bool/Long parameters
        Control? editor = uiType switch
        {
            UIControlType.Switch => BuildSwitch(info),
            UIControlType.Slider => BuildSlider(info),
            UIControlType.Step => BuildStep(info),
            UIControlType.Enum => BuildEnum(info),
            UIControlType.TextField => BuildTextField(info),
            _ => type switch
            {
                ParameterType.Bool => BuildSwitch(info),
                ParameterType.Long => BuildStep(info),
                _ => null,
            },
        };

        if (editor == null) return null;

        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(editor);

        return row;
    }

    private static Control? BuildRowWithEditor(ParameterDisplayInfo info, DockPanel row, Control? editor)
    {
        if (editor == null) return null;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(editor);
        return row;
    }

    private static bool IsFontParameter(ParameterDisplayInfo info)
    {
        return info.Name.Equals("Dasher Font", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] DasherFontPresets =
    [
        "System", "Arial", "Calibri", "Cambria", "Comic Sans MS",
        "Consolas", "Courier New", "Georgia", "Segoe UI", "Tahoma",
        "Times New Roman", "Trebuchet MS", "Verdana",
    ];

    private Control BuildFontDropdown(ParameterDisplayInfo info)
    {
        var currentPtr = NativeBridge.dasher_get_string_parameter(_handle, info.Key);
        var current = Marshal.PtrToStringUTF8(currentPtr) ?? "";
        var mapped = DasherFontPresets.Contains(current) ? current : "System";

        var comboBox = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };

        foreach (var font in DasherFontPresets)
            comboBox.Items.Add(font);
        comboBox.SelectedIndex = Array.IndexOf(DasherFontPresets, mapped);
        if (comboBox.SelectedIndex < 0) comboBox.SelectedIndex = 0;

        comboBox.SelectionChanged += (s, e) =>
        {
            if (comboBox.SelectedIndex >= 0 && comboBox.SelectedIndex < DasherFontPresets.Length)
            {
                var font = DasherFontPresets[comboBox.SelectedIndex];
                NativeBridge.dasher_set_string_parameter(_handle, info.Key, font == "System" ? "" : font);
            }
        };

        return comboBox;
    }

    private Control BuildSwitch(ParameterDisplayInfo info)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = NativeBridge.dasher_get_bool_parameter(_handle, info.Key) != 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        toggle.IsCheckedChanged += (s, e) =>
        {
            NativeBridge.dasher_set_bool_parameter(_handle, info.Key, toggle.IsChecked == true ? 1 : 0);
        };

        return toggle;
    }

    private Control BuildSlider(ParameterDisplayInfo info)
    {
        var current = NativeBridge.dasher_get_long_parameter(_handle, info.Key);
        var min = info.MinVal;
        var max = info.MaxVal > info.MinVal ? info.MaxVal : 1000;
        var step = info.Step > 0 ? info.Step : 1;

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = current,
            SmallChange = step,
            LargeChange = step * 5,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var valueText = new TextBlock
        {
            Text = current.ToString(),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 48,
            TextAlignment = TextAlignment.Center,
        };

        slider.ValueChanged += (s, e) =>
        {
            var val = (int)Math.Round(slider.Value);
            NativeBridge.dasher_set_long_parameter(_handle, info.Key, val);
            valueText.Text = val.ToString();
        };

        stack.Children.Add(slider);
        stack.Children.Add(valueText);
        return stack;
    }

    private Control BuildStep(ParameterDisplayInfo info)
    {
        var current = NativeBridge.dasher_get_long_parameter(_handle, info.Key);
        var min = info.MinVal;
        var max = info.MaxVal > info.MinVal ? info.MaxVal : 9999;
        var step = info.Step > 0 ? info.Step : 1;

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var downBtn = new Button
        {
            Content = "\u2212",
            Width = 28, Height = 28,
            FontSize = 14, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var valueBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xF2, 0xF1)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            MinWidth = 50,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = current.ToString(),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        var upBtn = new Button
        {
            Content = "+",
            Width = 28, Height = 28,
            FontSize = 14, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        int val = current;
        void UpdateDisplay()
        {
            (valueBorder.Child as TextBlock)!.Text = val.ToString();
            NativeBridge.dasher_set_long_parameter(_handle, info.Key, val);
        }

        downBtn.Click += (s, e) =>
        {
            val = Math.Max(min, val - step);
            UpdateDisplay();
        };

        upBtn.Click += (s, e) =>
        {
            val = Math.Min(max, val + step);
            UpdateDisplay();
        };

        stack.Children.Add(downBtn);
        stack.Children.Add(valueBorder);
        stack.Children.Add(upBtn);
        return stack;
    }

    private Control BuildEnum(ParameterDisplayInfo info)
    {
        var enumCount = NativeBridge.dasher_get_parameter_enum_count(info.Key);
        if (enumCount == 0) return BuildStep(info);

        var comboBox = new ComboBox
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };

        var currentLong = NativeBridge.dasher_get_long_parameter(_handle, info.Key);
        int selectedIndex = -1;

        for (int i = 0; i < enumCount; i++)
        {
            var namePtr = NativeBridge.dasher_get_parameter_enum_name(info.Key, i);
            var name = Marshal.PtrToStringUTF8(namePtr) ?? "";
            var value = NativeBridge.dasher_get_parameter_enum_value(info.Key, i);

            comboBox.Items.Add(new ComboBoxItem { Content = name, Tag = value });
            if (value == currentLong) selectedIndex = i;
        }

        if (selectedIndex >= 0) comboBox.SelectedIndex = selectedIndex;

        comboBox.SelectionChanged += (s, e) =>
        {
            if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is int val)
                NativeBridge.dasher_set_long_parameter(_handle, info.Key, val);
        };

        return comboBox;
    }

    private Control BuildTextField(ParameterDisplayInfo info)
    {
        var type = (ParameterType)info.Type;
        if (type == ParameterType.String)
        {
            var currentPtr = NativeBridge.dasher_get_string_parameter(_handle, info.Key);
            var current = Marshal.PtrToStringUTF8(currentPtr) ?? "";

            if ((UIControlType)info.UiType == UIControlType.Enum)
                return BuildStringDropdown(info);

            var textBox = new TextBox
            {
                Text = current,
                MinWidth = 200,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            textBox.LostFocus += (s, e) =>
            {
                NativeBridge.dasher_set_string_parameter(_handle, info.Key, textBox.Text ?? "");
            };

            return textBox;
        }

        return BuildStep(info);
    }

    private Control BuildStringDropdown(ParameterDisplayInfo info)
    {
        var currentPtr = NativeBridge.dasher_get_string_parameter(_handle, info.Key);
        var current = Marshal.PtrToStringUTF8(currentPtr) ?? "";

        var comboBox = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };

        var names = new List<string>();
        const int maxValues = 200;
        var ptrs = new IntPtr[maxValues];
        int count = NativeBridge.dasher_get_parameter_string_values(_handle, info.Key, ptrs, maxValues);

        int selectedIndex = -1;
        for (int i = 0; i < count; i++)
        {
            var name = Marshal.PtrToStringUTF8(ptrs[i]) ?? "";
            names.Add(name);
            comboBox.Items.Add(name);
            if (name == current) selectedIndex = i;
        }

        if (selectedIndex >= 0) comboBox.SelectedIndex = selectedIndex;

        comboBox.SelectionChanged += (s, e) =>
        {
            if (comboBox.SelectedIndex >= 0 && comboBox.SelectedIndex < names.Count)
                NativeBridge.dasher_set_string_parameter(_handle, info.Key, names[comboBox.SelectedIndex]);
        };

        return comboBox;
    }

    private void BuildSpeechSettings()
    {
        var svc = SpeechService.Instance;
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75));
        var valueBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70));

        var engineLabel = new TextBlock
        {
            Text = "TTS Engine",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var engineCombo = new ComboBox
        {
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
        };
        foreach (var eng in SpeechService.EngineNames)
            engineCombo.Items.Add(SpeechService.EngineDisplayName(eng));
        engineCombo.SelectedIndex = Array.IndexOf(SpeechService.EngineNames, svc.SelectedEngine);
        if (engineCombo.SelectedIndex < 0) engineCombo.SelectedIndex = 0;

        var credentialsPanel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        void RebuildCredentials()
        {
            credentialsPanel.Children.Clear();
            var keys = SpeechService.RequiredCredentialKeys(svc.SelectedEngine);
            if (keys.Length == 0)
            {
                credentialsPanel.Children.Add(new TextBlock
                {
                    Text = "No credentials required",
                    FontSize = 11,
                    Foreground = valueBrush,
                });
                return;
            }

            foreach (var key in keys)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
                var lbl = new TextBlock
                {
                    Text = key,
                    FontSize = 12,
                    Foreground = labelBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 140,
                };
                DockPanel.SetDock(lbl, Dock.Left);
                row.Children.Add(lbl);

                var isSecret = key.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase);

                var textBox = new TextBox
                {
                    Text = svc.Credentials.GetValueOrDefault(key, ""),
                    MinWidth = 200,
                    FontSize = 12,
                    PasswordChar = isSecret ? '•' : '\0',
                };
                textBox.LostFocus += (s, e) =>
                {
                    svc.Credentials[key] = textBox.Text ?? "";
                    svc.InvalidateClient();
                    svc.SaveSettings();
                };
                row.Children.Add(textBox);
                credentialsPanel.Children.Add(row);
            }
        }

        engineCombo.SelectionChanged += (s, e) =>
        {
            var idx = engineCombo.SelectedIndex;
            if (idx >= 0 && idx < SpeechService.EngineNames.Length)
            {
                svc.SelectedEngine = SpeechService.EngineNames[idx];
                svc.InvalidateClient();
                svc.SaveSettings();
                RebuildCredentials();
            }
        };

        RebuildCredentials();

        var engineRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(engineLabel, Dock.Left);
        engineRow.Children.Add(engineLabel);
        engineRow.Children.Add(engineCombo);

        _panel.Children.Add(engineRow);
        _panel.Children.Add(credentialsPanel);

        var sep = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0xDE, 0xE2, 0xE6)),
            Margin = new Thickness(0, 12, 0, 8),
        };
        _panel.Children.Add(sep);

        var voiceHeader = new StackPanel { Spacing = 6 };

        var voiceBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };

        var loadVoicesBtn = new Button
        {
            Content = "Load Voices",
            FontSize = 12,
        };
        voiceBtnRow.Children.Add(loadVoicesBtn);

        var voiceCountLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = valueBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        voiceBtnRow.Children.Add(voiceCountLabel);

        var previewBtn = new Button
        {
            Content = "Preview",
            FontSize = 12,
        };
        voiceBtnRow.Children.Add(previewBtn);

        voiceHeader.Children.Add(voiceBtnRow);

        var voiceCombo = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
        };
        voiceHeader.Children.Add(voiceCombo);

        var rateLabel = new TextBlock
        {
            Text = "Rate",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
            Margin = new Thickness(0, 8, 0, 0),
        };
        voiceHeader.Children.Add(rateLabel);

        var rateCombo = new ComboBox
        {
            MinWidth = 150,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var rateNames = new[] { "Very Slow", "Slow", "Medium", "Fast", "Very Fast" };
        var rateValues = Enum.GetValues<DotNetTtsWrapper.Models.SpeechRate>();
        foreach (var r in rateNames)
            rateCombo.Items.Add(r);
        rateCombo.SelectedIndex = Array.IndexOf(rateValues, svc.SpeechRate);
        if (rateCombo.SelectedIndex < 0) rateCombo.SelectedIndex = 2;
        voiceHeader.Children.Add(rateCombo);

        var pitchLabel = new TextBlock
        {
            Text = "Pitch",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
            Margin = new Thickness(0, 8, 0, 0),
        };
        voiceHeader.Children.Add(pitchLabel);

        var pitchCombo = new ComboBox
        {
            MinWidth = 150,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var pitchNames = new[] { "Very Low", "Low", "Medium", "High", "Very High" };
        var pitchValues = Enum.GetValues<DotNetTtsWrapper.Models.SpeechPitch>();
        foreach (var p in pitchNames)
            pitchCombo.Items.Add(p);
        pitchCombo.SelectedIndex = Array.IndexOf(pitchValues, svc.SpeechPitch);
        if (pitchCombo.SelectedIndex < 0) pitchCombo.SelectedIndex = 2;
        voiceHeader.Children.Add(pitchCombo);

        var volLabel = new TextBlock
        {
            Text = "Volume",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = labelBrush,
            Margin = new Thickness(0, 8, 0, 0),
        };
        voiceHeader.Children.Add(volLabel);

        var volSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = svc.SpeechVolume,
            SmallChange = 5,
            LargeChange = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var volRow = new DockPanel();
        var volValue = new TextBlock
        {
            Text = $"{svc.SpeechVolume}%",
            FontSize = 11,
            Foreground = valueBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 40,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(volValue, Dock.Right);
        volRow.Children.Add(volValue);
        volRow.Children.Add(volSlider);
        voiceHeader.Children.Add(volRow);

        _panel.Children.Add(voiceHeader);

        loadVoicesBtn.Click += async (s, e) =>
        {
            loadVoicesBtn.IsEnabled = false;
            voiceCountLabel.Text = "Loading...";
            try
            {
                await svc.LoadVoicesAsync();
                voiceCountLabel.Text = svc.AvailableVoices.Count > 0
                    ? $"{svc.AvailableVoices.Count} voices"
                    : "No voices found";
                voiceCombo.Items.Clear();
                voiceCombo.Items.Add("(Default)");
                foreach (var v in svc.AvailableVoices)
                    voiceCombo.Items.Add(v.Name);
                if (!string.IsNullOrEmpty(svc.SelectedVoiceId))
                {
                    var idx = svc.AvailableVoices.FindIndex(v => v.Id == svc.SelectedVoiceId);
                    voiceCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
                }
                else voiceCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                voiceCountLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                loadVoicesBtn.IsEnabled = true;
            }
        };

        voiceCombo.SelectionChanged += (s, e) =>
        {
            if (voiceCombo.SelectedIndex <= 0)
                svc.SelectedVoiceId = null;
            else if (voiceCombo.SelectedIndex - 1 < svc.AvailableVoices.Count)
                svc.SelectedVoiceId = svc.AvailableVoices[voiceCombo.SelectedIndex - 1].Id;
            svc.SaveSettings();
        };

        previewBtn.Click += (s, e) =>
        {
            if (svc.IsSpeaking)
                svc.Stop();
            else
                _ = svc.SpeakAsync("Hello, this is a preview of the selected voice.");
        };

        rateCombo.SelectionChanged += (s, e) =>
        {
            if (rateCombo.SelectedIndex >= 0 && rateCombo.SelectedIndex < rateValues.Length)
            {
                svc.SpeechRate = rateValues[rateCombo.SelectedIndex];
                svc.SaveSettings();
            }
        };

        pitchCombo.SelectionChanged += (s, e) =>
        {
            if (pitchCombo.SelectedIndex >= 0 && pitchCombo.SelectedIndex < pitchValues.Length)
            {
                svc.SpeechPitch = pitchValues[pitchCombo.SelectedIndex];
                svc.SaveSettings();
            }
        };

        volSlider.ValueChanged += (s, e) =>
        {
            svc.SpeechVolume = (int)Math.Round(volSlider.Value);
            volValue.Text = $"{svc.SpeechVolume}%";
            svc.SaveSettings();
        };

        var sep2 = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0xDE, 0xE2, 0xE6)),
            Margin = new Thickness(0, 12, 0, 8),
        };
        _panel.Children.Add(sep2);

        var speakOnStopLabel = new TextBlock
        {
            Text = "Engine Speech Features",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = labelBrush,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _panel.Children.Add(speakOnStopLabel);

        var speakOnStopRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var speakOnStopLabel2 = new TextBlock
        {
            Text = "Speak on stop",
            FontSize = 12,
            Foreground = labelBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 180,
        };
        DockPanel.SetDock(speakOnStopLabel2, Dock.Left);
        speakOnStopRow.Children.Add(speakOnStopLabel2);

        var speakOnStopToggle = new ToggleSwitch
        {
            IsChecked = NativeBridge.dasher_get_bool_parameter(_handle, ParameterKeys.BP_SPEAK_ALL_ON_STOP) != 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        speakOnStopToggle.IsCheckedChanged += (s, e) =>
        {
            NativeBridge.dasher_set_bool_parameter(_handle, ParameterKeys.BP_SPEAK_ALL_ON_STOP, speakOnStopToggle.IsChecked == true ? 1 : 0);
        };
        speakOnStopRow.Children.Add(speakOnStopToggle);
        _panel.Children.Add(speakOnStopRow);

        var speakWordsRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var speakWordsLabel = new TextBlock
        {
            Text = "Speak words",
            FontSize = 12,
            Foreground = labelBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 180,
        };
        DockPanel.SetDock(speakWordsLabel, Dock.Left);
        speakWordsRow.Children.Add(speakWordsLabel);

        var speakWordsToggle = new ToggleSwitch
        {
            IsChecked = NativeBridge.dasher_get_bool_parameter(_handle, ParameterKeys.BP_SPEAK_WORDS) != 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        speakWordsToggle.IsCheckedChanged += (s, e) =>
        {
            NativeBridge.dasher_set_bool_parameter(_handle, ParameterKeys.BP_SPEAK_WORDS, speakWordsToggle.IsChecked == true ? 1 : 0);
        };
        speakWordsRow.Children.Add(speakWordsToggle);
        _panel.Children.Add(speakWordsRow);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _scrollViewer.Measure(availableSize);
        return _scrollViewer.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _scrollViewer.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

internal enum ParameterType
{
    Bool = 0,
    Long = 1,
    String = 2,
    Invalid = -1,
}

internal enum UIControlType
{
    Switch = 0,
    TextField = 1,
    Slider = 2,
    Enum = 3,
    Step = 4,
    None = 5,
}

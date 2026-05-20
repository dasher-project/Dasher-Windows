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
        var names = new List<string>(_groups.Keys);
        names.Sort((a, b) =>
        {
            var order = new Dictionary<string, int>
            {
                ["Input"] = 0, ["Language"] = 1, ["Appearance"] = 2,
                ["Speed"] = 3, ["Output"] = 4, ["Advanced"] = 5, ["Other"] = 6,
            };
            int oa = order.TryGetValue(a, out var va) ? va : 99;
            int ob = order.TryGetValue(b, out var vb) ? vb : 99;
            return oa.CompareTo(ob);
        });
        return names;
    }

    public void ShowCategory(string category)
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

        if (!_groups.TryGetValue(category, out var parameters)) return;

        if (category == "Input")
            parameters = FilterByActiveInputFilter(parameters);

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
                    var row = BuildParameterRow(info);
                    if (row != null)
                        _panel.Children.Add(row);
                }
                catch { }
            }
        }
    }

    public event EventHandler? BackRequested;
    public event EventHandler<Dasher.Windows.Engine.EyeGazeIntegration.TrackerType>? InputSourceChanged;

    private Control? BuildInputSourceRow()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var label = new TextBlock
        {
            Text = "Input Source",
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
            SelectedIndex = 0,
        };
        combo.Items.Add("Mouse");
        combo.Items.Add("Eye Tracker (UDP)");
        combo.Items.Add("Eye Tracker (Windows Native)");

        combo.SelectionChanged += (s, e) =>
        {
            var trackerType = combo.SelectedIndex switch
            {
                1 => Dasher.Windows.Engine.EyeGazeIntegration.TrackerType.UdpGazeTracker,
                2 => Dasher.Windows.Engine.EyeGazeIntegration.TrackerType.WindowsNative,
                _ => Dasher.Windows.Engine.EyeGazeIntegration.TrackerType.None,
            };
            InputSourceChanged?.Invoke(this, trackerType);
        };

        row.Children.Add(combo);
        return row;
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
        ["Normal Control"] = ["CDefaultFilter", "CDynamicFilter", "CDynamicButtons"],
        ["Click Mode"] = ["CDefaultFilter", "CClickFilter"],
        ["Compass Mode"] = ["CDefaultFilter", "CCompassMode"],
        ["Button Mode"] = ["CDefaultFilter", "CButtonMode", "CDasherButtons"],
        ["Direct Mode"] = ["CDefaultFilter"],
        ["Menu Mode"] = ["CDefaultFilter", "CButtonMode", "CDasherButtons"],
        ["One Button Mode"] = ["CDefaultFilter", "COneButtonFilter", "COneButtonDynamicFilter"],
        ["Two Button Mode"] = ["CDefaultFilter", "CTwoButtonDynamicFilter"],
        ["Two Push Mode"] = ["CDefaultFilter", "CTwoPushDynamicFilter"],
        ["Smoothing Mode"] = ["CDefaultFilter", "CSmoothingFilter"],
        ["Stylus Mode"] = ["CDefaultFilter", "CStylusFilter"],
        ["Static Mode"] = ["CDefaultFilter", "CStaticFilter"],
        ["Multi-Press Mode"] = ["CDefaultFilter", "CButtonMultiPress"],
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
                ParameterType.String => BuildTextField(info),
                _ => null,
            },
        };

        if (editor == null) return null;

        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(editor);

        return row;
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

            if (info.Key == ParameterKeys.SP_ALPHABET_ID || info.Key == ParameterKeys.SP_COLOUR_ID
                || info.Key == ParameterKeys.SP_INPUT_FILTER || info.Key == ParameterKeys.SP_INPUT_DEVICE)
            {
                return BuildStringDropdown(info, current);
            }

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

    private Control BuildStringDropdown(ParameterDisplayInfo info, string current)
    {
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
    None = 0,
    Switch = 1,
    Slider = 2,
    Step = 3,
    Enum = 4,
    TextField = 5,
}

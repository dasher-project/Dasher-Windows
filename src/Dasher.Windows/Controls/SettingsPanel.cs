using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Dasher.Windows.Engine;
using Dasher.Windows.EyeGaze;
using Dasher.Windows.Services;
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

public class SettingsPanel : Decorator
{
    private IntPtr _handle;
    private readonly StackPanel _panel;
    private string _currentCategory = "";
    private readonly Dictionary<string, List<ParameterDisplayInfo>> _groups = new();

    // Theme-aware brushes — read ActualThemeVariant directly instead of relying
    // on FindResource (which doesn't resolve ThemeDictionaries reliably)
    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;
    private IBrush BrushLabel => IsDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x30));
    private IBrush BrushValue => IsDark ? new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0)) : new SolidColorBrush(Color.FromRgb(0x3D, 0x43, 0x50));
    private IBrush BrushMuted => IsDark ? new SolidColorBrush(Color.FromRgb(0xA8, 0xB2, 0xBC)) : new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x6F));
    private IBrush BrushControlBg => IsDark ? new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xED));
    private IBrush BrushBorder => IsDark ? new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xE8));
    private IBrush BrushHighlight => IsDark ? new SolidColorBrush(Color.FromRgb(0x1D, 0x2D, 0x35)) : new SolidColorBrush(Color.FromRgb(0xE9, 0xF2, 0xF1));
    private IBrush BrushOnAccent => new SolidColorBrush(Color.FromRgb(0x0A, 0x2A, 0x3D));

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

        // Decorator hosts a single child with framework-owned measure/arrange —
        // no custom layout code. Scrolling is owned entirely by the ScrollViewer
        // that wraps this panel in MainWindow.axaml.
        Child = _panel;
    }

    public void Initialize(IntPtr handle)
    {
        _handle = handle;
        LoadParameterGroups();
    }

    public List<string> GetCategoryNames()
    {
        var names = new List<string>(_groups.Keys) { "Speech", "Privacy" };
        names.Sort((a, b) =>
        {
            var order = new Dictionary<string, int>
            {
                ["Input"] = 0, ["Language"] = 1, ["Customization"] = 2,
                ["Speed"] = 3, ["Output"] = 4, ["Speech"] = 5,
                ["Advanced"] = 6, ["Other"] = 7, ["Appearance"] = 8,
                ["Privacy"] = 99,
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
        Title = LocalizeCategory(category);

        _panel.Children.Clear();

        if (category == "Input")
        {
            var inputSourceRow = BuildInputSourceRow();
            if (inputSourceRow != null)
                _panel.Children.Add(inputSourceRow);
        }

        if (category == "Language")
        {
            // No in-app locale picker: the UI follows the OS language
            // (Windows' per-app language override included) and Loc pushes
            // the resolved locale into the engine at startup.
            _panel.Children.Add(BuildTrainingSection());
        }

        if (category == "Speech")
        {
            BuildSpeechSettings();
            return;
        }

        if (category == "Privacy")
        {
            BuildPrivacySettings();
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

            _panel.Children.Add(BuildTypingRateSection());
        }

        if (!_groups.TryGetValue(category, out var parameters)) return;

        if (category == "Input")
            parameters = FilterByActiveInputFilter(parameters);

        // Filter out parameters that belong in other tabs or should be hidden
        parameters = parameters.Where(p => !ShouldHideParameter(p, category)).ToList();

        if (category == "Language")
            parameters = FilterByActiveLanguageModel(parameters);

        // Progressive disclosure (RFC 0006): Simple shows common params only;
        // the switch reveals advanced + expert tiers.
        var uiSettings = UiSettings.Load();
        if (!uiSettings.ShowAdvanced)
            parameters = parameters.Where(p => p.Advanced == 0).ToList();

        if (parameters.Count > 0)
            _panel.Children.Add(BuildSimpleAdvancedRow(uiSettings));

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
                    Foreground = BrushLabel,
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

    public event EventHandler<(EyeGazeIntegration.TrackerType trackerType, int udpPort)>? InputSourceChanged;
    public event EventHandler? JoystickRequested;
    public event Action<string, int>? OutputFontChanged;
    public event Action? ResetSettingsRequested;
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

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };

        var fontLabel = new TextBlock
        {
            Text = Loc.Tr("output_font"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Text = Loc.Tr("output_font_size"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(sizeLabel);

        var sizeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var sizeDown = new Button { Content = "\u2212", Width = 28, Height = 28, FontSize = 14, FontWeight = FontWeight.Bold };
        var sizeValue = new Border
        {
            Background = BrushHighlight,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            MinWidth = 50,
            Child = new TextBlock
            {
                Text = settings.FontSize.ToString(),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = BrushValue,
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

        sizeDown.Click += (s, e) =>
        {
            var before = fontSize;
            fontSize = Math.Max(8, fontSize - 1);
            if (fontSize == before) return;
            UpdateFontSize();
            AnalyticsService.CaptureSettingChanged("output_font_size", "number", fontSize);
        };
        sizeUp.Click += (s, e) =>
        {
            var before = fontSize;
            fontSize = Math.Min(72, fontSize + 1);
            if (fontSize == before) return;
            UpdateFontSize();
            AnalyticsService.CaptureSettingChanged("output_font_size", "number", fontSize);
        };

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
                AnalyticsService.CaptureSettingChanged("output_font_family", "enum", settings.FontFamily);
                OutputFontChanged?.Invoke(settings.FontFamily, fontSize);
            }
        };

        return panel;
    }

    private Control? BuildKeyboardTransparencyRow()
    {
        var settings = OutputTextSettings.Load();

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };

        var label = new TextBlock
        {
            Text = Loc.Tr("keyboard_mode_opacity"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Foreground = BrushValue,
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

        // Commit-time setting_changed (drag release / keyboard), not per
        // tick. The slider works in FRACTIONS (0.2–1.0); report percent.
        AttachSliderCommit(slider, (int)(settings.KeyboardOpacity * 100),
            pct => AnalyticsService.CaptureSettingChanged("keyboard_mode_opacity", "number", pct),
            v => (int)Math.Round(v * 100));

        sliderRow.Children.Add(slider);
        sliderRow.Children.Add(valueLabel);
        panel.Children.Add(sliderRow);

        var help = new TextBlock
        {
            Text = Loc.Tr("keyboard_mode_transparency_help"),
            FontSize = 11,
            Foreground = BrushMuted,
        };
        panel.Children.Add(help);

        return panel;
    }

    private Control BuildTypingRateSection()
    {
        var settings = OutputTextSettings.Load();
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tr("typing_rate", "Typing rate"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushLabel,
        });

        var toggleRow = BuildToggleRow("Show WPM in status bar", settings.ShowTypingRate, isChecked =>
        {
            settings.ShowTypingRate = isChecked;
            settings.Save();
            AnalyticsService.CaptureSettingChanged("show_typing_rate", "bool", isChecked);
            TypingRateVisibilityChanged?.Invoke(settings.ShowTypingRate);
        });
        toggleRow.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(toggleRow);

        var resetBtn = new Button
        {
            Content = Loc.Tr("reset_wpm_averages"),
            Padding = new Thickness(12, 6),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Background = BrushControlBg,
            Foreground = BrushLabel,
            BorderThickness = new Thickness(0),
        };
        resetBtn.Click += (s, e) => ResetTypingStatsRequested?.Invoke();
        panel.Children.Add(resetBtn);

        return panel;
    }

    public event Action<bool>? TypingRateVisibilityChanged;
    public event Action? ResetTypingStatsRequested;

    private bool _populatingSelections;
    private bool _populatingVoices;
    private int _voiceLoadDepth;

    /// <summary>
    /// RFC 0001 2026-10-28: setting_changed for a slider on COMMIT — drag
    /// release or keyboard adjust (arrows/PageUp/PageDown/Home/End all move
    /// Avalonia sliders) — deduped against the last committed value. A bare
    /// ValueChanged handler fires per drag tick. Optional transform maps the
    /// slider scale to the reported scale (e.g. 0.2–1.0 fraction → percent).
    /// </summary>
    private static void AttachSliderCommit(Avalonia.Controls.Slider slider, int initial, Action<int> emit,
        Func<double, int>? transform = null)
    {
        int last = initial;
        void Commit()
        {
            var v = transform != null ? transform(slider.Value) : (int)Math.Round(slider.Value);
            if (v == last) return;
            last = v;
            emit(v);
        }
        slider.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
            (_, _) => Commit(), Avalonia.Interactivity.RoutingStrategies.Bubble);
        slider.KeyUp += (s, e) =>
        {
            if (e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up
                or Avalonia.Input.Key.Down or Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown
                or Avalonia.Input.Key.Home or Avalonia.Input.Key.End)
                Commit();
        };
    }

    private Control? BuildInputSourceRow()
    {
        var panel = new StackPanel { Spacing = 8 };

        var config = AccessConfiguration.Load();

        var methodLabel = new TextBlock
        {
            Text = Loc.Tr("steering_method"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Text = Loc.Tr("selection_method"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            // Auto-populated entries are NOT user commits: guard the
            // selection handler's telemetry while the combo is rebuilt
            // (review: changing Steering Method fired a false selection_method
            // event for the app-picked default).
            _populatingSelections = true;
            try
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
            finally { _populatingSelections = false; }
        }

        PopulateSelections(config.Method);

        var subtitle = new TextBlock
        {
            FontSize = 11,
            Foreground = BrushMuted,
            Text = config.Selection.Subtitle(),
            Margin = new Thickness(0, 2, 0, 0),
        };

        // ── Eye tracker sub-picker (visible only when EyeGaze is selected) ──────────
        var trackerLabel = new TextBlock
        {
            Text = Loc.Tr("eye_tracker_device"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
        trackerCombo.Items.Add("eyetuitive");
        trackerCombo.Items.Add("Tobii (Stream Engine)");
        trackerCombo.SelectedIndex = config.EyeTrackerType switch
        {
            "UdpGaze" => 1,
            "Eyetuitive" => 2,
            "Tobii" => 3,
            _ => 0,
        };

        var portPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var portLabel = new TextBlock
        {
            Text = Loc.Tr("udp_port"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Foreground = BrushMuted,
            Text = Loc.Tr("udp_tracker_help"),
            Margin = new Thickness(0, 2, 0, 0),
        };

        // ── Gaze filter controls (transit zoom fix; tune for eye tracker latency) ──
        var filterHeader = new TextBlock
        {
            Text = Loc.Tr("gaze_smoothing"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var filterToggle = new ToggleSwitch
        {
            IsChecked = config.GazeFilterEnabled,
            OnContent = "", OffContent = "", MinWidth = 0, Height = 24,
        };
        var filterToggleLabel = new TextBlock
        {
            Text = Loc.Tr("gaze_filter_label"),
            FontSize = 12,
            Foreground = BrushValue,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var filterTogglePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filterTogglePanel.Children.Add(filterToggle);
        filterTogglePanel.Children.Add(filterToggleLabel);

        var gazeFilterHelp = new TextBlock
        {
            Text = Loc.Tr("gaze_filter_help"),
            FontSize = 11,
            Foreground = BrushMuted,
            TextWrapping = TextWrapping.Wrap,
        };

        // Eye-tracker diagnostics toggle: detailed per-sample logging to
        // %APPDATA%\Dasher\eyegaze.log (off by default; lifecycle events
        // always logged). The file to attach to bug reports.
        var diagToggle = new ToggleSwitch
        {
            IsChecked = config.GazeDiagnostics,
            OnContent = "", OffContent = "", MinWidth = 0, Height = 24,
        };
        var diagToggleLabel = new TextBlock
        {
            Text = Loc.Tr("save_eye_tracker_diagnostics"),
            FontSize = 12,
            Foreground = BrushValue,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var diagTogglePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        diagTogglePanel.Children.Add(diagToggle);
        diagTogglePanel.Children.Add(diagToggleLabel);
        var diagHelp = new TextBlock
        {
            Text = Loc.Tr("eyegaze_verbose_help"),
            FontSize = 11,
            Foreground = BrushMuted,
            TextWrapping = TextWrapping.Wrap,
        };
        diagToggle.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name != "IsChecked") return;
            var cfg = AccessConfiguration.Load();
            cfg.GazeDiagnostics = diagToggle.IsChecked == true;
            cfg.Save();
            EyeGaze.EyeGazeLogger.Verbose = cfg.GazeDiagnostics;
        };

        var deadZoneSlider = new Slider
        {
            Minimum = 0, Maximum = 30, Value = config.GazeDeadZonePx,
            Width = 180, FontSize = 12,
        };
        var deadZoneLabel = new TextBlock { Text = $"Dead zone: {config.GazeDeadZonePx:F0} px", FontSize = 11, Foreground = BrushValue };
        deadZoneSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value")
                deadZoneLabel.Text = $"Dead zone: {deadZoneSlider.Value:F0} px";
        };

        var settleSlider = new Slider
        {
            Minimum = 0, Maximum = 300, Value = config.GazeSettleMs,
            Width = 180, FontSize = 12,
        };
        var settleLabel = new TextBlock { Text = $"Settle time: {config.GazeSettleMs:F0} ms", FontSize = 11, Foreground = BrushValue };
        settleSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value")
                settleLabel.Text = $"Settle time: {settleSlider.Value:F0} ms";
        };

        void SaveFilterConfig()
        {
            var cfg = AccessConfiguration.Load();
            cfg.GazeFilterEnabled = filterToggle.IsChecked ?? true;
            cfg.GazeDeadZonePx = deadZoneSlider.Value;
            cfg.GazeSettleMs = settleSlider.Value;
            cfg.Save();
            // Apply live if canvas is available
            var canvas = this.FindControl<DasherCanvas>("DasherCanvas");
            if (canvas != null)
            {
                if (cfg.GazeFilterEnabled)
                    canvas.ConfigureGazeFilter(cfg.GazeDeadZonePx, null, cfg.GazeSettleMs);
                else
                    canvas.ConfigureGazeFilter(); // disable
            }
        }

        // Debounced save on slider change
        Avalonia.Threading.DispatcherTimer? filterSaveTimer = null;
        void ScheduleFilterSave()
        {
            filterSaveTimer?.Stop();
            filterSaveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            filterSaveTimer.Tick += (_, _) => { filterSaveTimer!.Stop(); SaveFilterConfig(); };
            filterSaveTimer.Start();
        }

        deadZoneSlider.PropertyChanged += (s, e) => { if (e.Property.Name == "Value") ScheduleFilterSave(); };
        settleSlider.PropertyChanged += (s, e) => { if (e.Property.Name == "Value") ScheduleFilterSave(); };
        filterToggle.PropertyChanged += (s, e) => { if (e.Property.Name == "IsChecked") ScheduleFilterSave(); };

        void UpdateTrackerVisibility()
        {
            var isEyeGaze = methods[methodCombo.SelectedIndex] == AccessMethod.EyeGaze;
            trackerLabel.IsVisible = isEyeGaze;
            trackerCombo.IsVisible = isEyeGaze;
            var isUdp = isEyeGaze && trackerCombo.SelectedIndex == 1;
            portPanel.IsVisible = isUdp;
            trackerHelp.IsVisible = false;
            filterHeader.IsVisible = isEyeGaze;
            filterTogglePanel.IsVisible = isEyeGaze;
            gazeFilterHelp.IsVisible = isEyeGaze;
            diagTogglePanel.IsVisible = isEyeGaze;
            diagHelp.IsVisible = isEyeGaze;
            deadZoneSlider.IsVisible = isEyeGaze;
            deadZoneLabel.IsVisible = isEyeGaze;
            settleSlider.IsVisible = isEyeGaze;
            settleLabel.IsVisible = isEyeGaze;

            if (!isEyeGaze) return;

            if (trackerCombo.SelectedIndex == 1)
            {
                // UDP
                trackerHelp.Text = Loc.Tr("udp_tracker_help");
                trackerHelp.IsVisible = true;
            }
            else if (trackerCombo.SelectedIndex == 2)
            {
                // eyetuitive
                var available = EyetuitiveTracker.IsAvailable();
                trackerHelp.Text = available
                    ? "eyetuitive device detected"
                    : "No eyetuitive device detected — connect via USB and ensure service is running";
                trackerHelp.IsVisible = true;
            }
            else if (trackerCombo.SelectedIndex == 3)
            {
                // Tobii Stream Engine
                var available = TobiiStreamEngineTracker.IsAvailable();
                trackerHelp.Text = available
                    ? "Tobii Stream Engine DLL detected"
                    : "tobii_stream_engine.dll not found. Download it from developer.tobii.com and place next to Dasher.Windows.exe";
                trackerHelp.IsVisible = true;
            }
        }

        UpdateTrackerVisibility();

        // ── Dasher Input Filter (smoothing mode etc.) ─────────────────────────────
        var filterLabel = new TextBlock
        {
            Text = Loc.Tr("input_processing"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var filterCombo = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
        };

        // Curated subset of filters most useful for accessibility users.
        // The list is a UI convenience, not the truth: the engine can hold
        // any registered filter (Selection Method choices, v5 imports, the
        // raw parameter row). Silently snapping the display back to
        // "Normal Control" for those made the app LOOK like it had forgotten
        // the user's choice ("always defaults to Normal Control") while the
        // engine was actually running it — so append the live value.
        var filterOptions = new List<(string Name, string Help)>
        {
            ("Normal Control", "Standard continuous control (default)"),
            ("Smoothing Mode", "Input smoothing — reduces jitter for noisy input (eye gaze, head tracking)"),
            ("Direct Mode", "Direct pointer — minimal processing"),
        };

        var currentFilterPtr = NativeBridge.dasher_get_string_parameter(_handle, ParameterKeys.SP_INPUT_FILTER);
        var currentFilter = currentFilterPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(currentFilterPtr) ?? "Normal Control" : "Normal Control";
        if (!string.IsNullOrEmpty(currentFilter) &&
            filterOptions.All(f => f.Name != currentFilter))
            filterOptions.Add((currentFilter, "Active engine input filter (set via Selection Method or advanced settings)"));

        foreach (var (name, _) in filterOptions)
            filterCombo.Items.Add(name);
        filterCombo.SelectedIndex = filterOptions.FindIndex(f => f.Name == currentFilter);
        if (filterCombo.SelectedIndex < 0) filterCombo.SelectedIndex = 0;

        var filterHelp = new TextBlock
        {
            FontSize = 11,
            Foreground = BrushMuted,
            Text = filterOptions[filterCombo.SelectedIndex].Help,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        filterCombo.SelectionChanged += (s, e) =>
        {
            var idx = filterCombo.SelectedIndex;
            if (idx < 0 || idx >= filterOptions.Count) return;
            var newFilter = filterOptions[idx].Name;
            filterHelp.Text = filterOptions[idx].Help;
            // The engine saves immediately and owns the persisted value
            // (Apply no longer pushes at startup); access.json is kept as a
            // mirror of the last explicit choice for diagnostics.
            config.InputFilter = newFilter;
            config.Save();
            NativeBridge.dasher_set_string_parameter(_handle, ParameterKeys.SP_INPUT_FILTER, newFilter);
            AnalyticsService.CaptureSettingChanged("Input Filter", "enum", newFilter);
            // Rebuild the Input tab to show/hide filter-specific parameters
            ShowCategory(_currentCategory);
        };

        methodCombo.SelectionChanged += (s, e) =>
        {
            var method = methods[methodCombo.SelectedIndex];
            config.Method = method;
            AnalyticsService.CaptureSettingChanged("access_method", "enum", method.DisplayName());
            PopulateSelections(method);
            var sel = method.ValidFor();
            config.Selection = sel[selectionCombo.SelectedIndex];
            subtitle.Text = config.Selection.Subtitle();
            config.InputFilter = config.Selection.FilterName();
            UpdateTrackerVisibility();
            ApplyAccessConfig(config);
            // Interactive filter push: the engine owns the persisted value
            // (Apply no longer sets it), so an explicit method/selection
            // choice must activate the canonical filter for that selection.
            NativeBridge.dasher_set_string_parameter(_handle, ParameterKeys.SP_INPUT_FILTER, config.InputFilter);
        };

        selectionCombo.SelectionChanged += (s, e) =>
        {
            var method = methods[methodCombo.SelectedIndex];
            var valid = method.ValidFor();
            if (selectionCombo.SelectedIndex >= 0 && selectionCombo.SelectedIndex < valid.Length)
            {
                config.Selection = valid[selectionCombo.SelectedIndex];
                if (!_populatingSelections)
                    AnalyticsService.CaptureSettingChanged("selection_method", "enum", config.Selection.DisplayName());
                subtitle.Text = config.Selection.Subtitle();
                // Keep the persisted input filter in step with the selection
                // choice (Apply no longer derives or pushes it).
                config.InputFilter = config.Selection.FilterName();
                ApplyAccessConfig(config);
                NativeBridge.dasher_set_string_parameter(_handle, ParameterKeys.SP_INPUT_FILTER, config.InputFilter);
            }
        };

        trackerCombo.SelectionChanged += (s, e) =>
        {
            config.EyeTrackerType = trackerCombo.SelectedIndex switch
            {
                1 => "UdpGaze",
                2 => "Eyetuitive",
                3 => "Tobii",
                _ => "WindowsNative",
            };
            AnalyticsService.CaptureSettingChanged("eye_tracker_type", "enum", config.EyeTrackerType);
            UpdateTrackerVisibility();
            ApplyAccessConfig(config);
        };

        portBox.ValueChanged += (s, e) =>
        {
            config.UdpPort = (int)portBox.Value;
            ApplyAccessConfig(config);
        };
        // setting_changed on commit (focus leaves the field), deduped against
        // the last committed port — NumericUpDown ValueChanged fires per
        // keystroke/spin, and a tab-through or panel rebuild blur must not
        // emit a false change (review D1).
        int lastPort = config.UdpPort;
        portBox.LostFocus += (s, e) =>
        {
            var p = (int)portBox.Value;
            if (p == lastPort) return;
            lastPort = p;
            AnalyticsService.CaptureSettingChanged("udp_port", "number", p);
        };

        panel.Children.Add(methodCombo);
        panel.Children.Add(selectionLabel);
        panel.Children.Add(selectionCombo);
        panel.Children.Add(subtitle);
        panel.Children.Add(trackerLabel);
        panel.Children.Add(trackerCombo);
        panel.Children.Add(portPanel);
        panel.Children.Add(trackerHelp);
        panel.Children.Add(filterHeader);
        panel.Children.Add(filterTogglePanel);
        panel.Children.Add(gazeFilterHelp);
        panel.Children.Add(diagTogglePanel);
        panel.Children.Add(diagHelp);
        panel.Children.Add(deadZoneSlider);
        panel.Children.Add(deadZoneLabel);
        panel.Children.Add(settleSlider);
        panel.Children.Add(settleLabel);
        panel.Children.Add(filterLabel);
        panel.Children.Add(filterCombo);
        panel.Children.Add(filterHelp);

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
                "Eyetuitive" => EyeGazeIntegration.TrackerType.Eyetuitive,
                "Tobii" => EyeGazeIntegration.TrackerType.TobiiStreamEngine,
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

    /// <summary>
    /// Localise a settings category name from the shared catalogue (RFC 0003).
    /// The UI culture follows the OS language, so the right satellite resource
    /// is chosen automatically. Categories without a catalogue key
    /// (Appearance, Other) stay as-is.
    /// </summary>
    public static string LocalizeCategory(string category) => category switch
    {
        "Input" => Loc.Tr("input", "Input"),
        "Language" => Loc.Tr("language", "Language"),
        "Customization" => Loc.Tr("customization", "Customization"),
        "Speed" => Loc.Tr("speed", "Speed"),
        "Output" => Loc.Tr("output", "Output"),
        "Speech" => Loc.Tr("speech", "Speech"),
        "Advanced" => Loc.Tr("advanced_settings", "Advanced"),
        "Privacy" => Loc.Tr("privacy", "Privacy"),
        _ => category,
    };

    /// <summary>
    /// The engine-owned training file for the current alphabet (the file
    /// adaptive learning appends to), or null when unavailable. The UI reads,
    /// exports, appends imports to, and resets THIS file — never a derived
    /// path (issue #53: the old code read training\training_english_GB.txt,
    /// a file the engine never writes, so exports failed on fresh installs
    /// and exported stale v5 snapshots for migrants).
    /// </summary>
    private string? GetTrainingPath()
    {
        try
        {
            var p = NativeBridge.dasher_get_training_path(_handle);
            if (p == IntPtr.Zero) return null;
            var path = Marshal.PtrToStringUTF8(p);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static readonly string LegacyTrainingDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "training");

    private Control BuildTrainingSection()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tr("training_data", "Training Data"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushLabel,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tr("training_data_description",
                "Import adds text to the language model and your accumulated training data (kept across restarts). " +
                "Export saves your training data for backup or transfer. Reset deletes all user training data — " +
                "the model returns to its built-in defaults on next launch."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushValue,
        });

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        var importBtn = new Button
        {
            Content = Loc.Tr("training_import", "Import Training Text"),
            Padding = new Thickness(12, 6),
            FontSize = 12,
            Background = BrushControlBg,
            Foreground = BrushLabel,
            BorderThickness = new Thickness(0),
        };

        var exportBtn = new Button
        {
            Content = Loc.Tr("training_export", "Export Training Data"),
            Padding = new Thickness(12, 6),
            FontSize = 12,
            Background = BrushControlBg,
            Foreground = BrushLabel,
            BorderThickness = new Thickness(0),
        };

        var resetBtn = new Button
        {
            Content = Loc.Tr("training_reset", "Reset"),
            Padding = new Thickness(12, 6),
            FontSize = 12,
            Background = BrushControlBg,
            Foreground = BrushLabel,
            BorderThickness = new Thickness(0),
        };

        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = BrushMuted,
            Margin = new Thickness(0, 4, 0, 0),
        };

        void RefreshStatus()
        {
            var trainingFile = GetTrainingPath();
            if (trainingFile != null && System.IO.File.Exists(trainingFile))
            {
                var sizeKB = new System.IO.FileInfo(trainingFile).Length / 1024;
                statusText.Text = string.Format(
                    Loc.Tr("training_current_size", "Current training data: {0} KB"), sizeKB);
            }
            else
            {
                statusText.Text = Loc.Tr("training_none_yet", "No user training data yet");
            }
        }
        RefreshStatus();

        importBtn.Click += async (s, e) =>
        {
            // Whole-handler guard: this is an async void — an exception
            // from the picker itself (COM failures, owner-window issues —
            // environment-dependent, see the recurring "export crashes the
            // app" reports) would take down the process. Everything shows a
            // status message instead.
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                var storageProvider = topLevel.StorageProvider;
                // Dialog-friendly owner — see the export handler.
                System.Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> result;
                using (Services.PickerSession.Begin(topLevel.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero))
                {
                    result = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = Loc.Tr("training_import", "Import Training Text"),
                        FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Text Files") { Patterns = ["*.txt"] }],
                        AllowMultiple = false,
                    });
                }
                if (result.Count == 0) return;
                var text = await System.IO.File.ReadAllTextAsync(result[0].Path.LocalPath);

                // 1. Persist FIRST — the training file is the source of truth
                // at startup, so if anything later fails the learning still
                // applies on next launch (self-healing). The reverse order
                // trained the live model and then lost the text on a
                // persistence failure, diverging model and file (greptile:
                // "persistence failure leaves models divergent"). An
                // unavailable path or failed append is a clean failure:
                // nothing has changed anywhere.
                var trainingFile = GetTrainingPath()
                    ?? throw new InvalidOperationException("training path unavailable");
                await System.IO.File.AppendAllTextAsync(trainingFile, text + "\n");

                // 2. Train the LIVE model. A failure here leaves the FILE
                // ahead of the model — they converge at next launch; say so
                // instead of claiming a plain failure after mutating state.
                var rc = NativeBridge.dasher_import_training_text(_handle, text);
                statusText.Text = rc == 0
                    ? string.Format(Loc.Tr("training_imported", "Imported {0} KB of training text"), text.Length / 1024)
                    : string.Format(
                        Loc.Tr("training_imported_restart",
                            "Imported {0} KB — applies fully on next launch (live model returned {1})"),
                        text.Length / 1024, rc);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                // Non-fatal, but keep the evidence: these picker failures are
                // environment-dependent — the scrubbed crash file (kept
                // locally unless uploaded, see FlushPendingCrash) is how we
                // diagnose them (greptile: "picker error details disappear").
                if (ex is not OperationCanceledException)
                    AnalyticsService.WriteCrashFile(ex, "training import picker");
                statusText.Text = string.Format(Loc.Tr("training_failed", "{0} failed: {1}"),
                    Loc.Tr("training_import", "Import"), ex.Message);
            }
        };

        exportBtn.Click += async (s, e) =>
        {
            try
            {
                var trainingFile = GetTrainingPath();
                if (trainingFile == null || !System.IO.File.Exists(trainingFile))
                {
                    statusText.Text = Loc.Tr("training_nothing_to_export", "No training data to export yet");
                    return;
                }
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                var storageProvider = topLevel.StorageProvider;
                // Dialog-friendly owner (keyboard mode's NOACTIVATE+TOPMOST
                // styles make the native dialog unsurfaced → UI hangs in the
                // invisible modal pump — the v0.1.33 "export white screen").
                Avalonia.Platform.Storage.IStorageFile? result;
                using (Services.PickerSession.Begin(topLevel.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero))
                {
                    result = await storageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = Loc.Tr("training_export", "Export Training Data"),
                        DefaultExtension = "txt",
                        SuggestedFileName = "dasher_training_export.txt",
                        FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Text Files") { Patterns = ["*.txt"] }],
                    });
                }
                if (result == null) return;
                await System.IO.File.WriteAllTextAsync(result.Path.LocalPath, await System.IO.File.ReadAllTextAsync(trainingFile));
                statusText.Text = string.Format(Loc.Tr("training_exported", "Exported to {0}"), result.Name);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    AnalyticsService.WriteCrashFile(ex, "training export picker");
                statusText.Text = string.Format(Loc.Tr("training_failed", "{0} failed: {1}"),
                    Loc.Tr("training_export", "Export"), ex.Message);
            }
        };

        resetBtn.Click += (s, e) =>
        {
            // Flags-first: settle the v5 migration lifecycle BEFORE any
            // deletion (clean-failure semantics). Without this, a corpus
            // whose .v5migrated flag write had failed could be restored
            // by a later migration after the Reset deleted the only
            // content evidence — resurrecting training the user deleted.
            // A flag-write failure aborts here with nothing deleted.
            try
            {
                V5MigrationService.MarkTrainingReset();
            }
            catch (Exception ex)
            {
                statusText.Text = string.Format(Loc.Tr("training_failed", "{0} failed: {1}"),
                    Loc.Tr("training_reset", "Reset"), ex.Message);
                return;
            }

            // Deletions are per-file isolated: one locked/undeletable file
            // must not abort the loop halfway ("reset leaves partial training
            // state") — every file gets its attempt, and Retry (idempotent:
            // flags rewritten, glob covers survivors) mops up the rest.
            var deleted = false;
            var failures = new List<string>();

            void TryDelete(string file)
            {
                try
                {
                    System.IO.File.Delete(file);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    failures.Add($"{System.IO.Path.GetFileName(file)}: {ex.Message}");
                }
            }

            try
            {
                // ALL alphabets' training files, matching the copy ("deletes
                // ALL user training data"): the glob covers every
                // training_*.txt in the engine's user-dir root, not just the
                // current alphabet's file.
                var engineFile = GetTrainingPath();
                var rootDir = engineFile != null
                    ? System.IO.Path.GetDirectoryName(engineFile)
                    : System.IO.Path.GetDirectoryName(LegacyTrainingDir);
                if (rootDir != null && System.IO.Directory.Exists(rootDir))
                    foreach (var f in System.IO.Directory.GetFiles(rootDir, "training_*.txt"))
                        TryDelete(f);

                // Legacy v5-migration copies under training\ — also scanned
                // at startup, so they must go too.
                if (System.IO.Directory.Exists(LegacyTrainingDir))
                    foreach (var f in System.IO.Directory.GetFiles(LegacyTrainingDir, "training_*.txt"))
                        TryDelete(f);
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }

            if (failures.Count > 0)
            {
                // Partial (or glob-level) failure: deleted-what-we-could —
                // the remaining files stay and Reset can simply be retried.
                statusText.Text = string.Format(
                    Loc.Tr("training_reset_partial",
                        "Deleted training data where possible — {0} item(s) failed; press Reset again to retry"),
                    failures.Count);
                return;
            }

            statusText.Text = deleted
                ? Loc.Tr("training_reset_done", "Training data deleted — the model returns to its built-in defaults on next launch")
                : Loc.Tr("training_none_yet", "No user training data yet");
        };

        btnRow.Children.Add(importBtn);
        btnRow.Children.Add(exportBtn);
        btnRow.Children.Add(resetBtn);
        panel.Children.Add(btnRow);
        panel.Children.Add(statusText);

        return panel;
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

    private static bool ShouldHideParameter(ParameterDisplayInfo info, string category)
    {
        // "Control Mode" used to be hidden here (the toolbar button owned the
        // toggle) — that hid the ONLY in-settings way to enable the control
        // node, and the toolbar button wasn't recognisable as it: a tester
        // reported "the control node is gone, even in the settings" (Android
        // keeps the settings row and has no such confusion). Both paths now
        // toggle BP_CONTROL_MODE; the toolbar accent syncs via the engine's
        // parameter callback (OnParameterChanged).
        if (info.Name == "Simulate Transparency")
            return true;
        return false;
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

        var section = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4, 0, 12) };

        var header = new TextBlock
        {
            Text = Loc.Tr("colour_theme"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushLabel,
        };
        section.Children.Add(header);

        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
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
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 8, 8),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = name,
            };

            var cardContent = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };

            var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            for (int c = 0; c < 4; c++)
            {
                var swatch = new Border
                {
                    Width = 22,
                    Height = 28,
                    CornerRadius = new CornerRadius(3),
                    Background = ArgbToBrush(colors[c]),
                };
                swatchRow.Children.Add(swatch);
            }

            var swatchContainer = new Border
            {
                Child = swatchRow,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(isSelected ? 3 : 0),
                BorderBrush = isSelected
                    ? ((this.FindResource("OnAccent") as IBrush) ?? Brushes.Navy)
                    : Brushes.Transparent,
                Padding = new Thickness(2),
            };
            cardContent.Children.Add(swatchContainer);

            var nameLabel = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = isSelected
                    ? (BrushLabel)
                    : (BrushValue),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = 100,
                TextAlignment = TextAlignment.Center,
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
            };
            cardContent.Children.Add(nameLabel);

            card.Content = cardContent;
            card.Width = 110;

            card.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is string paletteName)
                {
                    NativeBridge.dasher_set_palette(_handle, paletteName);
                    AnalyticsService.CaptureSettingChanged("Colour Palette", "enum", paletteName);
                    // Refresh the customization section to show the new theme selection
                    ShowCategory("Customization");
                }
            };

            wrap.Children.Add(card);
        }

        section.Children.Add(wrap);
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
            Foreground = BrushLabel,
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

        // The target-calibration offset learns while auto-calibration runs
        // (eye-gaze) and is invisible state to the user — offer an explicit
        // reset beside it (DasherCore #64).
        if (info.Key == ParameterKeys.LP_TARGET_OFFSET)
        {
            var reset = new Button
            {
                Content = Loc.Tr("training_reset"),
                Padding = new Thickness(12, 4),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = BrushControlBg,
                Foreground = BrushLabel,
                BorderThickness = new Thickness(0),
            };
            reset.Click += (s, e) =>
            {
                NativeBridge.dasher_set_long_parameter(_handle, ParameterKeys.LP_TARGET_OFFSET, 0);
                // BuildStep's editor: StackPanel > Border > TextBlock value.
                if (editor is StackPanel sp && sp.Children.OfType<Border>().FirstOrDefault() is { } border &&
                    border.Child is TextBlock value)
                    value.Text = "0";
            };
            row.Children.Add(reset);
        }

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
                AnalyticsService.CaptureSettingChanged(info.Name, "enum", font);
            }
        };

        return comboBox;
    }

    /// <summary>
    /// Simple/Advanced switch (RFC 0006 progressive disclosure, parity with
    /// Dasher-Android). Simple hides advanced and expert parameter tiers.
    /// </summary>
    private Control BuildSimpleAdvancedRow(UiSettings settings)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        var text = new TextBlock
        {
            Text = Loc.Tr("advanced_settings", "Advanced settings"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsChecked = settings.ShowAdvanced,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(12, 0, 0, 0),
        };
        toggle.IsCheckedChanged += (s, e) =>
        {
            settings.ShowAdvanced = toggle.IsChecked == true;
            settings.Save();
            AnalyticsService.CaptureSettingChanged("show_advanced", "bool", toggle.IsChecked == true);
            ShowCategory(_currentCategory);
        };
        row.Children.Add(toggle);

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
            AnalyticsService.CaptureSettingChanged(info.Name, "bool", toggle.IsChecked == true);
        };

        return toggle;
    }

    /// <summary>
    /// Label + toggle row matching the parameter-row layout: label docked left
    /// with fixed width, toggle immediately beside it. Never right-aligns the
    /// toggle — in a wide window that puts it far from its label.
    /// </summary>
    private Control BuildToggleRow(string label, bool isChecked, Action<bool> onChanged)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(text, label);
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsChecked = isChecked,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toggle.IsCheckedChanged += (s, e) => onChanged(toggle.IsChecked == true);
        row.Children.Add(toggle);

        return row;
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
            Foreground = BrushValue,
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

        // RFC 0001 2026-10-28: setting_changed is for COMMITTED changes —
        // ValueChanged fires per drag tick (dozens per gesture), so emit on
        // drag release / keyboard adjust instead, deduped against the last
        // committed value.
        AttachSliderCommit(slider, current, v => AnalyticsService.CaptureSettingChanged(info.Name, "number", v));

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
            Background = BrushHighlight,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            MinWidth = 50,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = current.ToString(),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = BrushValue,
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
            var before = val;
            val = Math.Max(min, val - step);
            if (val == before) return; // clamped at the bound: not a change
            UpdateDisplay();
            AnalyticsService.CaptureSettingChanged(info.Name, "number", val);
        };

        upBtn.Click += (s, e) =>
        {
            var before = val;
            val = Math.Min(max, val + step);
            if (val == before) return; // clamped at the bound: not a change
            UpdateDisplay();
            AnalyticsService.CaptureSettingChanged(info.Name, "number", val);
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
            {
                NativeBridge.dasher_set_long_parameter(_handle, info.Key, val);
                AnalyticsService.CaptureSettingChanged(info.Name, "enum", item.Content?.ToString() ?? "");
            }
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

            // Last value actually applied to the engine: compare against
            // THIS (not the build-time initial) so reverting to the original
            // text still applies — and tab-through/ShowCategory rebuilds
            // with unchanged text still skip (review N1: comparing against
            // the build-time initial left the engine desynced on revert).
            string lastApplied = current;
            textBox.LostFocus += (s, e) =>
            {
                var text = textBox.Text ?? "";
                if (text == lastApplied) return;
                NativeBridge.dasher_set_string_parameter(_handle, info.Key, text);
                lastApplied = text;
                // Free-text setting: the fact of the change only — values are
                // never collected (RFC 0001 2026-10-28 amendment).
                AnalyticsService.CaptureSettingChanged(info.Name, "string", null);
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
        int selectedIndex = -1;
        // Probe-then-fetch (needs DasherCore v0.2.5: the probe call used to
        // return 0 before querying, and a fixed 200-slot buffer truncated the
        // full alphabet list).
        int count = NativeBridge.dasher_get_parameter_string_values(_handle, info.Key, null!, 0);
        if (count > 0)
        {
            var ptrs = new IntPtr[count];
            count = Math.Min(count, NativeBridge.dasher_get_parameter_string_values(_handle, info.Key, ptrs, count));
            for (int i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUTF8(ptrs[i]) ?? "";
                names.Add(name);
                comboBox.Items.Add(name);
                if (name == current) selectedIndex = i;
            }
        }

        if (selectedIndex >= 0) comboBox.SelectedIndex = selectedIndex;

        comboBox.SelectionChanged += (s, e) =>
        {
            if (comboBox.SelectedIndex >= 0 && comboBox.SelectedIndex < names.Count)
            {
                NativeBridge.dasher_set_string_parameter(_handle, info.Key, names[comboBox.SelectedIndex]);
                // Values come from the engine's permitted-value list — a
                // fixed vocabulary, so the chosen value is collectable.
                AnalyticsService.CaptureSettingChanged(info.Name, "enum", names[comboBox.SelectedIndex]);
            }
        };

        return comboBox;
    }

    private void BuildPrivacySettings()
    {
        var settings = AnalyticsSettings.Load();

        var section = new StackPanel { Spacing = 16 };

        /* BrushLabel inlined */
        /* BrushValue inlined */
        /* BrushMuted inlined */

        section.Children.Add(new TextBlock
        {
            Text = Loc.Tr("anonymous_usage_analytics", "Anonymous usage analytics"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushLabel,
        });

        section.Children.Add(new TextBlock
        {
            Text = Loc.Tr("analytics_settings_help"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushValue,
        });

        var toggleRow = BuildToggleRow(Loc.Tr("analytics_opt_in", "Share anonymous analytics and crash reports"), settings.OptedIn, isChecked =>
        {
            AnalyticsService.SetOptIn(isChecked);
            // setting_changed fires for the commit regardless of opt-in state
            // — Capture drops it when not opted in, matching every event.
            AnalyticsService.CaptureSettingChanged("analytics_opt_in", "bool", isChecked);
        });
        toggleRow.Margin = new Thickness(0, 8, 0, 0);
        section.Children.Add(toggleRow);

        section.Children.Add(new TextBlock
        {
            Text = $"Anonymous ID: {AnalyticsService.AnonymousId}",
            FontSize = 11,
            Foreground = BrushMuted,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var resetBtn = new Button
        {
            Content = Loc.Tr("analytics_reset_id", "Reset analytics ID"),
            Padding = new Thickness(16, 6),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = BrushControlBg,
            Foreground = BrushLabel,
            BorderThickness = new Thickness(0),
        };
        resetBtn.Click += (s, e) =>
        {
            AnalyticsService.ResetAnonymousId();
            // Refresh the ID display
            foreach (var child in section.Children)
            {
                if (child is TextBlock tb && tb.Text?.StartsWith("Anonymous ID:") == true)
                    tb.Text = $"Anonymous ID: {AnalyticsService.AnonymousId}";
            }
        };
        section.Children.Add(resetBtn);

        section.Children.Add(new Border
        {
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 12, 0, 0),
        });

        // RFC 0016: app version, shown prominently after the analytics section.
        // Same constant analytics/crash events report (UpdateChecker), so the UI
        // can never disagree with telemetry. Interim until RFC 0006's About
        // section exists.
        section.Children.Add(new TextBlock
        {
            Text = $"Dasher {UpdateChecker.GetCurrentVersion()}",
            FontSize = 11,
            Foreground = BrushMuted,
            Margin = new Thickness(0, 8, 0, 0),
        });

#if !STORE
        // RFC 0017: opt-out for the passive update check (weekly, toast).
        // The initial value is a read-only snapshot; the handler re-loads and
        // writes ONLY Enabled so it can't clobber a check timestamp recorded
        // by the startup path while the panel was open.
        var updateEnabled = UpdateCheckSettings.Load().Enabled;
        var updateToggle = BuildToggleRow("Check for updates", updateEnabled, isChecked =>
        {
            UpdateCheckSettings.SetEnabled(isChecked);
        });
        updateToggle.Margin = new Thickness(0, 4, 0, 0);
        section.Children.Add(updateToggle);
#endif

        // ── Dasher 5 Import section ──
        section.Children.Add(new Border
        {
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 24, 0, 0),
        });

        section.Children.Add(new TextBlock
        {
            Text = Loc.Tr("dasher5_import"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushLabel,
        });

        if (V5MigrationService.HasV5Data)
        {
            var scan = V5MigrationService.Scan();

            var statusText = V5MigrationService.HasBeenOffered
                ? "Status: Imported"
                : "Status: Not imported (Dasher 5 data detected)";

            section.Children.Add(new TextBlock
            {
                Text = statusText,
                FontSize = 12,
                Foreground = BrushValue,
            });

            if (!string.IsNullOrEmpty(scan.Alphabet))
                section.Children.Add(new TextBlock { Text = $"  Alphabet: {scan.Alphabet}", FontSize = 11, Foreground = BrushMuted });
            if (!string.IsNullOrEmpty(scan.Colour))
                section.Children.Add(new TextBlock { Text = $"  Colour: {scan.Colour}", FontSize = 11, Foreground = BrushMuted });
            if (!string.IsNullOrEmpty(scan.Speed))
                section.Children.Add(new TextBlock { Text = $"  Speed: {scan.Speed}", FontSize = 11, Foreground = BrushMuted });
            if (scan.ControlMode)
                section.Children.Add(new TextBlock { Text = "  " + Loc.Tr("control_mode_enabled"), FontSize = 11, Foreground = BrushMuted });
            if (scan.CustomFileCount > 0)
                section.Children.Add(new TextBlock { Text = $"  {scan.CustomFileCount} custom file(s)", FontSize = 11, Foreground = BrushMuted });

            var importBtn = new Button
            {
                Content = Loc.Tr("import_from_dasher_5"),
                Padding = new Thickness(16, 6),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = BrushControlBg,
            Foreground = BrushLabel,
                BorderThickness = new Thickness(0),
            };
            importBtn.Click += (s, e) =>
            {
                var result = V5MigrationService.Import(_handle);
                // Apply deferred params immediately (engine is already realized)
                foreach (var (key, value) in result.DeferredParameters)
                {
                    if (value == "true" || value == "false")
                        NativeBridge.dasher_set_bool_parameter(_handle, key, value == "true" ? 1 : 0);
                    else if (int.TryParse(value, out var intVal))
                        NativeBridge.dasher_set_long_parameter(_handle, key, intVal);
                    else
                        NativeBridge.dasher_set_string_parameter(_handle, key, value);
                }
                // Refresh the panel
                ShowCategory("Privacy");
            };
            section.Children.Add(importBtn);
        }
        else
        {
            section.Children.Add(new TextBlock
            {
                Text = Loc.Tr("dasher5_no_data"),
                FontSize = 12,
                Foreground = BrushMuted,
            });
        }

        // ── Reset to defaults ──
        section.Children.Add(new Border
        {
            BorderBrush = BrushBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 24, 0, 0),
        });

        section.Children.Add(new TextBlock
        {
            Text = Loc.Tr("reset_settings", "Reset Settings"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushLabel,
        });

        section.Children.Add(new TextBlock
        {
            Text = Loc.Tr("restore_all_dasher_settings_to_their_default_values_this_can", "Restore all Dasher settings to their default values. This cannot be undone."),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushValue,
        });

        var resetSettingsBtn = new Button
        {
            Content = Loc.Tr("reset_to_defaults", "Reset to defaults"),
            Padding = new Thickness(16, 6),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0xEB, 0x5B, 0x5C)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        resetSettingsBtn.Click += (s, e) =>
        {
            var btn = s as Button;
            if (btn != null)
            {
                btn.Content = Loc.Tr("restarting_engine");
                btn.IsEnabled = false;
            }
            ResetSettingsRequested?.Invoke();
        };
        section.Children.Add(resetSettingsBtn);

        _panel.Children.Add(section);
    }

    private void BuildSpeechSettings()
    {
        var svc = SpeechService.Instance;

        var engineLabel = new TextBlock
        {
            Text = Loc.Tr("tts_engine"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
                    Text = Loc.Tr("tts_no_credentials"),
                    FontSize = 11,
                    Foreground = BrushValue,
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
                    Foreground = BrushLabel,
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

        Func<Task>? autoLoadVoices = null;

        engineCombo.SelectionChanged += async (s, e) =>
        {
            var idx = engineCombo.SelectedIndex;
            if (idx >= 0 && idx < SpeechService.EngineNames.Length)
            {
                svc.SelectedEngine = SpeechService.EngineNames[idx];
                svc.InvalidateClient();
                svc.SaveSettings();
                AnalyticsService.CaptureSettingChanged("speech_engine", "enum", svc.SelectedEngine);
                RebuildCredentials();
                if (autoLoadVoices != null)
                    await autoLoadVoices();
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
            Background = BrushControlBg,
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
            Content = Loc.Tr("load_voices"),
            FontSize = 12,
        };
        voiceBtnRow.Children.Add(loadVoicesBtn);

        var voiceCountLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = BrushValue,
            VerticalAlignment = VerticalAlignment.Center,
        };
        voiceBtnRow.Children.Add(voiceCountLabel);

        var previewBtn = new Button
        {
            Content = Loc.Tr("preview"),
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
            Text = Loc.Tr("rate"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Text = Loc.Tr("pitch"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Text = Loc.Tr("volume"),
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = BrushLabel,
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
            Foreground = BrushValue,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 40,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(volValue, Dock.Right);
        volRow.Children.Add(volValue);
        volRow.Children.Add(volSlider);
        voiceHeader.Children.Add(volRow);

        _panel.Children.Add(voiceHeader);

        // Voice loading logic — used by button click AND auto-load on engine change.
        // (_populatingVoices spans the await: a user selection made mid-load
        // is deliberately not emitted — the load's own programmatic
        // selection wins and the user's is a transient mid-rebuild state.
        // Single-flight: a second overlapping load would clear the flag
        // while the first still populates, so re-entrance just re-arms it.)
        async Task LoadVoicesIntoCombo()
        {
            loadVoicesBtn.IsEnabled = false;
            voiceCountLabel.Text = Loc.Tr("loading");
            _voiceLoadDepth++;
            // Auto-populated combo population is not a user commit: the
            // SelectedIndex assignments below would fire the handler.
            _populatingVoices = true;
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
                if (--_voiceLoadDepth == 0)
                    _populatingVoices = false;
            }
        }

        loadVoicesBtn.Click += async (s, e) => await LoadVoicesIntoCombo();

        voiceCombo.SelectionChanged += (s, e) =>
        {
            // Locale-stable frontend vocabulary for the sentinel entry —
            // the "(Default)" item is a frontend affordance, not an engine
            // value, so it reports the neutral "default".
            string chosen = "default";
            if (voiceCombo.SelectedIndex <= 0)
                svc.SelectedVoiceId = null;
            else if (voiceCombo.SelectedIndex - 1 < svc.AvailableVoices.Count)
            {
                svc.SelectedVoiceId = svc.AvailableVoices[voiceCombo.SelectedIndex - 1].Id;
                chosen = svc.AvailableVoices[voiceCombo.SelectedIndex - 1].Name;
            }
            svc.SaveSettings();
            if (!_populatingVoices)
                AnalyticsService.CaptureSettingChanged("speech_voice", "enum", chosen);
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
                AnalyticsService.CaptureSettingChanged("speech_rate", "enum", rateNames[rateCombo.SelectedIndex]);
            }
        };

        pitchCombo.SelectionChanged += (s, e) =>
        {
            if (pitchCombo.SelectedIndex >= 0 && pitchCombo.SelectedIndex < pitchValues.Length)
            {
                svc.SpeechPitch = pitchValues[pitchCombo.SelectedIndex];
                svc.SaveSettings();
                AnalyticsService.CaptureSettingChanged("speech_pitch", "enum", pitchNames[pitchCombo.SelectedIndex]);
            }
        };

        volSlider.ValueChanged += (s, e) =>
        {
            svc.SpeechVolume = (int)Math.Round(volSlider.Value);
            volValue.Text = $"{svc.SpeechVolume}%";
            svc.SaveSettings();
        };

        // Commit-time setting_changed (drag release / keyboard), matching the
        // generic parameter sliders — ValueChanged is a per-tick storm.
        AttachSliderCommit(volSlider, svc.SpeechVolume,
            v => AnalyticsService.CaptureSettingChanged("speech_volume", "number", v));

        // Auto-load voices on initial show
        autoLoadVoices = LoadVoicesIntoCombo;
        _ = LoadVoicesIntoCombo();

        var sep2 = new Border
        {
            Height = 1,
            Background = BrushControlBg,
            Margin = new Thickness(0, 12, 0, 8),
        };
        _panel.Children.Add(sep2);

        var speakOnStopLabel = new TextBlock
        {
            Text = Loc.Tr("engine_speech_features"),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = BrushLabel,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _panel.Children.Add(speakOnStopLabel);

        var speakOnStopRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var speakOnStopLabel2 = new TextBlock
        {
            Text = Loc.Tr("speak_on_stop"),
            FontSize = 12,
            Foreground = BrushLabel,
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
            AnalyticsService.CaptureSettingChanged("Speak on Stop", "bool", speakOnStopToggle.IsChecked == true);
        };
        speakOnStopRow.Children.Add(speakOnStopToggle);
        _panel.Children.Add(speakOnStopRow);

        var speakWordsRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var speakWordsLabel = new TextBlock
        {
            Text = Loc.Tr("speak_words"),
            FontSize = 12,
            Foreground = BrushLabel,
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
            AnalyticsService.CaptureSettingChanged("Speak Words", "bool", speakWordsToggle.IsChecked == true);
        };
        speakWordsRow.Children.Add(speakWordsToggle);
        _panel.Children.Add(speakWordsRow);
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

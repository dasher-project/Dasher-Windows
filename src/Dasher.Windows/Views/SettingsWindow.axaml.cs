using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using Dasher.Windows.Controls;
using Dasher.Windows.Engine;
using Lucide.Avalonia;

namespace Dasher.Windows.Views;

public class SettingsWindowCallbacks
{
    public Action<string, int>? OutputFontChanged { get; set; }
    public Action<double>? KeyboardOpacityChanged { get; set; }
    public Action<EyeGazeIntegration.TrackerType, int>? InputSourceChanged { get; set; }
    public Action? JoystickRequested { get; set; }
}

public partial class SettingsWindow : Window
{
    private readonly List<Button> _tabs = new();
    private SettingsPanel? _panel;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public void Initialize(IntPtr handle, SettingsWindowCallbacks callbacks)
    {
        _panel = this.FindControl<SettingsPanel>("SettingsPanel");
        if (_panel == null) return;

        _panel.Initialize(handle);

        if (callbacks.OutputFontChanged != null)
            _panel.OutputFontChanged += (f, s) => callbacks.OutputFontChanged(f, s);
        if (callbacks.KeyboardOpacityChanged != null)
            _panel.KeyboardOpacityChanged += callbacks.KeyboardOpacityChanged;
        if (callbacks.InputSourceChanged != null)
            _panel.InputSourceChanged += (s, e) => callbacks.InputSourceChanged(e.trackerType, e.udpPort);
        if (callbacks.JoystickRequested != null)
            _panel.JoystickRequested += (s, e) => callbacks.JoystickRequested();

        BuildTabs();

        DoneButton.Click += (_, _) => Close();
    }

    private static readonly Dictionary<string, LucideIconKind> TabIcons = new()
    {
        { "Input", LucideIconKind.MousePointerClick },
        { "Language", LucideIconKind.Languages },
        { "Customization", LucideIconKind.Palette },
        { "Speed", LucideIconKind.Gauge },
        { "Output", LucideIconKind.Type },
        { "Speech", LucideIconKind.Volume2 },
        { "Appearance", LucideIconKind.Eye },
        { "Advanced", LucideIconKind.Wrench },
        { "Other", LucideIconKind.Ellipsis },
    };

    private void BuildTabs()
    {
        if (_panel == null) return;
        var container = this.FindControl<StackPanel>("TabContainer");
        if (container == null) return;

        container.Children.Clear();
        _tabs.Clear();

        var categories = _panel.GetCategoryNames();
        foreach (var category in categories)
        {
            var tab = new Button
            {
                Classes = { "settings-tab" },
                Tag = category,
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var iconKind = TabIcons.GetValueOrDefault(category, LucideIconKind.Circle);
            stack.Children.Add(new LucideIcon { Kind = iconKind, Size = 16 });
            stack.Children.Add(new TextBlock { Text = category });

            tab.Content = stack;
            tab.Click += OnTabClick;
            container.Children.Add(tab);
            _tabs.Add(tab);
        }

        if (categories.Count > 0)
            ActivateTab(0);
    }

    private void OnTabClick(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string category || _panel == null) return;
        var idx = _tabs.IndexOf(btn);
        if (idx < 0) return;
        ActivateTab(idx);
        _panel.ShowCategory(category);
    }

    private void ActivateTab(int index)
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (i == index)
                _tabs[i].Classes.Add("active");
            else
                _tabs[i].Classes.Remove("active");
        }
    }
}

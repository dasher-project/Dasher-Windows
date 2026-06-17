using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Dasher.Windows.Services;
using Dasher.Windows.ViewModels;
using Dasher.Windows.Views;

namespace Dasher.Windows;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Initialize analytics (loads settings, starts PostHog if opted in)
            AnalyticsService.Initialize();

            // Crash capture
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            // Show opt-in prompt after the main window opens
            if (!AnalyticsService.HasPrompted)
            {
                desktop.MainWindow.Opened += (_, _) => ShowAnalyticsOptIn(desktop.MainWindow);
            }

            // Fire app_launched event
            AnalyticsService.Capture("app_launched", new()
            {
                ["platform"] = "windows",
                ["os_version"] = RuntimeInformation.OSDescription,
                ["app_version"] = UpdateChecker.GetCurrentVersion(),
                ["locale"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AnalyticsService.CaptureCrash(ex);
        _ = AnalyticsService.ShutdownAsync();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AnalyticsService.CaptureCrash(e.Exception);
        e.SetObserved();
    }

    private static void ShowAnalyticsOptIn(Window parent)
    {
        var dialog = new Window
        {
            Title = "Help improve Dasher",
            Width = 440,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xF6)),
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Help improve Dasher",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Dasher collects anonymous, privacy-respecting analytics to understand usage patterns and fix crashes. " +
                   "No typed text, clipboard contents, or personal information is ever collected.\n\n" +
                   "You can change this anytime in Settings > Privacy.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
        });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var notNowBtn = new Button
        {
            Content = "Not now",
            Padding = new Thickness(20, 8),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xE8)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
        };

        var helpBtn = new Button
        {
            Content = "Help improve Dasher",
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x99, 0xD4, 0xCD)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x4B, 0x75)),
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
        };

        notNowBtn.Click += (_, _) =>
        {
            AnalyticsService.SetOptIn(false);
            dialog.Close();
        };

        helpBtn.Click += (_, _) =>
        {
            AnalyticsService.SetOptIn(true);
            dialog.Close();
        };

        btnRow.Children.Add(notNowBtn);
        btnRow.Children.Add(helpBtn);
        panel.Children.Add(btnRow);

        dialog.Content = panel;
        dialog.ShowDialog(parent);
    }
}

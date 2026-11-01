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
                ["locale"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
            });
            _ = AnalyticsService.FlushAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            // RFC 0009: write crash file with engine log tail, flush on next launch
            AnalyticsService.WriteCrashFile(ex, "AppDomain.UnhandledException");
        }
        _ = AnalyticsService.ShutdownAsync();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // RFC 0009: write crash file for unobserved task exceptions
        AnalyticsService.WriteCrashFile(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private static void ShowAnalyticsOptIn(Window parent)
    {
        var dialog = new Window
        {
            Title = Loc.Tr("analytics_accept"),
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
            Text = Loc.Tr("analytics_accept"),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrushes.TextPrimary,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tr("analytics_rationale"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.TextSecondary,
        });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var notNowBtn = new Button
        {
            Content = Loc.Tr("analytics_decline"),
            Padding = new Thickness(20, 8),
            Background = Brushes.Transparent,
            BorderBrush = (Application.Current?.FindResource("BorderLight") as IBrush) ?? Brushes.LightGray,
            Foreground = ThemeBrushes.TextSecondary,
        };

        var helpBtn = new Button
        {
            Content = Loc.Tr("analytics_accept"),
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x99, 0xD4, 0xCD)),
            Foreground = ThemeBrushes.TextPrimary,
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

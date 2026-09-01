using Avalonia;
using System;
using System.IO;
using System.Threading;
using Dasher.Windows.Services;

namespace Dasher.Windows;

sealed class Program
{
    private const string MutexName = "Dasher-Windows-SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            OnFatalStartupException(ex);
        }
    }

    /// <summary>
    /// Last-resort handler for exceptions that escape the Avalonia lifetime.
    /// Writes the RFC 0009 crash envelope (scrubbed, versioned, flushed to
    /// PostHog on next launch if opted in) plus a plain-text copy, both under
    /// %APPDATA%\Dasher. Never throws and never blocks: the old handler wrote
    /// to a hardcoded dev-machine path, so user machines crashed inside the
    /// crash handler with DirectoryNotFoundException (issue reported via
    /// PostHog, 31 Aug).
    /// </summary>
    private static void OnFatalStartupException(Exception ex)
    {
        try { AnalyticsService.WriteCrashFile(ex, "Program.Main"); }
        catch { }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "startup-crash.log"),
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n" +
                $"Message: {ex.Message}\nType: {ex.GetType()}\nStack: {ex.StackTrace}\n" +
                $"Inner: {ex.InnerException}");
        }
        catch { }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

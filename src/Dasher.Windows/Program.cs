using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
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

        // Mixed-installation guard (#59): dasher.dll ships as an unversioned
        // content file, so an MSI upgrade can leave a STALE copy under a new
        // EXE — the app then dies periodically with EntryPointNotFoundException
        // on the first call to an export the old engine lacks (seed_buffer,
        // set_offset, get_training_path...). Fail loudly ONCE instead, with
        // an actionable message, before anything engine-touching runs.
        VerifyEngineExports();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            OnFatalStartupException(ex);
        }
    }

    // Sentinel export the app requires. Bump when adopting engine APIs newer
    // than the previous release (dasher_get_training_path needs v0.2.22).
    private const string RequiredEngineSentinel = "dasher_get_training_path";

    private static void VerifyEngineExports()
    {
        string failure;
        try
        {
            var handle = LoadLibrary("dasher.dll");
            if (handle == IntPtr.Zero)
            {
                failure = "dasher.dll could not be loaded (missing or wrong architecture).";
            }
            else if (GetProcAddress(handle, RequiredEngineSentinel) == IntPtr.Zero)
            {
                failure =
                    "This Dasher installation is mixed-version: dasher.dll is older than the app " +
                    "(missing " + RequiredEngineSentinel + "). Periodic crashes would follow. " +
                    "Please reinstall Dasher from the latest release.";
            }
            else
            {
                return; // engine present and new enough
            }
        }
        catch (Exception ex)
        {
            failure = "Engine check failed: " + ex.Message;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "startup-crash.log"),
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\nMessage: {failure}\nType: EngineVersionGuard\n");
        }
        catch { }

        // Native MessageBox (Avalonia is not up yet; no third-party APIs here).
        MessageBoxW(IntPtr.Zero, failure, "Dasher — installation problem", 0x10 /*MB_ICONERROR*/);
        Environment.Exit(1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string procName);

    [DllImport("user32.dll")]
    private static extern int MessageBoxW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string text,
        [MarshalAs(UnmanagedType.LPWStr)] string caption, uint type);

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

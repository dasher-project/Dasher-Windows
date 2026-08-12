using Avalonia;
using System;
using System.Threading;

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
            Console.WriteLine("Dasher is already running. Exiting.");
            return;
        }

        Console.WriteLine("Dasher starting...");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(@"C:\github\DasherProjects\crash.log",
                $"Message: {ex.Message}\nType: {ex.GetType()}\nStack: {ex.StackTrace}\nInner: {ex.InnerException}");
            Console.WriteLine($"CRASH: {ex}");
            Console.ReadLine();
        }
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

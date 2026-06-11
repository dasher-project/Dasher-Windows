using Avalonia;
using System;

namespace Dasher.Windows;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
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

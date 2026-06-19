using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dasher.Windows.Services;

/// <summary>
/// Provides theme-aware brushes for code-behind UI construction.
/// MUST be initialized with the main window so resources resolve
/// against the correct ActualThemeVariant (dark/light).
/// </summary>
public static class ThemeBrushes
{
    private static Window? _window;

    public static void Initialize(Window window) => _window = window;

    public static IBrush TextPrimary => Resolve("TextPrimary", Brushes.Black);
    public static IBrush TextSecondary => Resolve("TextSecondary", Brushes.DimGray);
    public static IBrush TextMuted => Resolve("TextMuted", Brushes.Gray);

    private static IBrush Resolve(string key, IBrush fallback)
    {
        if (_window?.FindResource(key) is IBrush brush)
            return brush;
        return fallback;
    }
}

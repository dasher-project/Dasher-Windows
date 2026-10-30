using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Services;

/// <summary>
/// Makes the main window a viable owner for native modal file dialogs.
///
/// In keyboard (direct-entry) mode the window carries WS_EX_NOACTIVATE,
/// WS_EX_LAYERED and TOPMOST so it floats over the target application
/// without stealing keystrokes. A Win32 common dialog owned by such a
/// window never surfaces: it cannot take activation from a NOACTIVATE
/// owner and is born behind the topmost one — IFileDialog::Show then
/// pumps invisibly on the UI thread forever. To the user the app is a
/// frozen white screen ("export crashes the app", v0.1.33 tester report)
/// that only Task Manager can end — reproduced locally with the picker
/// invoked from keyboard mode.
///
/// PickDialogFriendly temporarily clears WS_EX_NOACTIVATE and drops the
/// window out of the topmost band (restored by the returned dispose);
/// WS_EX_LAYERED is deliberately left alone — it does not block the
/// dialog and clearing it would break Window.Opacity. Pure Win32: the
/// Avalonia Topmost/Opacity properties are left alone too — they are
/// re-applied by the mode logic and must not fight the dialog.
/// The restore is surgical (only the NOACTIVATE bit and the topmost band
/// are touched), so a style another actor changed while the dialog was up
/// survives.
/// </summary>
public static class WindowDialogStyles
{
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOPMOST = 0x00000008;
    internal const long WS_EX_NOACTIVATE = 0x08000000;
    internal const long WS_EX_LAYERED = 0x00080000;

    // 64-bit entry points; user32.dll has no *Ptr variants on 32-bit, so
    // the accessors switch on IntPtr.Size (the pattern MainWindow used to
    // duplicate — this is now the single source of truth for both).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    internal static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    internal static long SetExStyle(IntPtr hwnd, long style) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GWL_EXSTYLE, (IntPtr)style).ToInt64()
            : SetWindowLong32(hwnd, GWL_EXSTYLE, (int)style);

    internal static bool TrySetForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && SetForegroundWindow(hwnd);

    /// <summary>
    /// Clear NOACTIVATE, leave the topmost band and bring the window to the
    /// foreground so the dialog it owns can activate and show above it.
    /// </summary>
    /// <returns>An IDisposable that restores the previous state. Never null;
    /// a zero/missing handle — or a window that already dialog-friendly
    /// (editor mode) — is a no-op.</returns>
    public static IDisposable PickDialogFriendly(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Noop;

        var exStyle = GetExStyle(hwnd);
        var wasTopmost = (exStyle & WS_EX_TOPMOST) != 0;
        var hadNoActivate = (exStyle & WS_EX_NOACTIVATE) != 0;
        if (!hadNoActivate && !wasTopmost)
        {
            // Editor-mode window: nothing to normalize, and no foreground
            // push either — the user just clicked a button in this window.
            return Noop;
        }

        if (hadNoActivate) SetExStyle(hwnd, exStyle & ~WS_EX_NOACTIVATE);
        if (wasTopmost) SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetForegroundWindow(hwnd);
        return new Restore(hwnd, hadNoActivate, wasTopmost);
    }

    private sealed class Restore(IntPtr hwnd, bool restoreNoActivate, bool restoreTopmost) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                // Surgical: only the bits PickDialogFriendly cleared come
                // back; anything another actor changed meanwhile survives.
                if (restoreNoActivate)
                {
                    var cur = GetExStyle(hwnd);
                    if ((cur & WS_EX_NOACTIVATE) == 0) SetExStyle(hwnd, cur | WS_EX_NOACTIVATE);
                }
                if (restoreTopmost && (GetExStyle(hwnd) & WS_EX_TOPMOST) == 0)
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch
            {
                // Never throw from a dispose on the UI path; worst case the
                // window keeps the dialog-friendly styles until the next
                // keyboard-mode change re-applies them.
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private static readonly NoopDisposable Noop = new();
}

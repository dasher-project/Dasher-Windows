using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dasher.Windows.Services;

/// <summary>
/// Modifier hygiene (issue #112): the invariant that Alt/Ctrl/Shift are NEVER
/// left logically down by a Dasher injection. A stranded modifier converts
/// every subsequent keystroke — ours or the user's — into a command chord
/// (the #90 Ctrl storm that choked Outlook for 10m49s; the v6.0.0 Alt storm
/// where ribbon accelerators fired and deleted a message mid-zoom). The
/// atomic-pair fix (#99/39ee590) cannot help: the transient alone is enough.
///
/// Two layers:
///   - <see cref="AssertClean"/>: called after every injection batch that
///     includes modifier key-downs (the RFC 0021 chords, the clipboard-bridge
///     Ctrl chords, the shielded char/VK paths). It verifies the OS state —
///     GetAsyncKeyState, the async truth, NOT GetKeyState (the
///     thread-synchronized view is stale on background threads and can pass
///     while the key is effectively latched — the #112 lesson from the old
///     Alt-tap safety release) — and synthesizes the KEYUP if a modifier is
///     still down. It checks the OS, not our arrays: even a batch that
///     carries its own downs and ups atomically gets the assert, because a
///     consumer can eat the release half after SendInput returns.
///   - a 1 Hz watchdog sweep (source="watchdog") that catches anything ANY
///     path strands — ours, a picker dialog's, or a driver's — started once
///     via <see cref="EnsureStarted"/> from the MainWindow startup, stopped
///     on window close.
///
/// Note this deliberately trumps the #90 shield's "restore held modifiers"
/// tail: a modifier held down (even physically, e.g. a sticky-key driver)
/// that survives a second is the hazard condition itself — failing toward
/// plain input can only lose a chord, never invent a command.
/// </summary>
public static class ModifierHygiene
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Full-size INPUT (MOUSEINPUT is the largest union member) so
    // Marshal.SizeOf<INPUT>() matches the native sizeof(INPUT) — SendInput
    // validates cbSize and rejects a short struct.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBOARDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBOARDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Assert the hygiene invariant: Alt/Ctrl/Shift must be up. Any still
    /// down gets a synthetic KEYUP and a log line the field logs can prove
    /// ("hygiene: released VK_MENU (source=watchdog)").
    /// </summary>
    public static void AssertClean(string source)
    {
        try
        {
            ReleaseIfDown(VK_MENU, "VK_MENU", source);
            ReleaseIfDown(VK_CONTROL, "VK_CONTROL", source);
            ReleaseIfDown(VK_SHIFT, "VK_SHIFT", source);
        }
        catch { /* never let hygiene break an injection path (#93 fail-soft) */ }
    }

    private static void ReleaseIfDown(int vk, string name, string source)
    {
        if ((GetAsyncKeyState(vk) & 0x8000) == 0) return;

        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = (ushort)vk;
        input.u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        Log($"hygiene: released {name} (source={source})");
    }

    // ── Watchdog (1 Hz sweep, lazy singleton) ──────────────────────────────

    private static Timer? _watchdog;
    private static readonly object Gate = new();

    /// <summary>Start the 1 Hz watchdog once (called from the MainWindow
    /// startup; idempotent).</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_watchdog != null) return;
            _watchdog = new Timer(_ => AssertClean("watchdog"), null, 1000, 1000);
        }
    }

    /// <summary>Stop the watchdog (called on window close so the sweep never
    /// outlives the app's input surface).</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _watchdog?.Dispose();
            _watchdog = null;
        }
    }

    // ── Logging (shared keyboard_debug.log) ────────────────────────────────

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "keyboard_debug.log");

    private static void Log(string msg)
    {
        // Same gate as KeyboardTargetTracker.Log / MainWindow.KbLog: the
        // release lines above are first-class (never indented) so the field
        // logs can always confirm the #112 fix is live.
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [hygiene] {msg}\n"); } catch { }
    }
}

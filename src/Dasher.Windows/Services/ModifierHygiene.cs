using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dasher.Windows.Services;

/// <summary>
/// Modifier hygiene (issue #112): the invariant that Alt/Ctrl/Shift/Win are
/// NEVER left logically down by a Dasher injection. A stranded modifier
/// converts every subsequent keystroke — ours or the user's — into a command
/// chord (the #90 Ctrl storm that choked Outlook for 10m49s; the v6.0.0 Alt
/// storm where ribbon accelerators fired and deleted a message mid-zoom). The
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
/// #112 review loop 2: the policed set is the full SIDED set
/// (VK_LSHIFT…VK_RWIN), matching what InjectionShield restores — SendInput
/// processes wVk as-is, so a generic KEYUP (0x10/0x11/0x12) need not clear a
/// sided async entry. Checking only generics let a held-LCtrl user cycle:
/// shield re-presses LCtrl, hygiene releases generic Ctrl, LCtrl stays
/// latched → a synthetic Ctrl transient on EVERY keystroke, exactly the
/// storm class this invariant retires. Win is policed too (a stranded Win is
/// a Start-menu storm). Shift stays in the set even though the shield
/// deliberately does not restore it: the RFC 0021 select-chords synthesize
/// Shift (Ctrl+Shift+arrows), so it IS in the synthesized set and hygiene
/// polices it — sided, both sides.
///
/// Note this deliberately trumps the #90 shield's "restore held modifiers"
/// tail: a modifier held down (even physically, e.g. a sticky-key driver)
/// that survives a second is the hazard condition itself — failing toward
/// plain input can only lose a chord, never invent a command.
/// </summary>
public static class ModifierHygiene
{
    /// <summary>#112 review loop 2: the full SIDED modifier set, each side
    /// checked and released individually. Name is the VK_ constant the field
    /// logs grep ("hygiene: released VK_…"); Friendly is the generic name +
    /// side a human reads.</summary>
    private static readonly (int Vk, string Name, string Friendly)[] HygieneSet =
    {
        (0xA0, "VK_LSHIFT",   "left Shift"),
        (0xA1, "VK_RSHIFT",   "right Shift"),
        (0xA2, "VK_LCONTROL", "left Ctrl"),
        (0xA3, "VK_RCONTROL", "right Ctrl"),
        (0xA4, "VK_LMENU",    "left Alt"),
        (0xA5, "VK_RMENU",    "right Alt"),
        (0x5B, "VK_LWIN",     "left Win"),
        (0x5C, "VK_RWIN",     "right Win"),
    };

    /// <summary>Source tag of the watchdog sweep (rate-limited logging;
    /// injection-site asserts always log).</summary>
    private const string WatchdogSource = "watchdog";

    /// <summary>#112 review loop 2: at most one watchdog log line per VK per
    /// this window — a sticky-key driver's latched modifier would otherwise
    /// be release-and-logged at 1 Hz all day, drowning diagnostics.</summary>
    private const long WatchdogLogIntervalMs = 30_000;

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
    /// Assert the hygiene invariant: every sided modifier (Shift/Ctrl/Alt/Win,
    /// both sides) must be up. Any still down gets a synthetic KEYUP and a
    /// log line the field logs can prove
    /// ("hygiene: released VK_LMENU (left Alt, source=watchdog)").
    /// Injection-site calls always log; the watchdog's lines are rate-limited
    /// to one per VK per 30 s (see <see cref="ReleaseIfDown"/>).
    /// </summary>
    public static void AssertClean(string source)
    {
        try
        {
            foreach (var (vk, name, friendly) in HygieneSet)
                ReleaseIfDown(vk, name, friendly, source);
        }
        catch { /* never let hygiene break an injection path (#93 fail-soft) */ }
    }

    private static void ReleaseIfDown(int vk, string name, string friendly, string source)
    {
        if ((GetAsyncKeyState(vk) & 0x8000) == 0) return;

        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = (ushort)vk;
        input.u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

        // #112 review loop 2: ALWAYS release (the invariant is safety), but
        // rate-limit the watchdog's LOG — a sticky-key driver's latched
        // modifier is indistinguishable from a strand and would be logged at
        // 1 Hz forever. At most one line per VK per WatchdogLogIntervalMs;
        // suppressed releases surface as "(repeated N times)" on the next
        // logged line. Injection-site asserts (source != watchdog) always
        // log.
        var repeated = 0;
        if (source == WatchdogSource)
        {
            lock (LogRateGate)
            {
                _lastWatchdogLogTick.TryGetValue(vk, out var last);
                var now = Environment.TickCount64;
                if (now - last < WatchdogLogIntervalMs)
                {
                    _suppressedSinceLog[vk] = _suppressedSinceLog.TryGetValue(vk, out var n) ? n + 1 : 1;
                    return;
                }
                _lastWatchdogLogTick[vk] = now;
                if (_suppressedSinceLog.Remove(vk, out var suppressed)) repeated = suppressed;
            }
        }
        Log(repeated > 0
            ? $"hygiene: released {name} ({friendly}, source={source}) (repeated {repeated} times)"
            : $"hygiene: released {name} ({friendly}, source={source})");
    }

    // ── Watchdog (1 Hz sweep, lazy singleton) ──────────────────────────────

    private static Timer? _watchdog;
    private static readonly object Gate = new();

    // #112 review loop 2: watchdog log rate-limiting state (per-VK last log
    // tick and suppressed-since-log count) — guarded by LogRateGate.
    private static readonly object LogRateGate = new();
    private static readonly Dictionary<int, long> _lastWatchdogLogTick = new();
    private static readonly Dictionary<int, int> _suppressedSinceLog = new();

    /// <summary>Start the 1 Hz watchdog once (called from the MainWindow
    /// startup; idempotent).</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_watchdog != null) return;
            _watchdog = new Timer(_ => AssertClean(WatchdogSource), null, 1000, 1000);
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
        // #112 review loop 2: keyboard_debug.log has three concurrent
        // writers (the UI thread's KbLog, the tracker's fg-restore thread,
        // this hygiene timer) — serialize through LogManager's shared lock so
        // a File.AppendAllText sharing violation cannot silently drop these
        // diagnostic lines. The release lines above are first-class (never
        // indented) so the field logs can always confirm the #112 fix is
        // live.
        lock (LogManager.KeyboardLogLock)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [hygiene] {msg}\n"); } catch { }
        }
    }
}

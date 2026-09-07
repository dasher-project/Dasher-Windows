using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Services;

/// <summary>
/// Tracks the user's typing-target window while Dasher is in keyboard
/// (direct entry) mode, and restores it to the foreground before input
/// injection so keystrokes always land in the user's application.
///
/// Owns three concerns that previously lived in MainWindow:
///   - the WinEventHooks: EVENT_SYSTEM_FOREGROUND for window switches and
///     EVENT_OBJECT_FOCUS for same-window field changes (the WS_EX_NOACTIVATE
///     overlay suppresses Deactivated, so hooks are the reliable signal)
///   - the tracked target HWND — ALWAYS rooted via TargetWindowIdentity
///     (SetForegroundWindow requires a top-level handle, and focus events
///     fire with child-control HWNDs)
///   - foreground restoration with AttachThreadInput escalation
///
/// Raises <see cref="TargetChanged"/> on the installing thread (the UI
/// thread) whenever the effective target changes or a same-window field
/// change is observed; the window decides whether to re-seed context.
/// </summary>
public sealed class KeyboardTargetTracker : IDisposable
{
    // ── Public surface ─────────────────────────────────────────────────────

    /// <summary>The rooted HWND of the tracked target window.</summary>
    public IntPtr Current => _target;

    /// <summary>Raised on the UI thread with a diagnostic reason.</summary>
    public event Action<string>? TargetChanged;

    /// <summary>
    /// Record the currently-foreground window as the target (mode entry,
    /// Deactivated fallback). No-op for our own window or when foreground
    /// cannot be rooted.
    /// </summary>
    public bool RecordForeground(IntPtr foregroundHwnd, IntPtr ourHwnd)
    {
        if (foregroundHwnd == IntPtr.Zero || foregroundHwnd == ourHwnd) return false;

        var root = TargetWindowIdentity.RootOf(foregroundHwnd);
        if (root == IntPtr.Zero) return false;

        if (root == _target) return false;
        _target = root;
        Log($"Record: target = 0x{root:X}");
        return true;
    }

    /// <summary>
    /// Install both hooks. Must be called on the UI thread (hook callbacks
    /// arrive on the installing thread's message loop).
    /// </summary>
    public void Install(IntPtr ourHwnd)
    {
        Remove(); // idempotent
        _ourHwnd = ourHwnd;

        _proc = (hook, eventType, hwnd, idObject, idChild, thread, time) =>
        {
            if (hwnd == IntPtr.Zero) return;

            // EVENT_OBJECT_FOCUS fires for non-window objects too (individual
            // controls, accessible elements) — the hwnd is still the containing
            // window. Only the foreground event is filtered to OBJID_WINDOW;
            // filtering focus events would reject browser/Electron editable
            // elements that are not windowed controls.
            if (eventType == EVENT_SYSTEM_FOREGROUND && idObject != 0 /* OBJID_WINDOW */) return;

            // Normalize to the root window — the tracked target is always a
            // root (see TargetWindowIdentity). A child-control HWND here would
            // break SetForegroundWindow restoration and would make the change
            // comparison meaningless (focus on a child of the current target
            // is a same-window field change, not a new target).
            var root = TargetWindowIdentity.RootOf(hwnd);
            if (root == IntPtr.Zero) return; // can't root — ignore, never store raw
            hwnd = root;

            if (hwnd == _ourHwnd) return; // Dasher gained focus — not a target

            var changed = hwnd != _target;
            _target = hwnd;

            if (eventType == EVENT_OBJECT_FOCUS)
                Raise(changed ? "focus changed (new window)" : "focus changed");
            else
                Raise(changed ? "foreground changed" : "foreground re-focus");
        };

        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Log($"Hooks installed: fg=0x{_foregroundHook:X} focus=0x{_focusHook:X}");
    }

    /// <summary>Remove both hooks (idempotent; also called by Dispose).</summary>
    public void Remove()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        if (_focusHook != IntPtr.Zero)
        {
            UnhookWinEvent(_focusHook);
            _focusHook = IntPtr.Zero;
            Log("Hooks removed");
        }
    }

    /// <summary>
    /// Tracks or restores the injection target so keystrokes always land in
    /// the user's application, never in Dasher itself. When the foreground is
    /// a new non-Dasher window it is recorded (and a change is raised — the
    /// user's first typing after a switch is the strongest signal they've
    /// arrived at their real target); when Dasher itself holds the keyboard
    /// focus the recorded target is restored, escalating through
    /// AttachThreadInput when it lives on another thread.
    /// </summary>
    public void EnsureForeground(IntPtr ourHwnd)
    {
        var fg = GetForegroundWindow();
        Log($"EnsureForeground: fg=0x{fg:X} us=0x{ourHwnd:X} target=0x{_target:X}");

        if (fg != ourHwnd && fg != IntPtr.Zero)
        {
            var root = TargetWindowIdentity.RootOf(fg);
            if (root != IntPtr.Zero && root != _target)
            {
                _target = root;
                Log("  foreground is a new target, tracking it");
                Raise("typing into new target");
            }
            else
            {
                Log("  foreground is target, tracking it");
            }
        }
        else if (_target != IntPtr.Zero)
        {
            Log("  → Dasher has focus, restoring target...");
            var targetThread = GetWindowThreadProcessId(_target, out _);
            var ourThread = GetCurrentThreadId();

            if (targetThread != 0 && targetThread != ourThread)
            {
                var attached = AttachThreadInput(ourThread, targetThread, true);
                var set = SetForegroundWindow(_target);
                AttachThreadInput(ourThread, targetThread, false);
                Log($"  → AttachThreadInput={attached} SetForegroundWindow={set}");
            }
            else
            {
                var set = SetForegroundWindow(_target);
                Log($"  → SetForegroundWindow={set}");
            }
        }
        else
        {
            Log("  → no known target window!");
        }
    }

    public void Dispose() => Remove();

    private void Raise(string reason) => TargetChanged?.Invoke(reason);

    // ── State ───────────────────────────────────────────────────────────────

    private IntPtr _target;
    private IntPtr _ourHwnd;
    private IntPtr _foregroundHook;
    private IntPtr _focusHook;
    private WinEventProc? _proc;

    // ── Logging (shared keyboard_debug.log) ────────────────────────────────

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "keyboard_debug.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [tracker] {msg}\n"); } catch { }
    }

    // ── Win32 ───────────────────────────────────────────────────────────────

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess,
        uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
}

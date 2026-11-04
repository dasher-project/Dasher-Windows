using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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

    /// <summary>#96-launch: set the remembered target from the previous
    /// session's persisted settings (the caller loads it from disk). The
    /// tracker uses it at Install time when the foreground is Dasher itself
    /// (fresh launch) — bridging to the user's typing target without
    /// waiting for the first focus event.</summary>
    public void SetRememberedTarget(IntPtr target) => _rememberedTarget = target;

    /// <summary>
    /// True when the tracked target is still the foreground window (or the
    /// foreground is unknown/transient and cannot be judged). Call this at
    /// SEED time — after any debounce delay, immediately before reading the
    /// target's text — so a stale queued focus event whose target was
    /// superseded mid-debounce cannot leak the old window's text into the
    /// engine ("stale focus bypasses foreground guard": the same-target hook
    //  branch deliberately skips the live check, so the seed path must be
    /// the one that verifies). When this returns false, drop the pending
    /// seed: the new foreground's own event chain will trigger a fresh one.
    /// </summary>
    public bool TargetIsForegroundOrUnknown(IntPtr ourHwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ourHwnd)
            return true; // unknown, or Dasher transiently holds focus
        return TargetWindowIdentity.RootOf(fg) == _target;
    }

    /// <summary>Raised on the UI thread with a diagnostic reason.</summary>
    public event Action<string>? TargetChanged;

    /// <summary>#96-launch: the last target from the previous session,
    /// persisted so a fresh launch with Dasher as foreground can bridge to
    /// the user's typing target without waiting for a focus event.</summary>
    private IntPtr _rememberedTarget;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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
        _rememberedTarget = root; // #96-launch: persist for next session
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

        // Initial-target seeding (#96, field-confirmed 2026-11-02): the
        // hooks only fire on focus CHANGES, so after a restart the tracker
        // had no target until the next focus event — ~10 seconds of dropped
        // keystrokes while the user was already parked in their typing
        // target ("no known target window" × N in the log, then the engine
        // buffer desynced from the target). If the CURRENT foreground is
        // already a non-Dasher window, that is where typing will land —
        // record it up front. ONLY when no target is remembered yet:
        // ResumeTargetTracking re-Installs after a picker dialog closes and
        // relies on the remembered target (its TrySetForeground restores
        // it) — capturing the dying dialog's HWND here would reintroduce
        // the "dead dialog as target" bug that path fixed (review loop 1).
        //
        // #96-launch variant (v0.1.42 field-confirmed 2026-11-04): when the
        // foreground IS Dasher (fresh launch — the user just started the
        // app), the capture skips — and the v0.1.42 log showed the user
        // zooming into a targetless tracker for 27 seconds ("no known
        // target window!" on every insert). The remembered target from the
        // previous session bridges the gap: if the foreground is Dasher but
        // we have a persisted target that still exists, use it.
        try
        {
            if (_target == IntPtr.Zero)
            {
                var fg = GetForegroundWindow();
                var root = fg == IntPtr.Zero ? IntPtr.Zero : TargetWindowIdentity.RootOf(fg);
                if (root != IntPtr.Zero && root != ourHwnd)
                {
                    _target = root;
                    Log($"Install: initial target from foreground = 0x{root:X}");
                    // NO Raise here (v0.1.43 regression, field-confirmed
                    // 2026-11-04): an Install-time read in Outlook fires
                    // before the UIA tree settles and grabs the top-level
                    // document (11k chars) instead of the focused compose
                    // element (52 chars) — seeding a massive wrong-source
                    // context = THE canvas jump on restore+click. The seed
                    // should wait for the first genuine focus event (which
                    // arrives with the user's click into the target — the
                    // same one-click they need anyway for the Windows
                    // foreground constraint). The HWND alone bridges typing.
                }
                else if (root == ourHwnd)
                {
                    var remembered = TargetWindowIdentity.RootOf(_rememberedTarget);
                    if (remembered != IntPtr.Zero && remembered != ourHwnd && IsWindow(remembered))
                    {
                        _target = remembered;
                        Log($"Install: restored remembered target = 0x{remembered:X}");
                        // Same: no Raise — the HWND bridges typing, the seed
                        // waits for the genuine focus event.
                    }
                }
            }
        }
        catch { /* never block install on this optimisation */ }

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

            if (eventType == EVENT_OBJECT_FOCUS)
            {
                // Background-focus guard ("background focus replaces target"):
                // focus events are system-wide and background applications
                // emit them for internal control focus changes without being
                // foreground. Accepting one would let a background app hijack
                // the target — its text would enter predictions and it would
                // be ACTIVATED for injected input. Two tiers:
                //
                //  - hwnd == _target: a same-window field change. It cannot
                //    hijack anything (the window is already the target) and
                //    MUST be accepted unconditionally — hook callbacks are
                //    delivered asynchronously, and gating them on a live
                //    GetForegroundWindow() would discard valid field
                //    transitions whenever the foreground flickered between
                //    the event and its delivery ("delayed focus events get
                //    discarded").
                //  - hwnd != _target: a focus event can only PROMOTE a new
                //    target when its window really is the foreground —
                //    EVENT_SYSTEM_FOREGROUND covers the ordinary switches.
                if (hwnd != _target)
                {
                    var fgRoot = TargetWindowIdentity.RootOf(GetForegroundWindow());
                    if (fgRoot == IntPtr.Zero || hwnd != fgRoot) return;
                }
            }
            // else EVENT_SYSTEM_FOREGROUND: hwnd IS the new foreground by
            // definition of the event — no guard needed.

            _target = hwnd;
            Raise(eventType == EVENT_OBJECT_FOCUS
                ? (changed ? "focus changed (new window)" : "focus changed")
                : (changed ? "foreground changed" : "foreground re-focus"));
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
    ///
    /// Returns true when it is safe to inject (foreground already the
    /// target, or restored). Returns false when the restore could not be
    /// confirmed within the escalation budget — callers must SKIP the
    /// injection (typing pauses; the UI never wedges).
    ///
    /// Fail-soft (issue #90, log-confirmed 2026-11-02): the escalation is a
    /// synchronous cross-thread input-queue handshake — AttachThreadInput
    /// couples our queue to the target's, and if the target is not pumping
    /// (Heide's freeze: a stuck physical Ctrl turned every injected char
    /// into a shortcut chord that choked Outlook's WebView2 host for 10m49s)
    /// the handshake blocks the UI thread for as long as the target stays
    /// busy. The escalation now runs on a background thread with a hard
    /// Join budget; a hung escalation never blocks the caller, and while one
    /// is in flight further injections are skipped rather than queued.
    /// </summary>
    public bool EnsureForeground(IntPtr ourHwnd)
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
            return true;
        }

        if (_target == IntPtr.Zero)
        {
            Log("  → no known target window!");
            return false;
        }

        if (fg == IntPtr.Zero)
            Log("  → foreground unknown (screensaver/secure desktop?) — attempting restore");

        Log("  → Dasher has focus, restoring target...");
        var targetThread = GetWindowThreadProcessId(_target, out _);
        var ourThread = GetCurrentThreadId();

        // Same-thread restore: SetForegroundWindow alone (posts to our own
        // queue — cannot block on another thread's pump). #106: verify the
        // async switch landed; the return value alone is not trustworthy.
        if (targetThread == 0 || targetThread == ourThread)
        {
            var set = SetForegroundWindow(_target);
            var verified = false;
            for (var waited = 0; waited < ForegroundSettleBudgetMs && !verified; waited += 10)
            {
                verified = GetForegroundWindow() == _target;
                if (!verified) Thread.Sleep(10);
            }
            Log($"  → SetForegroundWindow={set} verified={verified}");
            return verified;
        }

        // Cross-thread: attach/set/detach on a background thread, bounded.
        if (Interlocked.Exchange(ref _escalationBusy, 1) == 1)
        {
            Log("  → escalation already in flight (previous restore hung?) — skipping injection");
            return false;
        }

        var escalation = new Thread(() =>
        {
            try
            {
                var attached = AttachThreadInput(ourThread, targetThread, true);
                var set = SetForegroundWindow(_target);
                AttachThreadInput(ourThread, targetThread, false);
                Log($"  → AttachThreadInput={attached} SetForegroundWindow={set}");
                // #106: the switch is ASYNC — verify it landed (poll here on
                // this background thread, never the UI thread). The field
                // logs show the handshake "succeeding" while the next
                // SendInput lands with the foreground STILL Dasher — the
                // keystrokes went into Dasher itself ("arrows and delete
                // just aren't doing anything in the target").
                for (var waited = 0; waited < ForegroundSettleBudgetMs; waited += 10)
                {
                    if (GetForegroundWindow() == _target) return;
                    Thread.Sleep(10);
                }
                Log($"  → switch never landed within {ForegroundSettleBudgetMs}ms (fg=0x{GetForegroundWindow():X})");
            }
            catch { }
            finally
            {
                Volatile.Write(ref _escalationBusy, 0);
            }
        })
        { IsBackground = true, Name = "fg-restore" };
        escalation.Start();

        if (!escalation.Join(EscalationBudgetMs + ForegroundSettleBudgetMs))
        {
            Log($"  → restore did not complete within {EscalationBudgetMs + ForegroundSettleBudgetMs}ms — skipping injection this event (no UI block)");
            // The thread stays busy (flag held) until it completes; further
            // calls skip injection rather than stacking hung handshakes.
            return false;
        }

        // Trust only the verified foreground, never SetForegroundWindow's
        // return value (it lies about async switches).
        var landed = GetForegroundWindow() == _target;
        if (!landed)
            Log("  → handshake completed but foreground is not the target — skipping injection this event");
        return landed;
    }

    /// <summary>Hard budget for the cross-thread focus-restore handshake.
    /// Long enough for a healthy target (observed: single-digit ms); short
    /// enough that a choked target costs one skipped event, not the UI.</summary>
    private const int EscalationBudgetMs = 250;

    /// <summary>#106: budget for the ASYNC foreground switch to settle after
    /// the handshake reports success (SetForegroundWindow returns before the
    /// switch lands). The verification poll runs on the escalation thread.</summary>
    private const int ForegroundSettleBudgetMs = 150;

    private int _escalationBusy;

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
        // Same gate as MainWindow.KbLog
        if (msg.StartsWith("  ") && !Services.LogManager.AdvancedLogging) return;

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

    // GetCurrentThreadId is a KERNEL32 export — declaring it as user32 caused
    // EntryPointNotFoundException on every injection whenever the target-
    // restore path ran (Heide's v0.1.29 crash: "Unable to find an entry point
    // named 'GetCurrentThreadId' in DLL 'user32.dll'" from
    // EnsureForeground → SendTextToForeground → OnEngineOutput).
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
}

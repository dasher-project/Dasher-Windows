using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Interop.UIAutomationClient;

namespace Dasher.Windows.Services;

/// <summary>
/// Reads the focused control's text + caret for direct-entry context
/// awareness (RFC 0015 tiers 2/3). Strategy (most-capable-first):
///
/// 1. UI Automation TextPattern on the TARGET's focused descendant —
///    modern apps (WPF, UWP, browsers, Office, Electron).
/// 2. UI Automation TextPattern on the TARGET's top-level window.
/// 3. Win32 WM_GETTEXT / EM_GETSEL for classic EDIT / RichEdit controls.
///
/// All reads are scoped to the target window's process — text from other
/// applications is never read (greptile security: "crosses target
/// boundaries"). All work runs on a dedicated background thread with a
/// hard timeout; returns null on timeout/unsupported.
/// </summary>
public static class TargetContextReader
{
    public sealed record TargetContext(string Text, int CaretUtf16);

    // Diagnostic logging to the same file MainWindow.KbLog uses — the
    // seeding path is remote-debugged via %APPDATA%\Dasher\keyboard_debug.log.
    private static void Log(string msg)
    {
        try
        {
            var line = $"[KB] {DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dasher", "keyboard_debug.log"), line);
        }
        catch { }
    }

    /// <summary>
    /// Read the focused control of the given target window. Safe from the UI
    /// thread — work happens on a background thread with a hard timeout.
    /// Null = read failed/timed out/unsupported (fall back to session context).
    /// </summary>
    public static async Task<TargetContext?> ReadAsync(IntPtr targetHwnd, int timeoutMs = 300)
    {
        if (targetHwnd == IntPtr.Zero) return null;

        var tcs = new TaskCompletionSource<TargetContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(ReadSync(targetHwnd)); }
            catch { tcs.TrySetResult(null); }
        })
        {
            IsBackground = true,
            Name = "UIA-ContextRead",
        };
        thread.Start();

        using var cts = new CancellationTokenSource(timeoutMs);
        await using (cts.Token.Register(() => tcs.TrySetResult(null)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private static TargetContext? ReadSync(IntPtr targetHwnd)
    {
        // UIA COM wants STA on the calling thread.
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        bool comInit = hr == 0 || hr == 1; // S_OK or S_FALSE (already init)
        try
        {
            // Determine the target's process — all reads are scoped to it.
            uint targetPid = 0;
            GetWindowThreadProcessId(targetHwnd, ref targetPid);
            if (targetPid == 0)
            {
                Log($"[Ctx] cannot resolve process for hwnd 0x{targetHwnd:X}");
                return null;
            }

            var viaUia = TryReadTextPattern(targetHwnd, targetPid);
            if (viaUia != null) return viaUia;
            return TryReadWin32Edit(targetHwnd, targetPid);
        }
        finally
        {
            if (comInit) CoUninitialize();
        }
    }

    // ── UI Automation TextPattern ────────────────────────────────────────────

    private static TargetContext? TryReadTextPattern(IntPtr targetHwnd, uint targetPid)
    {
        try
        {
            var uia = new CUIAutomationClass();

            // Strategy: the focused element, verified to belong to the
            // TARGET WINDOW (not just the process — greptile: "process check
            // crosses window boundaries": the same process can own multiple
            // top-level windows, and focus may be in another one). We walk
            // up from the focused element to its top-level window and check
            // the native handle matches targetHwnd.
            IUIAutomationElement? element = null;

            try
            {
                var focused = uia.GetFocusedElement();
                if (focused != null && BelongsToTargetWindow(uia, focused, targetHwnd, targetPid))
                {
                    element = focused;
                    Log($"[UIA] focused element class='{focused.CurrentClassName}' in target window — using it");
                }
            }
            catch { /* GetFocusedElement can throw on hung providers */ }

            // Fallback: the focused child HWND from GetGUIThreadInfo (scoped
            // to the target's thread, not system-wide), then ElementFromHandle.
            if (element == null)
            {
                var focusedHwnd = GetFocusedChildInThread(targetHwnd);
                if (focusedHwnd != IntPtr.Zero && focusedHwnd != targetHwnd)
                {
                    try
                    {
                        var child = uia.ElementFromHandle(focusedHwnd);
                        if (child != null && child.CurrentProcessId == (int)targetPid)
                        {
                            element = child;
                            Log($"[UIA] focused child 0x{focusedHwnd:X} class='{child.CurrentClassName}' — using it");
                        }
                    }
                    catch { }
                }
            }

            // Fallback: the top-level window itself (greptile: "top-level
            // lookup misses focused controls" — some apps put TextPattern
            // on the top-level, some on descendants).
            if (element == null)
            {
                try
                {
                    element = uia.ElementFromHandle(targetHwnd);
                    if (element != null)
                        Log($"[UIA] top-level 0x{targetHwnd:X} class='{element.CurrentClassName}' — trying TextPattern on it");
                }
                catch (Exception ex)
                {
                    Log($"[UIA] ElementFromHandle(0x{targetHwnd:X}) threw: {ex.Message}");
                    return null;
                }
            }

            if (element == null)
            {
                Log($"[UIA] no element found for hwnd 0x{targetHwnd:X}");
                return null;
            }

            // Try TextPattern on the element we found.
            var pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId)
                as IUIAutomationTextPattern;
            if (pattern == null)
            {
                // Last resort: search descendants for the first element
                // with TextPattern (greptile: "top-level lookup misses
                // focused controls"). Bounded by the read timeout.
                Log($"[UIA] element class='{element.CurrentClassName}' no TextPattern — searching descendants");
                try
                {
                    var condition = uia.CreatePropertyCondition(
                        UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId, true);
                    var descendant = element.FindFirst(
                        TreeScope.TreeScope_Descendants, condition);
                    // Process isolation: FindFirst can reach out-of-process
                    // hosted text elements (browser render processes, etc.) —
                    // the descendant's process must match the target's before
                    // its text enters the engine (greptile: "descendant
                    // lookup bypasses process isolation").
                    if (descendant != null && descendant.CurrentProcessId == (int)targetPid)
                    {
                        pattern = descendant.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId)
                            as IUIAutomationTextPattern;
                        if (pattern != null)
                            Log($"[UIA] descendant class='{descendant.CurrentClassName}' has TextPattern");
                    }
                }
                catch { /* FindFirst can timeout on unresponsive providers */ }
            }

            if (pattern == null)
            {
                Log($"[UIA] no TextPattern found for hwnd 0x{targetHwnd:X} — falling through to Win32");
                return null;
            }

            var document = pattern.DocumentRange;
            if (document == null) return null;

            string? text = document.GetText(-1);
            if (text == null) return null;

            // Compute the caret offset from the document start.
            int caretUtf16 = text.Length; // default: end of text
            try
            {
                var selection = pattern.GetSelection();
                if (selection != null && selection.Length > 0)
                {
                    var sel = selection.GetElement(0);
                    // Robust caret measurement (greptile: "endpoint magnitude
                    // misplaces caret" — CompareEndpoints's return semantics vary
                    // by provider and its magnitude is not guaranteed to be the
                    // UTF-16 distance). Clone the document range, truncate its
                    // END to the selection's START, and measure the remaining
                    // text — that length IS the caret offset, independent of
                    // provider quirks.
                    var truncated = document.Clone();
                    truncated.MoveEndpointByRange(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                        sel,
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                    var beforeCaret = truncated.GetText(-1) ?? "";
                    caretUtf16 = Math.Clamp(beforeCaret.Length, 0, text.Length);
                    Log($"[UIA] caret via truncated range: {beforeCaret.Length} chars before caret (text len {text.Length})");
                }
            }
            catch
            {
                caretUtf16 = text.Length;
            }

            Log($"[UIA] read {text.Length} chars, caret16={caretUtf16}");
            return new TargetContext(text, caretUtf16);
        }
        catch (Exception ex)
        {
            Log($"[UIA] TextPattern read threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when the element is within the given target window (not just the
    /// process — the same process can own multiple top-level windows).
    /// Walks the control-view tree upward looking for a window whose native
    /// handle matches. Bounded by tree depth (20) to avoid hanging on deep
    /// or cyclic provider trees.
    /// </summary>
    private static bool BelongsToTargetWindow(
        CUIAutomationClass uia, IUIAutomationElement element, IntPtr targetHwnd, uint targetPid)
    {
        try
        {
            if (element.CurrentProcessId != (int)targetPid) return false;
            var walker = uia.ControlViewWalker;
            var current = element;
            for (int depth = 0; depth < 20 && current != null; depth++)
            {
                var handle = current.CurrentNativeWindowHandle;
                if (handle == targetHwnd) return true;
                if (handle != 0) return false; // reached a DIFFERENT window — wrong target
                current = walker.GetParentElement(current);
            }
            return false;
        }
        catch { return false; }
    }

    // ── Win32 fallback (classic EDIT / RichEdit) ─────────────────────────────

    private static TargetContext? TryReadWin32Edit(IntPtr topLevel, uint targetPid)
    {
        try
        {
            // Get the focused child within the TARGET's thread — never the
            // system-wide fallback (greptile: "fallback crosses target
            // boundaries" — GetGUIThreadInfo(0) returns another app's Edit
            // without an ownership check; its text must never enter the engine).
            var edit = GetFocusedChildInThread(topLevel);
            if (edit == IntPtr.Zero)
                edit = topLevel;
            if (edit == IntPtr.Zero)
            {
                Log($"[Win32] no focused child for 0x{topLevel:X}");
                return null;
            }

            // Ownership check: the edit must belong to the target's process.
            uint editPid = 0;
            GetWindowThreadProcessId(edit, ref editPid);
            if (editPid != targetPid)
            {
                Log($"[Win32] edit 0x{edit:X} pid={editPid} != target pid={targetPid} — rejecting");
                return null;
            }

            var className = new StringBuilder(64);
            GetClassName(edit, className, 64);
            var cn = className.ToString();
            if (!cn.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
                !cn.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase))
            {
                Log($"[Win32] hwnd 0x{edit:X} class='{cn}' — not an edit control");
                return null;
            }

            Log($"[Win32] reading edit control 0x{edit:X} class='{cn}'");

            int length = (int)SendMessage(edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (length < 0 || length > 1_000_000) return null;

            var buffer = new StringBuilder(length + 1);
            SendMessage(edit, WM_GETTEXT, (IntPtr)(length + 1), buffer);

            int selStart = 0, selEnd = 0;
            SendMessageI2(edit, EM_GETSEL, ref selStart, ref selEnd);
            int caret = Math.Clamp(selEnd, 0, buffer.Length);

            Log($"[Win32] read {buffer.Length} chars, caret={caret}");
            return new TargetContext(buffer.ToString(), caret);
        }
        catch (Exception ex)
        {
            Log($"[Win32] read threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the focused child of the given top-level window, scoped to the
    /// TARGET's thread. NEVER falls back to the system-wide focus — that
    /// would cross target boundaries.
    /// </summary>
    private static IntPtr GetFocusedChildInThread(IntPtr topLevel)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        uint pid = 0;
        uint tid = GetWindowThreadProcessId(topLevel, ref pid);
        if (tid != 0 && GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero)
            return info.hwndFocus;
        return IntPtr.Zero;
    }

    // ── Native ───────────────────────────────────────────────────────────────

    private const int COINIT_APARTMENTTHREADED = 0x0;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETSEL = 0x00B0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, ref int wParam, ref int lParam);

    private static IntPtr SendMessageI2(IntPtr hWnd, uint msg, ref int wp, ref int lp) =>
        SendMessage(hWnd, msg, ref wp, ref lp);
}

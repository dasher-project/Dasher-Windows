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
/// awareness (RFC 0015 tiers 2/3). Two mechanisms, most-capable-first:
///
/// 1. UI Automation TextPattern — modern apps (WPF, UWP, browsers, Office,
///    Electron). Ranges report UTF-16 code-unit offsets; the caller converts
///    with dasher_byte_offset_from_utf16.
/// 2. Win32 fallback — WM_GETTEXT / EM_GETSEL for classic EDIT and RichEdit
///    controls (Notepad, many dialogs).
///
/// All reads run on a dedicated background thread: UIA COM calls can block
/// on unresponsive providers, and the hard timeout (default 300 ms, RFC 0015
/// budget ~200 ms + slack) ensures a misbehaving target never stalls the
/// mode switch. Returns null on timeout/unsupported — callers degrade to
/// session-only context.
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
            var viaUia = TryReadTextPattern(targetHwnd);
            if (viaUia != null) return viaUia;
            return TryReadWin32Edit(targetHwnd);
        }
        finally
        {
            if (comInit) CoUninitialize();
        }
    }

    // ── UI Automation TextPattern ────────────────────────────────────────────

    private static TargetContext? TryReadTextPattern(IntPtr targetHwnd)
    {
        try
        {
            var uia = new CUIAutomationClass();
            // ElementFromHandle on the TARGET — never GetFocusedElement(): at
            // mode entry Dasher itself is the foreground, and the globally
            // focused element would be one of our own buttons (no text).
            IUIAutomationElement focused;
            try
            {
                focused = uia.ElementFromHandle(targetHwnd);
            }
            catch (Exception ex)
            {
                Log($"[UIA] ElementFromHandle(0x{targetHwnd:X}) threw: {ex.Message}");
                return null;
            }
            if (focused == null)
            {
                Log($"[UIA] ElementFromHandle(0x{targetHwnd:X}) returned null");
                return null;
            }

            var pattern = focused.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId)
                as IUIAutomationTextPattern;
            if (pattern == null)
            {
                Log($"[UIA] hwnd 0x{targetHwnd:X} class='{focused.CurrentClassName}' no TextPattern — falling through to Win32");
                return null;
            }

            var document = pattern.DocumentRange;
            if (document == null) return null;

            string? text = document.GetText(-1);
            if (text == null) return null;

            int caretUtf16;
            try
            {
                var selection = pattern.GetSelection();
                caretUtf16 = text.Length; // no selection info: end of text
                if (selection != null && selection.Length > 0)
                {
                    // Clone the selection, move its START to the document's
                    // START: the returned (negative) unit count is the
                    // caret's UTF-16 offset from the document start. Robust
                    // across providers that return oversized clones.
                    var probe = selection.GetElement(0).Clone();
                    probe.MoveEndpointByRange(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                        document,
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                    // The COM method returns the moved-unit count, but the
                    // generated interop maps it to void; recompute the caret
                    // by comparing endpoints instead (provider-portable).
                    int units = document.CompareEndpoints(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                        selection.GetElement(0),
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                    caretUtf16 = Math.Clamp(units, 0, text.Length);
                }
            }
            catch
            {
                caretUtf16 = text.Length;
            }

            return new TargetContext(text, caretUtf16);
        }
        catch (Exception ex)
        {
            Log($"[UIA] TextPattern read threw: {ex.Message}");
            return null;
        }
    }

    // ── Win32 fallback (classic EDIT / RichEdit) ─────────────────────────────

    private static TargetContext? TryReadWin32Edit(IntPtr topLevel)
    {
        try
        {
            var edit = GetFocusedChildOf(topLevel);
            if (edit == IntPtr.Zero)
            {
                Log($"[Win32] no focused child of 0x{topLevel:X}");
                return null;
            }

            var className = new StringBuilder(64);
            GetClassName(edit, className, 64);
            var cn = className.ToString();
            if (!cn.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
                !cn.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase))
            {
                Log($"[Win32] focused child 0x{edit:X} class='{cn}' — not an edit control");
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

            return new TargetContext(buffer.ToString(), caret);
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr GetFocusedChildOf(IntPtr topLevel)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        uint pid = 0;
        uint tid = GetWindowThreadProcessId(topLevel, ref pid);
        if (tid != 0 && GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero)
            return info.hwndFocus;
        // Fallback: foreground thread's focus (system-wide).
        if (GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero)
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

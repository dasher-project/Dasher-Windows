using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Interop.UIAutomationClient;

namespace Dasher.Windows.Services;

/// <summary>
/// RFC 0021 unit actions over the TARGET's UIA TextPattern (issue #89:
/// Ctrl+Shift+Up/Down paragraph granularity varies by control class —
/// line-or-paragraph; browsers and plain edit controls are line-granular,
/// which field testing surfaced as "Select Paragraph doesn't select the
/// whole paragraph"). TextPattern ranges are paragraph-accurate wherever
/// the pattern exists (browsers, Office, Electron, WPF) — the same trust
/// class as the context reads (TargetContextReader): background thread,
/// hard timeout, silent false on unsupported targets so callers fall back
/// to the keystroke chords. Nothing here touches the UI thread.
/// </summary>
public static class TargetTextActions
{
    public enum Unit { Word, Paragraph }

    // Interop enums (Interop.UIAutomationClient): endpoint/unit params are
    // enum-typed in this interop assembly.
    private static readonly TextPatternRangeEndpoint EndpointStart =
        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
    private static readonly TextPatternRangeEndpoint EndpointEnd =
        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;

    private static TextUnit UnitValue(Unit unit) =>
        unit == Unit.Word ? TextUnit.TextUnit_Word : TextUnit.TextUnit_Paragraph;

    private static void Log(string msg)
    {
        if (!LogManager.AdvancedLogging) return;
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
    /// Extend the target's selection by one unit at its active end
    /// (paragraph-accurate select; the #89 fix). True = applied.
    /// </summary>
    public static Task<bool> ExtendSelectionAsync(IntPtr targetHwnd, Unit unit, bool forward, int timeoutMs = 300)
        => RunAsync(targetHwnd, timeoutMs, range =>
        {
            var r = range.Clone();
            var moved = r.MoveEndpointByUnit(
                forward ? EndpointEnd : EndpointStart,
                UnitValue(unit),
                forward ? 1 : -1);
            if (moved == 0) return false; // at document edge: nothing to extend
            r.Select();
            return true;
        }, $"extend-selection unit={unit} forward={forward}");

    /// <summary>
    /// Move the target's caret by one unit (degenerate-range move + Select;
    /// used by the move action's paragraph/sentence dists where chords are
    /// line-granular). True = applied.
    /// </summary>
    public static Task<bool> MoveCaretAsync(IntPtr targetHwnd, Unit unit, bool forward, int timeoutMs = 300)
        => RunAsync(targetHwnd, timeoutMs, range =>
        {
            var r = range.Clone();
            var moved = r.Move(UnitValue(unit), forward ? 1 : -1);
            if (moved == 0) return false;
            r.Select();
            return true;
        }, $"move-caret unit={unit} forward={forward}");

    /// <summary>
    /// Select the unit span adjacent to the caret (backward: from the unit
    /// start TO the caret; forward: from the caret to the unit end) — the
    /// precise pre-selection for unit deletes; the caller then injects the
    /// Delete key. True = selection applied.
    /// </summary>
    public static Task<bool> SelectUnitSpanAsync(IntPtr targetHwnd, Unit unit, bool forward, int timeoutMs = 300)
        => RunAsync(targetHwnd, timeoutMs, range =>
        {
            var r = range.Clone();
            var moved = r.MoveEndpointByUnit(
                forward ? EndpointEnd : EndpointStart,
                UnitValue(unit),
                forward ? 1 : -1);
            if (moved == 0) return false;
            r.Select();
            return true;
        }, $"select-unit-span unit={unit} forward={forward}");

    // ── machinery (the ReadAsync pattern) ───────────────────────────────────

    private delegate bool RangeOp(IUIAutomationTextRange selection);

    private static Task<bool> RunAsync(IntPtr targetHwnd, int timeoutMs, RangeOp op, string name)
    {
        if (targetHwnd == IntPtr.Zero) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(RunSync(targetHwnd, op, name)); }
            catch { tcs.TrySetResult(false); }
        })
        {
            IsBackground = true,
            Name = "UIA-TextAction",
        };
        thread.Start();

        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
        return tcs.Task;
    }

    private static bool RunSync(IntPtr targetHwnd, RangeOp op, string name)
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        bool comInit = hr == 0 || hr == 1; // S_OK or S_FALSE
        try
        {
            uint pid = 0;
            GetWindowThreadProcessId(targetHwnd, ref pid);
            if (pid == 0) return false;

            var uia = new CUIAutomationClass();
            var pattern = TargetContextReader.FindTextPatternForWindow(uia, targetHwnd, pid);
            if (pattern == null)
            {
                Log($"  [TextAction:{name}] no TextPattern — caller falls back to chords");
                return false;
            }

            var selection = pattern.GetSelection();
            if (selection == null || selection.Length == 0)
            {
                Log($"  [TextAction:{name}] empty selection array");
                return false;
            }
            var applied = op(selection.GetElement(0));
            Log($"  [TextAction:{name}] applied={applied}");
            return applied;
        }
        finally
        {
            if (comInit) CoUninitialize();
        }
    }
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);

    private const uint COINIT_APARTMENTTHREADED = 0x2;
}

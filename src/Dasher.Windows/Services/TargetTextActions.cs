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
/// class as the context reads (TargetContextReader): background STA
/// thread, hard timeout, silent false on unsupported targets so callers
/// fall back to the keystroke chords. Nothing here touches the UI thread.
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
    /// Extend the target's selection by one unit at the endpoint the
    /// selection reports as its first range (paragraph-accurate select;
    /// the #89 fix). True = applied. <paramref name="superseded"/> is
    /// checked immediately before the mutation: a newer action dispatched
    /// while this op was in flight wins, and the stale op must not apply.
    /// </summary>
    public static Task<bool> ExtendSelectionAsync(IntPtr targetHwnd, Unit unit, bool forward, int timeoutMs = 300, Func<bool>? superseded = null)
        => RunAsync(targetHwnd, timeoutMs, (range, aborted) =>
        {
            var r = range.Clone();
            var moved = r.MoveEndpointByUnit(
                forward ? EndpointEnd : EndpointStart,
                UnitValue(unit),
                forward ? 1 : -1);
            if (moved == 0) { Log($"  [TextAction] at document edge (moved=0)"); return false; }
            if (aborted() || (superseded?.Invoke() ?? false)) { Log("  [TextAction] aborted/superseded before Select — result discarded"); return false; }
            r.Select();
            return true;
        }, $"extend-selection unit={unit} forward={forward}");

    /// <summary>
    /// Move the target's caret by one unit (degenerate-range move + Select;
    /// used by the move action's paragraph dist where chords are
    /// line-granular). True = applied. See
    /// <see cref="ExtendSelectionAsync"/> for <paramref name="superseded"/>.
    /// </summary>
    public static Task<bool> MoveCaretAsync(IntPtr targetHwnd, Unit unit, bool forward, int timeoutMs = 300, Func<bool>? superseded = null)
        => RunAsync(targetHwnd, timeoutMs, (range, aborted) =>
        {
            var r = range.Clone();
            var moved = r.Move(UnitValue(unit), forward ? 1 : -1);
            if (moved == 0) { Log($"  [TextAction] at document edge (moved=0)"); return false; }
            if (aborted() || (superseded?.Invoke() ?? false)) { Log("  [TextAction] aborted/superseded before Select — result discarded"); return false; }
            r.Select();
            return true;
        }, $"move-caret unit={unit} forward={forward}");

    /// <summary>
    /// Extend the target's first selection range by one unit away from its
    /// current endpoints (backward: Start endpoint back one unit; forward:
    /// End endpoint forward one unit) — the pre-selection for unit deletes;
    /// the caller then injects the Delete key. NOTE: this extends the
    /// EXISTING range (it does not collapse to the caret first), so with a
    /// non-degenerate selection the span grows from the selection's edge,
    /// not the caret — a caret-anchored span (via TextPattern2
    /// GetCaretRange) is the documented follow-up if field use wants the
    /// caret-anchored semantics. True = selection applied.
    /// </summary>
    public static Task<bool> SelectUnitSpanAsync(IntPtr targetHwnd, Unit unit, bool forward, int timeoutMs = 300, Func<bool>? superseded = null)
        => ExtendSelectionAsync(targetHwnd, unit, forward, timeoutMs, superseded);

    // ── machinery (the ReadAsync pattern, applied to MUTATING actions) ──────
    //
    // Two differences from the read, both because actions are not side-
    // effect-free (review loop 1):
    //  1. the timeout CTS must stay alive across the await — disposing it
    //     at return would cancel the timer before it ever fires (the
    //     read's `await using` shape, held for the whole await);
    //  2. a timeout must also stop the LATE application: the leaked
    //     thread's Select() must not land after the caller has already
    //     fallen back to chords (double mutation). The op checks the
    //     abort flag immediately before Select().

    private delegate bool RangeOp(IUIAutomationTextRange selection, Func<bool> aborted);

    private static async Task<bool> RunAsync(IntPtr targetHwnd, int timeoutMs, RangeOp op, string name)
    {
        if (targetHwnd == IntPtr.Zero) return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new System.Runtime.CompilerServices.StrongBox<bool>();
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(RunSync(targetHwnd, op, name, () => Volatile.Read(ref aborted.Value))); }
            catch { tcs.TrySetResult(false); }
        })
        {
            IsBackground = true,
            Name = "UIA-TextAction",
        };
        thread.Start();

        using var cts = new CancellationTokenSource(timeoutMs);
        await using (cts.Token.Register(() =>
        {
            Volatile.Write(ref aborted.Value, true);
            tcs.TrySetResult(false);
        }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private static bool RunSync(IntPtr targetHwnd, RangeOp op, string name, Func<bool> aborted)
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
            var applied = op(selection.GetElement(0), aborted);
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

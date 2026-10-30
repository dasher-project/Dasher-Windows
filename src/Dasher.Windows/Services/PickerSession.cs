using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Services;

/// <summary>
/// One place for "a native modal dialog is about to run" coordination:
/// normalizes the owner window (WindowDialogStyles) and suspends the
/// keyboard-mode target tracking while the dialog is up.
///
/// The tracker suspension matters: with the hang fixed, the dialog CAN
/// take foreground for the first time in keyboard mode — and the
/// foreground-change machinery (WinEvent hooks, the window's Deactivated
/// handler) would otherwise record the DIALOG as the typing target, then
/// re-seed predictions from its controls. Worse, after the dialog closes
/// the recorded target is a dead HWND and the first injected characters
/// go nowhere until the user re-clicks their document (review loop 1 on
/// the picker-hang fix).
///
/// MainWindow registers the one suspend/resume pair for the app's single
/// window; resume runs BEFORE the window styles come back so the previous
/// typing target can be given foreground while Dasher can still activate.
/// Handlers are invoked isolated: one throwing handler cannot abort the
/// session and resurrect the hang (or the focus regression).
/// </summary>
public static class PickerSession
{
    private static event Action? SuspendHandlers;
    private static event Action? ResumeHandlers;

    /// <summary>Session depth: nested Begins (defensively impossible while
    /// the modal disables the owner, but the invariant is now stated rather
    /// than lucky) suspend once and resume when the LAST session ends — an
    /// inner dispose must not re-arm tracking under a still-live dialog
    /// (review loop 3).</summary>
    private static int _depth;

    /// <summary>Diagnostics sink. MainWindow wires this to the keyboard
    /// debug log: a swallowed handler exception must be visible in field
    /// logs, not only in debug builds (review loop 3).</summary>
    internal static Action<string>? Log { private get; set; }

    /// <summary>Register the app-side suspend/resume actions (idempotent per delegate pair).</summary>
    public static void Register(Action suspend, Action resume)
    {
        SuspendHandlers -= suspend;
        ResumeHandlers -= resume;
        SuspendHandlers += suspend;
        ResumeHandlers += resume;
    }

    public static void Unregister(Action suspend, Action resume)
    {
        SuspendHandlers -= suspend;
        ResumeHandlers -= resume;
    }

    /// <summary>Wrap a native picker invocation: suspend tracking, normalize
    /// the owner; dispose resumes tracking (previous target back to
    /// foreground) and then restores the window styles.</summary>
    public static IDisposable Begin(IntPtr ownerHwnd)
    {
        if (_depth == 0)
            InvokeIsolated(SuspendHandlers);
        _depth++;

        var styleRestore = (IDisposable)NoopDisposable.Instance;
        try
        {
            styleRestore = WindowDialogStyles.PickDialogFriendly(ownerHwnd);
        }
        catch
        {
            // Never leave tracking suspended with no dialog to wait for.
            if (--_depth == 0) InvokeIsolated(ResumeHandlers);
            throw;
        }

        return new Session(() =>
        {
            try
            {
                if (--_depth == 0) InvokeIsolated(ResumeHandlers);
            }
            finally
            {
                // Always: a throwing resume handler must not leave the
                // window activatable in keyboard mode (focus stealing).
                styleRestore.Dispose();
            }
        });
    }

    private static void InvokeIsolated(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception ex)
            {
                // One bad handler must not defeat the session mechanism.
                Log?.Invoke($"PickerSession handler threw: {ex.Message} ({ex.GetType()})");
                System.Diagnostics.Debug.WriteLine($"PickerSession handler threw: {ex}");
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class Session(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Services;

/// <summary>
/// The ONE definition of "this HWND belongs to that target window".
///
/// An HWND belongs to a target root window iff rooting it
/// (<see cref="GetAncestor"/> with GA_ROOT) lands on that root. This accepts
/// child HWND containers owned by the target (browser render-widget hosts,
/// MDI children, classic EDIT controls in dialogs) and rejects sibling
/// top-level windows — same process or same GUI thread or neither.
///
/// Every site that decides whether a handle or UIA element belongs to the
/// tracked target MUST go through this class. The review findings on PR #52
/// ("child HWND breaks target restoration", "process check crosses window
/// boundaries", "renderer fields lose context", "fallback crosses window
/// boundaries", "child window containment fails") were each an instance of
/// this predicate being improvised per-site with process ids, thread
/// ownership, or first-non-zero-handle comparisons — all of which have
/// legitimate exceptions.
/// </summary>
public static class TargetWindowIdentity
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    /// <summary>The top-level ancestor of hwnd, or IntPtr.Zero.</summary>
    public static IntPtr RootOf(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    /// <summary>
    /// True when <paramref name="hwnd"/> is a child of (or is)
    /// <paramref name="rootHwnd"/>. The root must itself be a top-level
    /// window — callers track roots, never raw focus handles.
    /// </summary>
    public static bool IsOwnedBy(IntPtr hwnd, IntPtr rootHwnd) =>
        hwnd != IntPtr.Zero && rootHwnd != IntPtr.Zero && RootOf(hwnd) == rootHwnd;
}

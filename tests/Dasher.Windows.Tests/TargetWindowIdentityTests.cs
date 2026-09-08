using System;
using System.Runtime.InteropServices;
using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// Regression tests for the single ownership invariant that guards every
/// context-read and injection path (PR #52 review findings). Uses real
/// Win32 windows: a top-level owner, a child of it, and a sibling top-level.
/// </summary>
public class TargetWindowIdentityTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CHILD = 0x40000000;

    [Fact]
    public void ChildWindowBelongsToItsRoot()
    {
        var top = CreateWindowExW(0, "STATIC", "", WS_POPUP, 0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var child = CreateWindowExW(0, "EDIT", "", WS_CHILD, 0, 0, 50, 20,
            top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Assert.NotEqual(IntPtr.Zero, top);
            Assert.NotEqual(IntPtr.Zero, child);

            // The invariant: rooting a child lands on its top-level owner.
            Assert.Equal(top, TargetWindowIdentity.RootOf(child));
            Assert.True(TargetWindowIdentity.IsOwnedBy(child, top));

            // A root owns itself.
            Assert.Equal(top, TargetWindowIdentity.RootOf(top));
            Assert.True(TargetWindowIdentity.IsOwnedBy(top, top));
        }
        finally
        {
            DestroyWindow(child);
            DestroyWindow(top);
        }
    }

    [Fact]
    public void SiblingTopLevelWindowIsRejected()
    {
        var a = CreateWindowExW(0, "STATIC", "", WS_POPUP, 0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var b = CreateWindowExW(0, "STATIC", "", WS_POPUP, 200, 200, 100, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Assert.NotEqual(IntPtr.Zero, a);
            Assert.NotEqual(IntPtr.Zero, b);
            Assert.NotEqual(a, b);

            // Same process, same thread — but a different root: rejected.
            // (This is the case PID gates could not catch.)
            Assert.False(TargetWindowIdentity.IsOwnedBy(b, a));
            Assert.Equal(b, TargetWindowIdentity.RootOf(b));
        }
        finally
        {
            DestroyWindow(a);
            DestroyWindow(b);
        }
    }

    [Fact]
    public void TrackerRootsChildHandlesBeforeStoring()
    {
        var top = CreateWindowExW(0, "STATIC", "", WS_POPUP, 0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var child = CreateWindowExW(0, "EDIT", "", WS_CHILD, 0, 0, 50, 20,
            top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            using var tracker = new KeyboardTargetTracker();

            // Recording a child handle (focus events fire with these) must
            // store the ROOT, not the child — SetForegroundWindow requires
            // a top-level handle.
            var changed = tracker.RecordForeground(child, IntPtr.Zero);

            Assert.True(changed);
            Assert.Equal(top, tracker.Current);

            // Re-recording the same root through a different child reports
            // no change — it's a same-window field change, not a new target.
            var child2 = CreateWindowExW(0, "EDIT", "", WS_CHILD, 0, 30, 50, 20,
                top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                Assert.False(tracker.RecordForeground(child2, IntPtr.Zero));
                Assert.Equal(top, tracker.Current);
            }
            finally { DestroyWindow(child2); }
        }
        finally
        {
            DestroyWindow(child);
            DestroyWindow(top);
        }
    }

    [Fact]
    public void TrackerIgnoresOurOwnWindow()
    {
        var our = CreateWindowExW(0, "STATIC", "", WS_POPUP, 0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            using var tracker = new KeyboardTargetTracker();
            Assert.False(tracker.RecordForeground(our, our));
            Assert.Equal(IntPtr.Zero, tracker.Current);
        }
        finally { DestroyWindow(our); }
    }
}

using System;
using System.Runtime.InteropServices;
using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// #115: pump-adjacent native callbacks (WinEvent hook procs, engine
/// callbacks firing inside dasher_frame on the UI thread) must never let an
/// exception — above all SEHException — propagate into DispatchMessage.
/// These tests pin that PumpGuard swallows both exception classes, returns
/// the hook-contract fallback for int callbacks, and produces the distinct
/// marker lines the field log greps for.
/// </summary>
public class PumpGuardTests
{
    [Fact]
    public void Run_swallows_managed_exception()
    {
        // Reaching the end means the throw was swallowed — nothing may
        // escape toward DispatchMessage.
        PumpGuard.Run("UnitTestCallback", () => throw new InvalidOperationException("boom"));
        Assert.True(true);
    }

    [Fact]
    public void Run_swallows_SEH_exception()
    {
        PumpGuard.Run("UnitTestCallback", () => throw new SEHException());
        Assert.True(true);
    }

    [Fact]
    public void Run_body_completes_normally()
    {
        var ran = false;
        PumpGuard.Run("UnitTestCallback", () => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void Int_overload_returns_fallback_on_throw()
    {
        var rc = PumpGuard.Run("UnitTestCallback", () => throw new SEHException(), fallback: 1);
        Assert.Equal(1, rc);

        rc = PumpGuard.Run("UnitTestCallback", () => throw new InvalidOperationException("boom"), fallback: 1);
        Assert.Equal(1, rc);
    }

    [Fact]
    public void Int_overload_passes_through_success_value()
    {
        var rc = PumpGuard.Run("UnitTestCallback", () => 42, fallback: 1);
        Assert.Equal(42, rc);
    }

    [Fact]
    public void Managed_marker_carries_callback_name_and_exception_type()
    {
        var marker = LogManager.PumpCallbackGuardLog("UnitTestCallback", new InvalidOperationException("boom"));
        Assert.Contains("pump callback guarded (Exception): UnitTestCallback", marker);
        Assert.Contains("InvalidOperationException", marker);
    }

    [Fact]
    public void SEH_marker_carries_callback_name_and_hresult()
    {
        var marker = LogManager.PumpCallbackGuardLog("UnitTestCallback", new SEHException());
        Assert.Contains("pump callback guarded (SEH): UnitTestCallback", marker);
        Assert.Contains("(0x", marker);
    }
}

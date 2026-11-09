using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Services;

/// <summary>
/// #115: pump-adjacent native callbacks — the WinEvent hook procs
/// (delivered inside DispatchMessage on the UI thread) and the engine's
/// callbacks that fire inside dasher_frame on the same thread — must NEVER
/// let an exception propagate: an SEHException (or any managed throw) from
/// one unwinds the Avalonia message loop and kills the app with no better
/// frame than DispatchMessage (the v0.1.38 field crash). Every such callback
/// wraps its body in <see cref="Run(string, Action)"/> (or the int-returning
/// overload with the hook contract's fallback value); failures are swallowed
/// after a marker line in keyboard_debug.log via
/// <see cref="LogManager.PumpCallbackGuardLog"/>.
/// </summary>
public static class PumpGuard
{
    /// <summary>
    /// Run a void native-callback body under the #115 guard: SEHException
    /// first (the fatal native/COM case), then any managed Exception. Both
    /// are swallowed after a marker log line — the pump keeps running.
    /// </summary>
    public static void Run(string callbackName, Action body)
    {
        try
        {
            body();
        }
        catch (SEHException ex)
        {
            LogManager.PumpCallbackGuardLog(callbackName, ex);
        }
        catch (Exception ex)
        {
            LogManager.PumpCallbackGuardLog(callbackName, ex);
        }
    }

    /// <summary>
    /// Run an int-returning native-callback body under the #115 guard. On
    /// failure returns <paramref name="fallback"/> — the hook contract's
    /// "couldn't service the callback" value (e.g. 1 for the engine's
    /// text-size callback: fall back to the engine's own estimate).
    /// </summary>
    public static int Run(string callbackName, Func<int> body, int fallback)
    {
        try
        {
            return body();
        }
        catch (SEHException ex)
        {
            LogManager.PumpCallbackGuardLog(callbackName, ex);
            return fallback;
        }
        catch (Exception ex)
        {
            LogManager.PumpCallbackGuardLog(callbackName, ex);
            return fallback;
        }
    }
}

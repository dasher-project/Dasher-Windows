using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Dasher.Windows.EyeGaze;

/// <summary>
/// Tobii Stream Engine tracker driver.
/// Provides raw gaze data directly from Tobii hardware (PCEye 5, PCEye Go,
/// PCEye Mini, EyeX, 4C, Eye Tracker 5, etc.) without Tobii Computer Control.
///
/// Users must download tobii_stream_engine.dll from Tobii's developer site
/// (https://developer.tobii.com/consumer-eye-trackers/streams-and-apis/)
/// and place it next to Dasher.Windows.exe or in the system PATH.
///
/// License: Tobii Stream Engine SDK is free to use with Tobii hardware.
/// We do not redistribute the DLL — users download it themselves.
/// </summary>
public sealed class TobiiStreamEngineTracker : IEyeTrackerService
{
    // Native DLL — loaded at runtime
    private const string DllName = "tobii_stream_engine";

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct tobii_gaze_point_t
    {
        public float timestamp_s;
        public float x;
        public float y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void gaze_point_callback(ref tobii_gaze_point_t gaze_point, IntPtr user_data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_api_create(out IntPtr api, IntPtr allocator, IntPtr custom_log);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void tobii_api_destroy(IntPtr api);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_api_get_devices(IntPtr api, IntPtr urls_receiver, IntPtr user_data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_create(IntPtr api, [MarshalAs(UnmanagedType.LPStr)] string url, out IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_destroy(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_gaze_point_subscribe(IntPtr device, gaze_point_callback callback, IntPtr user_data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_gaze_point_unsubscribe(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_process_callbacks(IntPtr device);

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _api;
    private IntPtr _device;
    private gaze_point_callback? _callback; // prevent GC
    private Thread? _callbackThread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event EventHandler<GazePoint>? GazeDataReceived;
    public string TrackerName => "Tobii (Stream Engine)";
    public bool IsConnected { get; private set; }

    // ── Device enumeration callback ───────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void urls_callback([MarshalAs(UnmanagedType.LPStr)] string url, IntPtr user_data);

    private static void DeviceUrlCallback(string url, IntPtr user_data)
    {
        // Store first device URL in the GCHandle-wrapped string
        var handle = GCHandle.FromIntPtr(user_data);
        if (handle.Target is string[] arr && string.IsNullOrEmpty(arr[0]))
            arr[0] = url;
    }

    // ── IEyeTrackerService ───────────────────────────────────────────────────

    public Task<bool> ConnectAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                EyeGazeLogger.Log("Tobii: ConnectAsync starting");

                EnsureDllLoaded();
                EyeGazeLogger.Log("Tobii: DLL resolved");

                int result = tobii_api_create(out _api, IntPtr.Zero, IntPtr.Zero);
                if (result != 0 || _api == IntPtr.Zero)
                {
                    EyeGazeLogger.Log($"Tobii: tobii_api_create failed (code {result})");
                    return false;
                }
                EyeGazeLogger.Log("Tobii: API created");

                var deviceUrls = new string[1];
                var handle = GCHandle.Alloc(deviceUrls);
                try
                {
                    tobii_api_get_devices(_api, Marshal.GetFunctionPointerForDelegate(
                        new urls_callback(DeviceUrlCallback)), GCHandle.ToIntPtr(handle));
                }
                finally
                {
                    handle.Free();
                }

                if (string.IsNullOrEmpty(deviceUrls[0]))
                {
                    EyeGazeLogger.Log("Tobii: No Tobii devices found — ensure device is connected and drivers are installed");
                    return false;
                }
                EyeGazeLogger.Log($"Tobii: Device found: {deviceUrls[0]}");

                result = tobii_device_create(_api, deviceUrls[0], out _device);
                if (result != 0 || _device == IntPtr.Zero)
                {
                    EyeGazeLogger.Log($"Tobii: tobii_device_create failed (code {result})");
                    return false;
                }

                IsConnected = true;
                EyeGazeLogger.Log($"Tobii: Connected to {deviceUrls[0]}");
                return true;
            }
            catch (Exception ex)
            {
                EyeGazeLogger.Log($"Tobii: ConnectAsync exception: {ex}");
                return false;
            }
        });
    }

    public void StartTracking()
    {
        if (_device == IntPtr.Zero || !IsConnected)
        {
            EyeGazeLogger.Log("Tobii: StartTracking called but device not connected");
            return;
        }

        _callback = OnGazePoint;
        int result = tobii_gaze_point_subscribe(_device, _callback, IntPtr.Zero);
        if (result != 0)
        {
            EyeGazeLogger.Log($"Tobii: gaze_point_subscribe failed (code {result})");
            return;
        }
        EyeGazeLogger.Log("Tobii: Gaze point subscription active");

        // Start callback processing thread
        _cts = new CancellationTokenSource();
        _callbackThread = new Thread(() => ProcessCallbacks(_cts.Token))
        {
            IsBackground = true,
            Name = "TobiiStreamEngine"
        };
        _callbackThread.Start();
    }

    public void StopTracking()
    {
        _cts?.Cancel();
        if (_device != IntPtr.Zero)
        {
            try { tobii_gaze_point_unsubscribe(_device); } catch { }
        }
        _callbackThread?.Join(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopTracking();

        if (_device != IntPtr.Zero)
        {
            try { tobii_device_destroy(_device); } catch { }
            _device = IntPtr.Zero;
        }
        if (_api != IntPtr.Zero)
        {
            try { tobii_api_destroy(_api); } catch { }
            _api = IntPtr.Zero;
        }
        _cts?.Dispose();
        IsConnected = false;
    }

    /// <summary>
    /// Check if the Tobii Stream Engine DLL is available.
    /// Searches: app directory, %APPDATA%\Dasher\, system PATH.
    /// </summary>
    public static bool IsAvailable()
    {
        // Check standard load path (system PATH + app directory)
        if (NativeLibrary.TryLoad(DllName, out var lib))
        {
            NativeLibrary.Free(lib);
            return true;
        }
        // Check common user locations
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "Dasher", "tobii_stream_engine.dll"),
            Path.Combine(AppContext.BaseDirectory, "tobii_stream_engine.dll"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                if (NativeLibrary.TryLoad(path, out var lib2))
                {
                    NativeLibrary.Free(lib2);
                    return true;
                }
            }
        }
        return false;
    }

    // ── Private methods ───────────────────────────────────────────────────────

    private static bool _dllResolved;

    /// <summary>
    /// Registers a DLL import resolver so [DllImport("tobii_stream_engine")]
    /// can find the DLL in user directories, not just the system PATH.
    /// Must be called before any Tobii P/Invoke.
    /// </summary>
    private static void EnsureDllLoaded()
    {
        if (_dllResolved) return;

        var searchPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher"),
            AppContext.BaseDirectory,
        };

        var assembly = typeof(TobiiStreamEngineTracker).Assembly;
        NativeLibrary.SetDllImportResolver(assembly, (name, assembly2, path) =>
        {
            if (name != "tobii_stream_engine")
                return IntPtr.Zero;

            // Try system PATH first
            if (NativeLibrary.TryLoad(name, out var handle))
                return handle;

            // Try custom search paths
            foreach (var dir in searchPaths)
            {
                var fullPath = Path.Combine(dir, "tobii_stream_engine.dll");
                if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out handle))
                    return handle;
            }

            return IntPtr.Zero;
        });

        _dllResolved = true;
    }

    private void ProcessCallbacks(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _device != IntPtr.Zero)
        {
            try
            {
                int result = tobii_device_process_callbacks(_device);
                if (result != 0 && result != 5)
                {
                    EyeGazeLogger.Log($"Tobii: process_callbacks error (code {result})");
                    break;
                }
            }
            catch
            {
                break;
            }
            Thread.Sleep(1); // ~1000Hz max, reduce CPU
        }
    }

    private void OnGazePoint(ref tobii_gaze_point_t gaze_point, IntPtr user_data)
    {
        if (gaze_point.x < 0 || gaze_point.y < 0)
        {
            EyeGazeLogger.LogGazeData(gaze_point.x, gaze_point.y, valid: false);
            return;
        }

        EyeGazeLogger.LogGazeData(gaze_point.x, gaze_point.y, valid: true);

        GazeDataReceived?.Invoke(this, new GazePoint(
            gaze_point.x,
            gaze_point.y,
            DateTimeOffset.UtcNow,
            isValid: true,
            isScreenCoordinates: true));
    }
}

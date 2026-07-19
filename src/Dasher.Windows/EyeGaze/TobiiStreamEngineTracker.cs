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

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_get_status(IntPtr device, out int status);

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
                // Create API instance
                int result = tobii_api_create(out _api, IntPtr.Zero, IntPtr.Zero);
                if (result != 0 || _api == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"[Tobii] tobii_api_create failed: {result}");
                    return false;
                }

                // Enumerate devices
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
                    System.Diagnostics.Debug.WriteLine("[Tobii] No Tobii devices found");
                    return false;
                }

                // Create device connection
                result = tobii_device_create(_api, deviceUrls[0], out _device);
                if (result != 0 || _device == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"[Tobii] tobii_device_create failed: {result}");
                    return false;
                }

                IsConnected = true;
                System.Diagnostics.Debug.WriteLine($"[Tobii] Connected to: {deviceUrls[0]}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Tobii] Connect failed: {ex.Message}");
                // Likely DLL not found
                return false;
            }
        });
    }

    public void StartTracking()
    {
        if (_device == IntPtr.Zero || !IsConnected) return;

        _callback = OnGazePoint;
        int result = tobii_gaze_point_subscribe(_device, _callback, IntPtr.Zero);
        if (result != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[Tobii] Subscribe failed: {result}");
            return;
        }

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

    private void ProcessCallbacks(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _device != IntPtr.Zero)
        {
            try
            {
                int result = tobii_device_process_callbacks(_device);
                if (result != 0 && result != 5) // 5 = TOBII_ERROR_CONNECTION_TIMED_OUT (retryable)
                {
                    System.Diagnostics.Debug.WriteLine($"[Tobii] process_callbacks error: {result}");
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
        // Gaze coordinates from Stream Engine are in screen pixels (not normalized).
        // Only forward valid data (x/y != -1 when tracking is lost)
        if (gaze_point.x < 0 || gaze_point.y < 0) return;

        GazeDataReceived?.Invoke(this, new GazePoint(
            gaze_point.x,
            gaze_point.y,
            DateTimeOffset.UtcNow,
            isValid: true,
            isScreenCoordinates: true));
    }
}

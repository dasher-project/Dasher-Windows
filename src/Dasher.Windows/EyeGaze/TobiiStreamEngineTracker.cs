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
        public long timestamp_us;
        public int validity;
        public float x;
        public float y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void gaze_point_callback(ref tobii_gaze_point_t gaze_point);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_api_create(out IntPtr api, IntPtr allocator, IntPtr custom_log);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void tobii_api_destroy(IntPtr api);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_enumerate_local_device_urls(IntPtr api, urls_callback receiver, IntPtr user_data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_create(IntPtr api, IntPtr url, out IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_destroy(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_gaze_point_subscribe(IntPtr device, gaze_point_callback callback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_gaze_point_unsubscribe(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_process_callbacks(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_device_reconnect(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_engine_create(IntPtr api, out IntPtr engine);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_engine_destroy(IntPtr engine);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_wait_for_callbacks(IntPtr engine, int device_count, IntPtr[] devices);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr tobii_error_message(int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_get_feature_group(IntPtr device, out int feature_group);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_stream_supported(IntPtr device, int stream, out bool supported);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int tobii_resume_device(IntPtr device);

    private static string GetTobiiError(int code)
    {
        try
        {
            var ptr = tobii_error_message(code);
            return ptr != IntPtr.Zero ? Marshal.PtrToStringUTF8(ptr) ?? $"code {code}" : $"code {code}";
        }
        catch { return $"code {code}"; }
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _api;
    private IntPtr _device;
    private IntPtr _engine;
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
                    EyeGazeLogger.Log($"Tobii: tobii_api_create failed (code {result}: {GetTobiiError(result)})");
                    return false;
                }
                EyeGazeLogger.Log("Tobii: API created");

                var deviceUrls = new string[1];
                var handle = GCHandle.Alloc(deviceUrls);
                try
                {
                    var cb = new urls_callback(DeviceUrlCallback);
                    tobii_enumerate_local_device_urls(_api, cb, GCHandle.ToIntPtr(handle));
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

                var urlPtr = Marshal.StringToHGlobalAnsi(deviceUrls[0]);
                try
                {
                    result = tobii_device_create(_api, urlPtr, out _device);
                    if (result != 0 || _device == IntPtr.Zero)
                    {
                        EyeGazeLogger.Log($"Tobii: tobii_device_create failed (code {result}: {GetTobiiError(result)})");
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(urlPtr);
                }

                IsConnected = true;
                EyeGazeLogger.Log($"Tobii: Connected to {deviceUrls[0]}");

                // Create engine (required by v2.x for wait_for_callbacks polling pattern)
                int engResult = tobii_engine_create(_api, out _engine);
                if (engResult != 0 || _engine == IntPtr.Zero)
                {
                    EyeGazeLogger.Log($"Tobii: engine_create failed (code {engResult}: {GetTobiiError(engResult)}) — continuing without engine");
                    _engine = IntPtr.Zero;
                }
                else
                {
                    EyeGazeLogger.Log("Tobii: Engine created");
                }

                // Check feature group (licensing level)
                int fgResult = tobii_get_feature_group(_device, out int featureGroup);
                var fgName = featureGroup switch
                {
                    0 => "BLOCKED",
                    1 => "CONSUMER",
                    2 => "CONFIG",
                    3 => "PROFESSIONAL",
                    4 => "INTERNAL",
                    _ => $"UNKNOWN({featureGroup})",
                };
                EyeGazeLogger.Log($"Tobii: Feature group = {fgName} (result code {fgResult})");

                // Check if gaze point stream is supported on this device
                int ssResult = tobii_stream_supported(_device, 0, out bool gazeSupported);
                EyeGazeLogger.Log($"Tobii: Gaze point stream supported = {gazeSupported} (result code {ssResult})");

                // Resume device in case it's paused
                int rdResult = tobii_resume_device(_device);
                EyeGazeLogger.Log($"Tobii: resume_device result = {rdResult}{(rdResult != 0 ? $": {GetTobiiError(rdResult)}" : "")}");
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
        int result = tobii_gaze_point_subscribe(_device, _callback);
        if (result != 0)
        {
            EyeGazeLogger.Log($"Tobii: gaze_point_subscribe failed (code {result}: {GetTobiiError(result)})");
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

    public void StopTracking() => StopTracking(blocking: true);

    public void StopTracking(bool blocking)
    {
        EyeGazeLogger.Log($"Tobii: StopTracking (blocking={blocking}) — cancelling callback thread");
        _cts?.Cancel();

        if (blocking)
        {
            _callbackThread?.Join(TimeSpan.FromSeconds(2));
            EyeGazeLogger.Log("Tobii: StopTracking — callback thread joined");
            _callbackThread = null;

            if (_device != IntPtr.Zero)
            {
                EyeGazeLogger.Log("Tobii: StopTracking — unsubscribing gaze point");
                try { tobii_gaze_point_unsubscribe(_device); } catch (Exception ex) { EyeGazeLogger.Log($"Tobii: unsubscribe exception: {ex.Message}"); }
            }
        }
        else
        {
            // Non-blocking (app exit): skip ALL native calls — they can deadlock.
            // The process is exiting; Windows reclaims native resources.
            EyeGazeLogger.Log("Tobii: StopTracking — non-blocking, skipping all native cleanup");
        }
    }

    public void Dispose() => Dispose(blocking: true);

    public void Dispose(bool blocking)
    {
        if (_disposed) return;
        _disposed = true;

        EyeGazeLogger.Log($"Tobii: Dispose starting (blocking={blocking})");
        StopTracking(blocking);

        if (blocking)
        {
            if (_engine != IntPtr.Zero)
            {
                EyeGazeLogger.Log("Tobii: Dispose — destroying engine");
                try { tobii_engine_destroy(_engine); } catch { }
                _engine = IntPtr.Zero;
            }
            if (_device != IntPtr.Zero)
            {
                EyeGazeLogger.Log("Tobii: Dispose — destroying device");
                try { tobii_device_destroy(_device); } catch (Exception ex) { EyeGazeLogger.Log($"Tobii: device_destroy exception: {ex.Message}"); }
                _device = IntPtr.Zero;
            }
            if (_api != IntPtr.Zero)
            {
                EyeGazeLogger.Log("Tobii: Dispose — destroying API");
                try { tobii_api_destroy(_api); } catch (Exception ex) { EyeGazeLogger.Log($"Tobii: api_destroy exception: {ex.Message}"); }
                _api = IntPtr.Zero;
            }
        }
        else
        {
            // Non-blocking (app exit): skip native destroy — it can deadlock.
            // The process is exiting; Windows will clean up native resources.
            EyeGazeLogger.Log("Tobii: Dispose — skipping native destroy (non-blocking exit)");
        }
        _cts?.Dispose();
        IsConnected = false;
        EyeGazeLogger.Log("Tobii: Dispose complete");
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
    private static IntPtr _cachedDllHandle;

    private static void EnsureDllLoaded()
    {
        if (_dllResolved) return;

        var searchPaths = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher"),
        };

        // Resolve once: find and load the DLL, cache the handle
        foreach (var dir in searchPaths)
        {
            var fullPath = Path.Combine(dir, "tobii_stream_engine.dll");
            if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out _cachedDllHandle))
            {
                var ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(fullPath);
                EyeGazeLogger.Log($"Tobii: Loaded DLL from {fullPath} v{ver.ProductVersion}");
                break;
            }
        }

        if (_cachedDllHandle == IntPtr.Zero)
        {
            if (NativeLibrary.TryLoad("tobii_stream_engine", out _cachedDllHandle))
                EyeGazeLogger.Log("Tobii: Loaded DLL from system PATH (fallback)");
        }

        // Register a resolver that just returns the cached handle — no file I/O per call
        var assembly = typeof(TobiiStreamEngineTracker).Assembly;
        NativeLibrary.SetDllImportResolver(assembly, (name, assembly2, path) =>
        {
            if (name == "tobii_stream_engine" && _cachedDllHandle != IntPtr.Zero)
                return _cachedDllHandle;
            return IntPtr.Zero;
        });

        _dllResolved = true;
    }

    private void ProcessCallbacks(CancellationToken ct)
    {
        EyeGazeLogger.Log("Tobii: Callback thread started");
        var deviceArray = new[] { _device };
        long sampleCount = 0;
        var lastHeartbeat = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && _device != IntPtr.Zero)
        {
            try
            {
                if (_engine != IntPtr.Zero)
                {
                    int waitResult = tobii_wait_for_callbacks(_engine, 1, deviceArray);
                    if (waitResult != 0 && waitResult != 5)
                    {
                        EyeGazeLogger.Log($"Tobii: wait_for_callbacks error (code {waitResult}: {GetTobiiError(waitResult)})");
                        Thread.Sleep(100);
                        continue;
                    }
                }

                int result = tobii_device_process_callbacks(_device);
                sampleCount++;
                if (result != 0 && result != 5)
                {
                    EyeGazeLogger.Log($"Tobii: process_callbacks error (code {result}: {GetTobiiError(result)}) — attempting reconnect");
                    try { tobii_device_reconnect(_device); } catch { }
                    Thread.Sleep(500);
                    continue;
                }

                // Heartbeat every 5 seconds
                var now = DateTime.UtcNow;
                if (now - lastHeartbeat >= TimeSpan.FromSeconds(5))
                {
                    EyeGazeLogger.Log($"Tobii: Heartbeat — {sampleCount} callbacks processed, {_validCount} valid, {_invalidCount} invalid");
                    lastHeartbeat = now;
                }
            }
            catch (Exception ex)
            {
                EyeGazeLogger.Log($"Tobii: ProcessCallbacks exception: {ex}");
                Thread.Sleep(1000);
            }
            Thread.Sleep(1);
        }

        EyeGazeLogger.Log($"Tobii: Callback thread exiting (cancelled={ct.IsCancellationRequested}, device={_device != IntPtr.Zero})");
    }

    private long _validCount;
    private long _invalidCount;

    private long _rawDumpCount;
    private const int MaxRawDumps = 5;

    private void OnGazePoint(ref tobii_gaze_point_t gaze_point)
    {
        // Dump raw struct bytes for the first few samples to verify layout
        var dumpIdx = Interlocked.Increment(ref _rawDumpCount);
        if (dumpIdx <= MaxRawDumps)
        {
            EyeGazeLogger.Log($"Tobii: RAW gaze #{dumpIdx}: ts={gaze_point.timestamp_us} validity={gaze_point.validity} x={gaze_point.x:F6} y={gaze_point.y:F6}");
            EyeGazeLogger.Log($"  raw floats: ts_hex={BitConverter.DoubleToInt64Bits(gaze_point.timestamp_us):X16} x_bits={BitConverter.SingleToInt32Bits(gaze_point.x):X8} y_bits={BitConverter.SingleToInt32Bits(gaze_point.y):X8}");
        }

        if (gaze_point.validity == 0)
        {
            Interlocked.Increment(ref _invalidCount);
            EyeGazeLogger.LogGazeData(gaze_point.x, gaze_point.y, valid: false);
            return;
        }

        Interlocked.Increment(ref _validCount);

        // v2.x Stream Engine returns NORMALIZED coordinates (0.0–1.0).
        // Convert to screen pixels for the rest of the pipeline.
        float pixelX = gaze_point.x * ScreenWidth;
        float pixelY = gaze_point.y * ScreenHeight;

        EyeGazeLogger.LogGazeData(pixelX, pixelY, valid: true);

        GazeDataReceived?.Invoke(this, new GazePoint(
            pixelX,
            pixelY,
            DateTimeOffset.UtcNow,
            isValid: true,
            isScreenCoordinates: true));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static float ScreenWidth
    {
        get
        {
            try { return GetSystemMetrics(0); }
            catch { return 1920; }
        }
    }

    private static float ScreenHeight
    {
        get
        {
            try { return GetSystemMetrics(1); }
            catch { return 1080; }
        }
    }
}

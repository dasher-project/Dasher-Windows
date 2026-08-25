using System.Runtime.InteropServices;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

// Integration tests against the real dasher.dll + DasherCore/Data. The DLL
// is built in CI before tests run (Build Installer workflow) and is a
// gitignored artifact locally — if it (or the data tree) is absent these
// tests pass vacuously rather than fail; CI is where they bite.
public class EngineCApiTests
{
    private static string? FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "DasherCore", "Data");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "alphabets")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private enum EngineAvailability
    {
        Ready,
        ArtifactsMissing, // dasher.dll / DasherCore/Data absent — legit local skip
        InitFailed,       // artifacts present but the engine failed — must fail, not skip
    }

    private static string? _initError;

    private static bool RequireEngine =>
        Environment.GetEnvironmentVariable("DASHER_TESTS_REQUIRE_ENGINE") == "1";

    private static EngineAvailability TryCreateEngine(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        _initError = null;
        var dataDir = FindDataDir();
        if (dataDir == null)
            return FailIfRequired("DasherCore/Data not found (walked up from " + AppContext.BaseDirectory + ")");

        try
        {
            var userDir = Path.Combine(Path.GetTempPath(), "dasher-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDir);
            handle = NativeBridge.dasher_create(dataDir, userDir, out var errorPtr);
            if (handle == IntPtr.Zero)
            {
                _initError = errorPtr != IntPtr.Zero
                    ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(errorPtr)
                    : "dasher_create returned null without an error message";
                return EngineAvailability.InitFailed;
            }
            NativeBridge.dasher_set_screen_size(handle, 800, 600);
            return EngineAvailability.Ready;
        }
        catch (DllNotFoundException e) { return FailIfRequired(e.Message); }
        catch (BadImageFormatException e) { return FailIfRequired(e.Message); }
    }

    private static EngineAvailability FailIfRequired(string reason)
    {
        _initError = reason;
        // CI sets DASHER_TESTS_REQUIRE_ENGINE=1 after building dasher.dll:
        // a vacuous skip there would let ABI regressions through unnoticed.
        return RequireEngine ? EngineAvailability.InitFailed : EngineAvailability.ArtifactsMissing;
    }

    [Fact]
    public void Permitted_values_probe_returns_full_count_before_fetch()
    {
        // DasherCore #58 (v0.2.5): the probe call (null buffer) must return
        // the full count so callers can size and re-fetch. The settings
        // dropdowns rely on this — the old fixed 200-slot buffer truncated
        // the 622-alphabet list, and the pre-v0.2.5 probe returned 0.
        if (TryCreateEngine(out var handle) == EngineAvailability.ArtifactsMissing) return;
        Assert.False(handle == IntPtr.Zero, $"dasher_create failed: {_initError}");
        try
        {
            var key = NativeBridge.dasher_find_parameter_key("SP_ALPHABET_ID");
            Assert.True(key >= 0);

            var probe = NativeBridge.dasher_get_parameter_string_values(handle, key, null!, 0);
            Assert.True(probe > 0, "probe call returned no values");

            var ptrs = new IntPtr[probe];
            var count = NativeBridge.dasher_get_parameter_string_values(handle, key, ptrs, probe);
            Assert.Equal(probe, count);
            Assert.NotEqual(IntPtr.Zero, ptrs[0]);
        }
        finally
        {
            NativeBridge.dasher_destroy(handle);
        }
    }

    [Fact]
    public void Text_size_callback_hits_engine_cache_in_steady_state()
    {
        // The #36 regression shape: if the engine never accepts/caches
        // measurements (e.g. an inverted success code on either side of the
        // ABI), it re-measures every visible label every frame — measured
        // ~2,500 callbacks/sec before the fix, 0 after warm-up.
        if (TryCreateEngine(out var handle) == EngineAvailability.ArtifactsMissing) return;
        Assert.False(handle == IntPtr.Zero, $"dasher_create failed: {_initError}");
        long calls = 0;
        NativeBridge.TextSizeCallback? cb = (text, fontSize, outW, outH, _) =>
        {
            Interlocked.Increment(ref calls);
            Marshal.WriteInt32(outW, 42);
            Marshal.WriteInt32(outH, 12);
            return 0; // dasher.h contract: 0 = success
        };
        try
        {
            NativeBridge.dasher_set_text_size_callback(handle, cb, IntPtr.Zero);

            long frames = 0;
            void Frame()
            {
                var t = frames++ * 16;
                NativeBridge.dasher_mouse_move(handle, 400, 300);
                NativeBridge.dasher_frame(handle, t, out _, out _, out _, out _);
            }

            long callsSnapshot() => Interlocked.Read(ref calls);

            // Warm-up: the engine realizes, draws the initial tree.
            for (int i = 0; i < 10; i++) Frame();
            Assert.True(callsSnapshot() > 0, "engine drew no labels at rest — test premise broken");

            // Count over two later windows. Cached: the static tree's labels
            // are measured once, then served from the engine cache; uncached
            // (regression): ~20+ labels re-measured on every single frame.
            var beforeWindow1 = callsSnapshot();
            for (int i = 0; i < 20; i++) Frame();
            var window1 = callsSnapshot() - beforeWindow1;

            var beforeWindow2 = callsSnapshot();
            for (int i = 0; i < 20; i++) Frame();
            var window2 = callsSnapshot() - beforeWindow2;

            Assert.True(window2 < 60,
                $"text-size callback still firing per frame (window1={window1}, window2={window2}) — engine cache is not hitting");
        }
        finally
        {
            NativeBridge.dasher_destroy(handle);
        }
    }
}


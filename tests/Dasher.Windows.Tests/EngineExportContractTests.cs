using System.Reflection;
using System.Runtime.InteropServices;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

/// <summary>
/// #63 review follow-up ("sentinel can become outdated"): the startup
/// mixed-version guard probes ONE export (the sentinel) as proxy for the
/// whole interop surface. These tests keep that proxy honest:
///
/// 1. The sentinel names a real NativeBridge P/Invoke (a typo'd or renamed
///    sentinel would make the guard vacuously pass at startup).
/// 2. When the engine DLL is present (dev builds; CI via
///    DASHER_TESTS_REQUIRE_ENGINE=1), EVERY dasher_* export NativeBridge
///    declares resolves in it — the same probe the startup guard does, over
///    the full surface. A future PR that adopts a new engine API without
///    rebuilding the local DLL fails here, prompting the sentinel bump.
/// </summary>
public class EngineExportContractTests
{
    private static IEnumerable<(string Name, string Dll)> NativeImports()
    {
        foreach (var method in typeof(NativeBridge).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            var attr = method.GetCustomAttribute<DllImportAttribute>();
            if (attr != null && attr.Value == "dasher")
                yield return (method.Name, attr.Value);
        }
    }

    [Fact]
    public void Sentinel_names_a_real_NativeBridge_import()
    {
        // Mirrors Program.RequiredEngineSentinel — keep in sync when bumping.
        const string sentinel = "dasher_get_training_path";
        Assert.Contains(NativeImports().Select(i => i.Name), n => n == sentinel);
    }

    [Fact]
    public void Every_dasher_import_resolves_in_the_local_engine_dll()
    {
        // Force the same load the app performs (app-dir/PATH resolution via
        // the DllImport layer) instead of LoadLibrary-by-name: a hand-rolled
        // LoadLibrary follows its own search quirks (and an ANSI/W entry-point
        // mixup made this vacuously skip locally while failing CI).
        try
        {
            var probe = NativeBridge.dasher_find_parameter_key("BP_LM_ADAPTIVE");
            Assert.True(probe >= 0, "context-free engine probe failed unexpectedly");
        }
        catch (DllNotFoundException)
        {
            if (Environment.GetEnvironmentVariable("DASHER_TESTS_REQUIRE_ENGINE") == "1")
                Assert.Fail("dasher.dll not loadable but DASHER_TESTS_REQUIRE_ENGINE=1");
            return; // legit local skip (no engine build)
        }

        var handle = GetModuleHandle("dasher.dll");
        if (handle == IntPtr.Zero)
        {
            if (Environment.GetEnvironmentVariable("DASHER_TESTS_REQUIRE_ENGINE") == "1")
                Assert.Fail("engine loaded but GetModuleHandle failed — name mismatch?");
            return;
        }

        try
        {
            var imports = NativeImports().ToList();
            Assert.NotEmpty(imports);

            var missing = imports
                .Where(i => GetProcAddress(handle, i.Name) == IntPtr.Zero)
                .Select(i => i.Name)
                .ToList();

            Assert.True(missing.Count == 0,
                "dasher.dll is missing exports the app declares — rebuild the engine " +
                "(native/build.ps1) and bump Program.RequiredEngineSentinel if new APIs " +
                "were adopted. Missing: " + string.Join(", ", missing));
        }
        finally
        {
            // GetModuleHandle does NOT take a reference — nothing to free.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string fileName);

    private static IntPtr GetModuleHandle(string fileName) => GetModuleHandleW(fileName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string procName);
}

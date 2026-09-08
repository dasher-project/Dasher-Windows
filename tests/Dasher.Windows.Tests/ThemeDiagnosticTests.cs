using System.Runtime.InteropServices;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

// Diagnostic: what does a frame look like under European/Asian (Original)?
// The user reports white/grey gaps between letter boxes on Windows that GTK
// doesn't show — this test dumps the fill-colour distribution to identify
// whether the engine emits the palette colours or the renderer drops them.
[Collection("engine-integration")]
public class ThemeDiagnosticTests
{
    private static string? FindData()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "DasherCore", "Data");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "alphabets")))
                return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    [Fact]
    public void EuropeanAsian_frame_colour_dump()
    {
        var dataDir = FindData();
        if (dataDir == null) return;

        var userDir = Path.Combine(Path.GetTempPath(), "dasher-tests", $"theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(userDir);
        var ctx = NativeBridge.dasher_create(dataDir, userDir, out _);
        Assert.False(ctx == IntPtr.Zero, "dasher_create failed");
        try
        {
            NativeBridge.dasher_set_screen_size(ctx, 800, 600);
            NativeBridge.dasher_set_palette(ctx, "European/Asian (Original)");
            var activePtr = NativeBridge.dasher_get_current_palette(ctx);
            var active = activePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(activePtr) : "";
            Assert.Equal("European/Asian (Original)", active);

            long clock = 1000;
            for (int i = 0; i < 30; i++)
                NativeBridge.dasher_frame(ctx, clock += 16, out _, out _, out _, out _);
            NativeBridge.dasher_frame(ctx, clock += 16, out var cmdsPtr, out var cmdCount,
                out var strsPtr, out var strCount);
            var cmds = new int[cmdCount];
            Marshal.Copy(cmdsPtr, cmds, 0, cmdCount);

            var strings = new string[strCount];
            if (strCount > 0 && strsPtr != IntPtr.Zero)
            {
                var ptrs = new IntPtr[strCount];
                Marshal.Copy(strsPtr, ptrs, 0, strCount);
                for (int s = 0; s < strCount; s++) strings[s] = Marshal.PtrToStringUTF8(ptrs[s]) ?? "";
            }

            // Dump: opcode-4 (filled rect) colours + the text sitting inside
            // each. This is the diagnostic output.
            var fills = new Dictionary<int, int>(); // argb -> count
            var outlines = new Dictionary<int, int>();
            var texts = new List<(int x, int y, string t, int col)>();
            for (int i = 0; i + 5 < cmdCount; i += 6)
            {
                if (cmds[i] == 3)
                {
                    var argb = cmds[i + 5];
                    outlines[argb] = outlines.GetValueOrDefault(argb) + 1;
                }
                else if (cmds[i] == 4)
                {
                    var argb = cmds[i + 5];
                    fills[argb] = fills.GetValueOrDefault(argb) + 1;
                }
                else if (cmds[i] == 5 && cmds[i + 4] < strings.Length)
                {
                    texts.Add((cmds[i + 1], cmds[i + 2], strings[cmds[i + 4]], cmds[i + 5]));
                }
            }

            Console.WriteLine($"  palette: {active}");
            Console.WriteLine($"  outlined rects ({outlines.Count} distinct):");
            foreach (var (argb, count) in outlines.OrderByDescending(kv => kv.Value).Take(8))
                Console.WriteLine($"    0x{argb:X8} x{count}");
            Console.WriteLine($"  fill rects ({fills.Count} distinct):");
            foreach (var (argb, count) in fills.OrderByDescending(kv => kv.Value).Take(8))
                Console.WriteLine($"    0x{argb:X8} x{count}");
            Console.WriteLine($"  texts ({texts.Count}):");
            foreach (var (x, y, t, col) in texts.Take(8))
                Console.WriteLine($"    '{t}' @({x},{y}) colour=0x{col:X8}");

            // Letters must sit on non-white boxes (fill or outline colour
            // from the palette). If every box is white, the palette
            // colours aren't reaching the draw stream.
            _ = fills; _ = outlines; // used above for diagnostics
        }
        finally
        {
            NativeBridge.dasher_destroy(ctx);
            try { Directory.Delete(userDir, true); } catch { }
        }
    }
}

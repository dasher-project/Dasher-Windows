// Verifies the v0.2.20 outline fix end-to-end with a FRESH user dir (the
// beta-tester "wipe settings" path): OutlineWidth default must be 1 and
// frame commands must contain outlined boxes (opcode 3), not just fills.
using System.Runtime.InteropServices;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

[Collection("engine-integration")]
public class OutlineDefaultTests
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
    public void Fresh_user_dir_gets_outlines_by_default()
    {
        var dataDir = FindData();
        if (dataDir == null) return;

        var userDir = Path.Combine(Path.GetTempPath(), "dasher-tests", $"outline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(userDir);
        var ctx = NativeBridge.dasher_create(dataDir, userDir, out _);
        Assert.False(ctx == IntPtr.Zero, "dasher_create failed");
        try
        {
            NativeBridge.dasher_set_screen_size(ctx, 800, 600);

            var key = NativeBridge.dasher_find_parameter_key("LP_OUTLINE_WIDTH");
            Assert.True(key >= 0);
            Assert.Equal(1, NativeBridge.dasher_get_long_parameter(ctx, key));

            // And the draw stream actually draws outlines (opcode 3 rects).
            long clock = 1000;
            for (int i = 0; i < 30; i++)
                NativeBridge.dasher_frame(ctx, clock += 16, out _, out _, out _, out _);
            NativeBridge.dasher_frame(ctx, clock += 16, out var cmdsPtr, out var cmdCount,
                out _, out _);
            var cmds = new int[cmdCount];
            Marshal.Copy(cmdsPtr, cmds, 0, cmdCount);

            int outlined = 0, filled = 0;
            for (int i = 0; i + 5 < cmdCount; i += 6)
            {
                if (cmds[i] == 3) outlined++;
                if (cmds[i] == 4) filled++;
            }
            Assert.True(outlined > 0, $"no outline ops in frame (filled={filled}) — fix not applying");
        }
        finally
        {
            NativeBridge.dasher_destroy(ctx);
            try { Directory.Delete(userDir, true); } catch { }
        }
    }
}

using System.Runtime.InteropServices;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

// Issue #47: "Yellow on Black" renders all black — no yellow anywhere. Drives
// the real engine with the palette selected, decodes opcode-5 (Text) draw
// commands, and asserts the label colour is the palette's defaultLabelColor
// (#FFFF00), not black/transparent.
public class PaletteLabelColorTests
{
    private static string? FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "DasherCore", "Data");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "alphabets")))
                return candidate;
            return null; // engine tests walk the tree themselves; keep single-level here
        }
        return null;
    }

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

    private static void Drive(IntPtr ctx, int frames)
    {
        long clock = 1000;
        NativeBridge.dasher_mouse_down(ctx);
        for (int i = 0; i < frames; i++)
        {
            NativeBridge.dasher_mouse_move(ctx, 640f, 300f + (i % 5));
            NativeBridge.dasher_frame(ctx, clock += 16, out _, out _, out _, out _);
        }
        NativeBridge.dasher_mouse_up(ctx);
    }

    [Fact]
    public void YellowOnBlack_labels_are_yellow_not_black()
    {
        var dataDir = FindData();
        if (dataDir == null) return; // vacuous locally; CI requires engine

        var userDir = Path.Combine(Path.GetTempPath(), "dasher-tests", $"palette-{Guid.NewGuid():N}");
        Directory.CreateDirectory(userDir);
        var ctx = NativeBridge.dasher_create(dataDir, userDir, out _);
        Assert.False(ctx == IntPtr.Zero, "dasher_create failed");
        try
        {
            NativeBridge.dasher_set_screen_size(ctx, 800, 600);

            NativeBridge.dasher_set_palette(ctx, "Yellow on Black");
            var activePtr = NativeBridge.dasher_get_current_palette(ctx);
            var active = activePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(activePtr) : null;
            Assert.Equal("Yellow on Black", active);

            Drive(ctx, 120);

            // Decode one frame's command stream.
            NativeBridge.dasher_frame(ctx, 200_000, out var cmdsPtr, out var cmdCount,
                out var strsPtr, out var strCount);
            Assert.True(cmdCount > 0, "no draw commands produced");
            var cmds = new int[cmdCount];
            Marshal.Copy(cmdsPtr, cmds, 0, cmdCount);

            int textCmds = 0;
            var colours = new HashSet<int>();
            for (int i = 0; i + 5 < cmdCount; i += 6)
            {
                if (cmds[i] != 5) continue;
                textCmds++;
                colours.Add(cmds[i + 5]);
            }
            Assert.True(textCmds > 0, "no text commands in a zoomed frame");

            // Every text colour must be the palette's yellow #FFFF00 (alpha 255)
            // — the palette's own defaultLabelColor, not the parent Default's black.
            Assert.All(colours, argb =>
                Assert.Equal(unchecked((int)0xFFFFFF00), argb));
        }
        finally
        {
            NativeBridge.dasher_destroy(ctx);
            try { Directory.Delete(userDir, true); } catch { }
        }
    }
}

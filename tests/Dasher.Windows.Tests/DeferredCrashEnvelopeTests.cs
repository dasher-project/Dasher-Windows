using System.Linq;
using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// #115: the deferred crash upload must report the ORIGINAL exception's
/// type, message and structured frames — not the SavedCrashException
/// envelope type — so PostHog Error Tracking groups correctly and the
/// $exception_functions columns carry signal. These tests pin the envelope
/// builder against the exact pending_crash.txt shapes (old format
/// compatible: header + stack text, optional engine-log tail excluded from
/// frames).
/// </summary>
public class DeferredCrashEnvelopeTests
{
    private const string OriginalStack =
        "System.Runtime.InteropServices.SEHException (0x80004005): External component has thrown an exception.\r\n" +
        "   at Dasher.Windows.Services.AnalyticsService.FlushPendingCrash() in C:\\src\\AnalyticsService.cs:line 246\r\n" +
        "   at Dasher.Windows.Program.Main(String[] args) in C:\\src\\Program.cs:line 27";

    [Fact]
    public void Frames_carry_original_function_and_module()
    {
        var frames = DeferredCrashEnvelope.ParseStackFrames(OriginalStack);

        Assert.Equal(2, frames.Count);
        Assert.Equal("FlushPendingCrash", frames[0]["function"]);
        Assert.Equal("Dasher.Windows.Services.AnalyticsService", frames[0]["module"]);
        Assert.Equal("AnalyticsService.cs", frames[0]["filename"]);
        Assert.Equal(246, frames[0]["lineno"]);
        Assert.Equal("dotnet", frames[0]["lang"]);
        Assert.Equal("Dasher.Windows.Program.Main", $"{frames[1]["module"]}.{frames[1]["function"]}");
    }

    [Fact]
    public void Frame_without_file_info_gets_line_zero()
    {
        var frames = DeferredCrashEnvelope.ParseStackFrames(
            "System.Exception: boom\r\n   at Avalonia.Win32.Interop.UnmanagedMethods.DispatchMessage(MSG& msg)");
        Assert.Single(frames);
        Assert.Equal("DispatchMessage", frames[0]["function"]);
        Assert.Equal("Avalonia.Win32.Interop.UnmanagedMethods", frames[0]["module"]);
        Assert.Equal(0, frames[0]["lineno"]);
        Assert.Equal("", frames[0]["filename"]);
    }

    [Fact]
    public void Message_line_and_non_frame_lines_are_skipped()
    {
        var frames = DeferredCrashEnvelope.ParseStackFrames(
            "System.Exception: boom\r\n--- engine log ---\r\n[engine] noise");
        Assert.Empty(frames);
    }

    [Fact]
    public void Original_message_parsed_from_first_line()
    {
        var msg = DeferredCrashEnvelope.ParseOriginalMessage(
            "System.Runtime.InteropServices.SEHException", OriginalStack);
        Assert.Equal("External component has thrown an exception.", msg);
    }

    [Fact]
    public void Plain_message_first_line_parsed()
    {
        var msg = DeferredCrashEnvelope.ParseOriginalMessage(
            "System.InvalidOperationException", "System.InvalidOperationException: boom\r\n   at A.B.C()");
        Assert.Equal("boom", msg);
    }

    [Fact]
    public void Unparseable_stack_falls_back_to_type_and_top_frame()
    {
        var msg = DeferredCrashEnvelope.ParseOriginalMessage(
            "System.Exception", "   at Dasher.Windows.Program.Main(String[] args)");
        Assert.Equal("System.Exception: Dasher.Windows.Program.Main", msg);
    }

    [Fact]
    public void Empty_stack_falls_back_to_type_only()
    {
        var msg = DeferredCrashEnvelope.ParseOriginalMessage("System.Exception", "");
        Assert.Equal("System.Exception", msg);
    }

    [Fact]
    public void Frame_count_capped_at_50()
    {
        var stack = string.Join("\r\n", Enumerable.Range(0, 120)
            .Select(i => $"   at Ns.Type.Method{i}() in C:\\src\\F.cs:line {i}"));
        var frames = DeferredCrashEnvelope.ParseStackFrames(stack);
        Assert.Equal(50, frames.Count);
    }

    [Fact]
    public void Exception_list_carries_original_type_value_and_frames()
    {
        var list = DeferredCrashEnvelope.BuildExceptionList(
            "System.InvalidOperationException", "boom",
            "System.InvalidOperationException: boom\r\n   at A.B.C()");

        var entry = Assert.Single(list);
        Assert.Equal("System.InvalidOperationException", entry["type"]);
        Assert.Equal("boom", entry["value"]);
        var st = (System.Collections.Generic.Dictionary<string, object>)entry["stacktrace"]!;
        Assert.Equal("raw", st["type"]);
        var frames = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)st["frames"]!;
        Assert.Single(frames);
        Assert.Equal("C", frames[0]["function"]);
        var mech = (System.Collections.Generic.Dictionary<string, object>)entry["mechanism"]!;
        Assert.Equal("generic", mech["type"]);
    }
}

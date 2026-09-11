using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// #60: deferred crash reports arrived as type-only PostHog events with no
/// frames — the SDK reads ex.StackTrace (empty on a never-thrown reconstructed
/// exception), not ToString(). SavedCrashException now installs the saved
/// frames via ExceptionDispatchInfo.SetRemoteStackTrace; these tests pin that
/// the ORIGINAL method names survive onto the properties the SDK consumes.
/// </summary>
public class SavedCrashExceptionTests
{
    private const string OriginalStack =
        "   at Dasher.Windows.Services.AnalyticsService.ReportDeferredCrash() in C:\\src\\AnalyticsService.cs:line 246\r\n" +
        "   at Dasher.Windows.Program.Main(String[] args) in C:\\src\\Program.cs:line 27";

    [Fact]
    public void Saved_frames_survive_onto_StackTrace_property()
    {
        var ex = new SavedCrashException("System.EntryPointNotFoundException", OriginalStack);

        // The property the PostHog SDK reads — must carry the ORIGINAL frames.
        Assert.Contains("AnalyticsService.ReportDeferredCrash", ex.StackTrace ?? "");
        Assert.Contains("Program.Main", ex.StackTrace ?? "");
    }

    [Fact]
    public void Original_type_name_survives_in_message_and_source()
    {
        var ex = new SavedCrashException("System.EntryPointNotFoundException", OriginalStack);

        Assert.Contains("System.EntryPointNotFoundException", ex.Message);
        Assert.Equal("System.EntryPointNotFoundException", ex.Source);
    }

    [Fact]
    public void ToString_renders_original_frames_with_deferred_note()
    {
        var ex = new SavedCrashException("System.InvalidOperationException", OriginalStack);

        var text = ex.ToString();
        Assert.Contains("ReportDeferredCrash", text);
        Assert.Contains("deferred", text);
    }

    [Fact]
    public void Empty_stack_does_not_throw()
    {
        var ex = new SavedCrashException("System.Exception", "");
        Assert.NotNull(ex.ToString());
        Assert.Contains("System.Exception", ex.Message);
    }

    [Fact]
    public void Round_trip_write_then_reconstruct_preserves_frames()
    {
        // The full deferred path: what FlushPendingCrash parses out of the
        // crash file must be exactly what SavedCrashException re-exposes.
        // Simulates the file format: header block, blank line, stack, engine
        // log separator.
        var crashFileContent =
            "time=2026-09-11T10:00:00Z\n" +
            "exception_type=System.EntryPointNotFoundException\n" +
            "source=Program.Main\n" +
            "\n" +
            OriginalStack + "\n" +
            "--- engine log ---\n" +
            "[engine] last lines";

        var splitIdx = crashFileContent.IndexOf("\n\n");
        var body = crashFileContent[(splitIdx + 2)..];
        var bodyParts = body.Split("\n--- engine log ---", 2, StringSplitOptions.None);
        var stackTrace = bodyParts[0].Trim();

        var ex = new SavedCrashException("System.EntryPointNotFoundException", stackTrace);
        Assert.Contains("ReportDeferredCrash", ex.StackTrace ?? "");
        Assert.Equal(2, bodyParts.Length);
        Assert.Contains("[engine] last lines", bodyParts[1]);
    }
}

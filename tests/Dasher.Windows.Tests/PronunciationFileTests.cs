using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// PronunciationFile: the line-oriented model of pronunciations.txt.
/// Comments and blank lines must survive edits (Notepad co-editing),
/// entries round-trip, and duplicate adds no-op.
/// </summary>
public class PronunciationFileTests
{
    private const string Sample =
        """
        # Dasher pronunciation overrides
        botox = bow tocks
        # phoneme form
        botox = /ˈboʊ.tɑːks/

        nguyen = wen
        san rafael = san rah fah el
        """;

    [Fact]
    public void Parse_finds_entries_and_skips_comments()
    {
        var entries = PronunciationFile.ParseEntries(Sample);
        Assert.Equal(4, entries.Count);
        Assert.Equal("botox", entries[0].Word);
        Assert.Equal("bow tocks", entries[0].Say);
        Assert.Equal("/ˈboʊ.tɑːks/", entries[1].Say);
        Assert.Equal("san rafael", entries[3].Word);
        Assert.Equal(1, entries[0].LineIndex); // comments occupy line 0
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("// also a comment")]
    [InlineData("nodelimiter")]
    [InlineData("= upside down")]
    [InlineData("word =")]      // empty right side
    [InlineData("= value")]     // empty left side
    public void Malformed_lines_are_not_entries(string line)
    {
        Assert.False(PronunciationFile.TryParseLine(line, out _, out _));
    }

    [Fact]
    public void Both_forms_coexist_for_one_word()
    {
        var entries = PronunciationFile.ParseEntries(Sample);
        Assert.Equal(2, entries.Count(e => e.Word == "botox"));
    }

    [Fact]
    public void RemoveLines_preserves_comments_and_blank_lines()
    {
        var edited = PronunciationFile.RemoveLines(Sample, [1]); // drop the respell botox
        Assert.DoesNotContain("bow tocks", edited);
        Assert.Contains("# Dasher pronunciation overrides", edited);
        Assert.Contains("# phoneme form", edited);
        Assert.Contains("/ˈboʊ.tɑːks/", edited);
        Assert.Contains("nguyen = wen", edited);
        // blank line between sections survives
        Assert.Contains("\n\n", edited);
    }

    [Fact]
    public void AppendEntry_adds_a_line()
    {
        var edited = PronunciationFile.AppendEntry(Sample, "Screenshot", "screen shot");
        Assert.EndsWith("Screenshot = screen shot\n", edited);
        Assert.Equal(5, PronunciationFile.ParseEntries(edited).Count);
    }

    [Fact]
    public void AppendEntry_to_empty_file()
    {
        var edited = PronunciationFile.AppendEntry("", "botox", "bow tocks");
        Assert.Equal("botox = bow tocks\n", edited);
    }

    [Fact]
    public void Contains_is_case_insensitive()
    {
        Assert.True(PronunciationFile.Contains(Sample, "BOTOX", "Bow Tocks"));
        Assert.False(PronunciationFile.Contains(Sample, "botox", "wrong"));
    }

    [Fact]
    public void Round_trip_after_remove_then_append()
    {
        var text = PronunciationFile.RemoveLines(Sample, [1]);
        text = PronunciationFile.AppendEntry(text, "tear", "tare");
        var entries = PronunciationFile.ParseEntries(text);
        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, e => e.Word == "tear" && e.Say == "tare");
        Assert.Contains(entries, e => e.Word == "botox" && e.Say == "/ˈboʊ.tɑːks/");
    }

    [Fact]
    public void Crlf_files_parse_and_normalize()
    {
        // Notepad (and older tools) produce \r\n; the whole-file edit
        // path deliberately normalizes to LF, which the wrapper accepts.
        var crlf = Sample.Replace("\n", "\r\n");
        var entries = PronunciationFile.ParseEntries(crlf);
        Assert.Equal(4, entries.Count);
        Assert.Equal("bow tocks", entries[0].Say);
        var edited = PronunciationFile.RemoveLines(crlf, [1]);
        Assert.DoesNotContain("\r", edited);
        Assert.DoesNotContain("bow tocks", edited);
    }

    [Fact]
    public void Append_to_text_without_trailing_newline()
    {
        var noNewline = "botox = bow tocks"; // no trailing \n
        var edited = PronunciationFile.AppendEntry(noNewline, "tear", "tare");
        Assert.Equal("botox = bow tocks\ntear = tare\n", edited);
    }

    [Fact]
    public void RemoveLines_with_empty_and_out_of_range_indexes_is_safe()
    {
        Assert.Equal(Sample, PronunciationFile.RemoveLines(Sample, []));
        // Out-of-range indexes simply match nothing (defensive contract).
        Assert.Equal(Sample, PronunciationFile.RemoveLines(Sample, [9999]));
    }

    [Fact]
    public void Say_side_may_contain_equals()
    {
        // First '=' splits, matching the wrapper's split_once semantics.
        Assert.True(PronunciationFile.TryParseLine("word = a = b", out var w, out var s));
        Assert.Equal("word", w);
        Assert.Equal("a = b", s);
    }

    [Fact]
    public void Contains_collapses_whitespace_like_the_wrapper()
    {
        // The wrapper's normalize_key collapses internal whitespace:
        // `san  rafael` and `san rafael` are the same override.
        Assert.True(PronunciationFile.Contains("san  rafael = x", "san rafael", "x"));
    }

    [Fact]
    public void Unreadable_lines_are_counted_not_hidden()
    {
        var text = "botox = bow tocks\nbotox =\nnodelimiter\n# real comment\n";
        Assert.Equal(2, PronunciationFile.CountUnreadable(text));
        Assert.Equal(1, PronunciationFile.ParseEntries(text).Count);
    }

    [Fact]
    public void Slash_slash_lines_with_equals_are_entries_per_wrapper()
    {
        // The wrapper does NOT treat // as comments: `// note = foo` is a
        // live (if odd) override. So is ours — visible and removable in
        // the editor rather than silently diverging from the engine.
        Assert.True(PronunciationFile.TryParseLine("// note = foo", out var w, out var s));
        Assert.Equal("// note", w);
        Assert.Equal("foo", s);
    }
}

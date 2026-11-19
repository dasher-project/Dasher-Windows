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
}

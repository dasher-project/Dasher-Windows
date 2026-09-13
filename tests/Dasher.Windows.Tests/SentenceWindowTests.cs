using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// Sentence-window trimming (Heide's suggestion, v5-aligned): target reads
/// are trimmed to the sentence around the caret before seeding, so the
/// shadow-compare stays in sync during typing and re-seeds are cheap.
/// </summary>
public class SentenceWindowTests
{
    [Fact]
    public void Middle_of_sentence_trims_to_sentence_start()
    {
        var (text, caret) = SentenceWindow.Trim("Hello John. How are you to|day", 26);
        Assert.Equal("How are you to", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void Caret_at_sentence_boundary_trims_to_empty()
    {
        var (text, caret) = SentenceWindow.Trim("Hello John.|", 11);
        Assert.Equal("", text);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void No_boundary_within_lookback_trims_to_max()
    {
        var longRun = new string('a', 500) + " tail";
        var (text, caret) = SentenceWindow.Trim(longRun, longRun.Length);
        Assert.Equal(SentenceWindow.MaxLookback, text.Length);
        Assert.EndsWith("tail", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void Newline_is_a_boundary()
    {
        var (text, _) = SentenceWindow.Trim("First line\nSecond line|", 22);
        Assert.Equal("Second line", text);
    }

    [Fact]
    public void Empty_text_returns_empty()
    {
        var (text, caret) = SentenceWindow.Trim("", 0);
        Assert.Equal("", text);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void Caret_at_start_returns_empty()
    {
        var (text, caret) = SentenceWindow.Trim("Hello world|", 0);
        Assert.Equal("", text);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void Typing_flow_stays_in_sync_with_engine_buffer()
    {
        // The core invariant: after seeding with a sentence, each typed
        // character grows both the target sentence and the engine buffer
        // identically — the shadow-compare matches, no re-seed fires.
        var target = "Hello John. I wanted to ask "; // 28 chars
        var (seed, _) = SentenceWindow.Trim(target, target.Length);
        Assert.Equal("I wanted to ask ", seed);

        // Dasher types 'H' → engine buffer: "I wanted to ask H"
        // Target now: "Hello John. I wanted to ask H"
        var engineBuffer = seed + "H";
        target = "Hello John. I wanted to ask H";
        var (sentence, _) = SentenceWindow.Trim(target, target.Length);
        Assert.Equal("I wanted to ask H", sentence);
        Assert.Equal(engineBuffer, sentence); // match → shadow-compare skips
    }

    [Fact]
    public void Clicking_different_sentence_reseeds()
    {
        // Clicking at a different position in the same field changes the
        // sentence window → mismatch → correct re-seed.
        var target = "First sentence here. Second sentence there.";
        var (sentence, _) = SentenceWindow.Trim(target, 36); // after "Second sentence"
        Assert.Equal("Second sentence", sentence);

        var (other, _) = SentenceWindow.Trim(target, 19); // mid "First sentence here." (before the period)
        Assert.Equal("First sentence here", other);
        Assert.NotEqual(sentence, other); // mismatch → re-seed fires
    }

    [Fact]
    public void Boundary_characters_are_included_in_boundary()
    {
        // The boundary char itself is NOT part of the next sentence.
        var (text, _) = SentenceWindow.Trim("Done. Next part|", 15);
        Assert.Equal("Next part", text);
    }

    [Fact]
    public void Semicolon_and_colon_are_boundaries()
    {
        var (text1, _) = SentenceWindow.Trim("one; two|", 8);
        Assert.Equal("two", text1);

        var (text2, _) = SentenceWindow.Trim("one: two|", 8);
        Assert.Equal("two", text2);
    }
}

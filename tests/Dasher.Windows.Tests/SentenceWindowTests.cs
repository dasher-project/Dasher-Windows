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

    // ── Review-loop round 1 additions: the cases that broke the lockstep ────

    [Fact]
    public void Typing_a_boundary_char_does_not_break_lockstep()
    {
        // THE critical test (review #1): typing '.' through Dasher grows
        // the engine buffer to include the '.', but the sentence window
        // trims to "" (boundary). SYMMETRIC trimming (both sides trimmed
        // the same way) makes them match.
        var target = "Hello John. I wanted to ask."; // user typed the '.'
        var (sentence, _) = SentenceWindow.Trim(target, target.Length);
        Assert.Equal("", sentence); // boundary → empty sentence

        // Engine buffer also has the '.': trim it the same way
        var engineBuffer = "I wanted to ask."; // what the engine accumulated
        var (engineSentence, _) = SentenceWindow.Trim(engineBuffer, engineBuffer.Length);
        Assert.Equal("", engineSentence); // also empty

        // Symmetric: both sides see the same sentence at the same position
        Assert.Equal(sentence, engineSentence); // match → skip, no reset
    }

    [Fact]
    public void Typing_after_a_boundary_resumes_lockstep()
    {
        // After typing '.', then ' N' (start of next sentence)
        var engineBuffer = "I wanted to ask. N"; // engine has the period + new char
        var target = "Hello John. I wanted to ask. N"; // target has same

        var (engineSentence, _) = SentenceWindow.Trim(engineBuffer, engineBuffer.Length);
        var (targetSentence, _) = SentenceWindow.Trim(target, target.Length);

        Assert.Equal("N", engineSentence);
        Assert.Equal("N", targetSentence);
        Assert.Equal(engineSentence, targetSentence); // match → skip
    }

    [Fact]
    public void Newline_crlf_divergence_symmetric_trim_handles_it()
    {
        // Engine emits \n, Outlook inserts \r\n (review #2). Symmetric
        // trimming lands on the same boundary on both sides.
        var engineBuffer = "hello world\nNew sent"; // \n from the engine
        var target = "hello world\r\nNew sent";     // \r\n from Outlook

        var (engineSentence, _) = SentenceWindow.Trim(engineBuffer, engineBuffer.Length);
        var (targetSentence, _) = SentenceWindow.Trim(target, target.Length);

        Assert.Equal("New sent", engineSentence);
        Assert.Equal("New sent", targetSentence);
        Assert.Equal(engineSentence, targetSentence); // match → skip
    }

    [Fact]
    public void Consecutive_boundaries()
    {
        var (text, _) = SentenceWindow.Trim("Wow!!! Next|", 11);
        Assert.Equal("Next", text); // only the LAST boundary matters
    }

    [Fact]
    public void Surrogate_pair_at_lookback_cap_does_not_split()
    {
        // An emoji straddling the 200-char cap must not produce an orphan
        // low surrogate at the start of the window (review #5).
        var emoji = "\U0001F600"; // surrogate pair
        var text = new string('a', SentenceWindow.MaxLookback - 1) + emoji + " tail";
        var (trimmed, _) = SentenceWindow.Trim(text, text.Length);

        Assert.False(char.IsLowSurrogate(trimmed[0]));
        Assert.True(trimmed.Length > 0);
    }

    [Fact]
    public void Caret_beyond_text_length_clamps()
    {
        var (text, caret) = SentenceWindow.Trim("Hello", 999);
        Assert.Equal("Hello", text);
        Assert.Equal(5, caret);
    }
}

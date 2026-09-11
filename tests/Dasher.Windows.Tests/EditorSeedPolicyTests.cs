using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// RFC 0019 clause 7 — long-document seed cap (#57). Over-long pane/target
/// text seeds only a trailing window; the caret moves with the window.
/// </summary>
public class EditorSeedPolicyTests
{
    [Fact]
    public void Under_cap_passes_through_untouched()
    {
        var (text, caret, truncated) = EditorSeedPolicy.Clamp("hello world", 5);
        Assert.Equal("hello world", text);
        Assert.Equal(5, caret);
        Assert.False(truncated);
    }

    [Fact]
    public void Empty_text_passes_through()
    {
        var (text, caret, truncated) = EditorSeedPolicy.Clamp("", 0);
        Assert.Equal("", text);
        Assert.Equal(0, caret);
        Assert.False(truncated);
    }

    [Fact]
    public void Over_cap_seeds_trailing_window_with_caret_adjusted()
    {
        // 150k chars; marker 'X' at the very start, 'Z' at the very end.
        var big = new string('a', EditorSeedPolicy.MaxSeedUtf16 + 50_000);
        var text = "X" + big[1..^1] + "Z";

        var (seed, caret, truncated) = EditorSeedPolicy.Clamp(text, text.Length - 1); // caret on 'Z'

        Assert.True(truncated);
        Assert.Equal(EditorSeedPolicy.MaxSeedUtf16, seed.Length);
        Assert.EndsWith("Z", seed);
        Assert.DoesNotContain("X", seed); // head dropped
        Assert.Equal(seed.Length - 1, caret); // caret still on 'Z' within the window
    }

    [Fact]
    public void Caret_in_dropped_head_clamps_to_window_start()
    {
        var text = new string('a', EditorSeedPolicy.MaxSeedUtf16 + 10_000);
        var (seed, caret, truncated) = EditorSeedPolicy.Clamp(text, 5); // caret in the dropped head

        Assert.True(truncated);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void Boundary_never_splits_a_surrogate_pair()
    {
        // 'a' * (Max+1) then an emoji (surrogate PAIR) straddling the cutoff:
        // the window's first unit would be the emoji's LOW surrogate.
        var emoji = "\U0001F600"; // U+1F600 = high + low surrogate
        var text = new string('a', EditorSeedPolicy.MaxSeedUtf16 - 1) + emoji + new string('b', 10);

        var (seed, caret, truncated) = EditorSeedPolicy.Clamp(text, text.Length);

        Assert.True(truncated);
        Assert.False(char.IsLowSurrogate(seed[0])); // never starts on a low half
        Assert.Equal(seed.Length, caret);

        // And the seed is what the UTF-8 bridge can encode losslessly:
        Assert.DoesNotContain('\uD800', seed.Select(c => c).Where(char.IsSurrogate).ToArray());
        var roundTrip = new string(seed.SkipWhile(char.IsLowSurrogate).ToArray());
        Assert.Equal(seed, roundTrip);
    }

    [Fact]
    public void Out_of_range_caret_clamps()
    {
        var (text, caret, _) = EditorSeedPolicy.Clamp("abc", 99);
        Assert.Equal("abc", text);
        Assert.Equal(3, caret);

        var (t2, c2, _) = EditorSeedPolicy.Clamp("abc", -1);
        Assert.Equal("abc", t2);
        Assert.Equal(0, c2);
    }
}

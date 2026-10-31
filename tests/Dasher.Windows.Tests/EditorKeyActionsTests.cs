using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// Editor-mode ⌫ / ← / → semantics (the strip buttons): surrogate-safe
/// backspace and caret moves. An orphaned half-surrogate seeds through the
/// UTF-8 bridge as U+FFFD and permanently breaks the editor shadow-compare —
/// the corruption class SentenceWindow/TextUnits defend against; found by
/// review loop 1 on the editing-keys PR.
/// </summary>
public class EditorKeyActionsTests
{
    private const string Emoji = "\U0001F600"; // surrogate pair

    [Fact]
    public void Backspace_deletes_one_bmp_char()
    {
        var (text, caret) = EditorKeyActions.Backspace("abc", 3);
        Assert.Equal("ab", text);
        Assert.Equal(2, caret);
    }

    [Fact]
    public void Backspace_deletes_the_whole_surrogate_pair()
    {
        var (text, caret) = EditorKeyActions.Backspace($"a{Emoji}b", 3); // caret after the pair
        Assert.Equal("ab", text);
        Assert.Equal(1, caret);
    }

    [Fact]
    public void Backspace_at_start_is_a_no_op()
    {
        var (text, caret) = EditorKeyActions.Backspace("abc", 0);
        Assert.Equal("abc", text);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void Backspace_on_empty_text_is_a_no_op()
    {
        var (text, caret) = EditorKeyActions.Backspace("", 0);
        Assert.Equal("", text);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void Backspace_clamps_an_out_of_range_caret()
    {
        var (text, caret) = EditorKeyActions.Backspace("ab", 99);
        Assert.Equal("a", text);
        Assert.Equal(1, caret);
    }

    [Fact]
    public void Backspace_with_a_mid_pair_caret_deletes_the_whole_pair()
    {
        // Loop-2 final review: a retained caret index can sit BETWEEN the
        // halves (engine caret preservation straddling an inserted pair).
        // Deleting one unit would orphan the other half — the exact U+FFFD
        // corruption this class exists to prevent.
        var (text, caret) = EditorKeyActions.Backspace(Emoji, 1); // caret on the low half
        Assert.Equal("", text);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void MoveCaret_left_skips_over_a_low_surrogate()
    {
        var text = $"a{Emoji}b"; // indices: a=0, high=1, low=2, b=3
        // From 3 (on 'b'), one left would land on 2 (low half) — must go to 1
        Assert.Equal(1, EditorKeyActions.MoveCaret(text, 3, -1));
    }

    [Fact]
    public void MoveCaret_right_skips_over_a_low_surrogate()
    {
        var text = $"a{Emoji}b";
        // From 1 (high half start), one right would land on 2 (low half) — must go to 3
        Assert.Equal(3, EditorKeyActions.MoveCaret(text, 1, 1));
    }

    [Fact]
    public void MoveCaret_bmp_moves_one_unit()
    {
        Assert.Equal(2, EditorKeyActions.MoveCaret("abc", 3, -1));
        Assert.Equal(3, EditorKeyActions.MoveCaret("abc", 2, 1));
    }

    [Fact]
    public void MoveCaret_clamps_at_both_ends()
    {
        Assert.Equal(0, EditorKeyActions.MoveCaret("abc", 0, -1));
        Assert.Equal(3, EditorKeyActions.MoveCaret("abc", 3, 1));
    }

    [Fact]
    public void MoveCaret_on_empty_text_returns_zero()
    {
        Assert.Equal(0, EditorKeyActions.MoveCaret("", 5, 1));
    }
}

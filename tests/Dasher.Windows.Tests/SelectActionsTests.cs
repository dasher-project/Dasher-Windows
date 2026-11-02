using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// RFC 0021 select-action semantics (pure): attribute parsing, direct-mode
/// keystroke mapping, editor-mode bounds + active-end state machine.
/// </summary>
public class SelectActionsTests
{
    // ── Parse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_accepts_all_documented_pairs()
    {
        foreach (var (d, f) in new[] { ("char", "yes"), ("char", "no"), ("word", "yes"), ("word", "no"), ("paragraph", "yes"), ("paragraph", "no"), ("all", "yes"), ("all", "no") })
        {
            var r = SelectActions.Parse(d, f);
            Assert.NotNull(r);
        }
    }

    [Fact]
    public void Parse_rejects_unknown_values()
    {
        Assert.Null(SelectActions.Parse("sentence", "yes")); // deferred granularity
        Assert.Null(SelectActions.Parse("char", "maybe"));
        Assert.Null(SelectActions.Parse(null, "yes"));
        Assert.Null(SelectActions.Parse("char", null));
    }

    // ── Keystroke ─────────────────────────────────────────────────────────

    [Fact]
    public void Keystroke_char_is_shift_arrow()
    {
        var back = SelectActions.Keystroke(SelectDistance.Char, SelectDirection.Backward);
        Assert.False(back.Ctrl); Assert.True(back.Shift);
        Assert.Equal((ushort)0x25, back.Arrow); Assert.Null(back.Letter);

        var fwd = SelectActions.Keystroke(SelectDistance.Char, SelectDirection.Forward);
        Assert.Equal((ushort)0x27, fwd.Arrow);
    }

    [Fact]
    public void Keystroke_word_is_ctrl_shift_arrow()
    {
        var k = SelectActions.Keystroke(SelectDistance.Word, SelectDirection.Forward);
        Assert.True(k.Ctrl); Assert.True(k.Shift); Assert.Equal((ushort)0x27, k.Arrow);
    }

    [Fact]
    public void Keystroke_paragraph_is_ctrl_shift_up_down()
    {
        Assert.Equal((ushort)0x26, SelectActions.Keystroke(SelectDistance.Paragraph, SelectDirection.Backward).Arrow);
        Assert.Equal((ushort)0x28, SelectActions.Keystroke(SelectDistance.Paragraph, SelectDirection.Forward).Arrow);
    }

    [Fact]
    public void Keystroke_all_is_ctrl_a()
    {
        var k = SelectActions.Keystroke(SelectDistance.All, SelectDirection.Forward);
        Assert.True(k.Ctrl); Assert.False(k.Shift); Assert.Null(k.Arrow);
        Assert.Equal((ushort)0x41, k.Letter);
    }

    // ── EditorBounds ──────────────────────────────────────────────────────

    [Fact]
    public void Cold_start_char_forward_selects_one_char()
    {
        // caret at 5 in "hello world", no prior selection
        var (s, e) = SelectActions.EditorBounds("hello world", 5, 5, SelectDistance.Char, SelectDirection.Forward, false);
        Assert.Equal(5, s); Assert.Equal(6, e);
    }

    [Fact]
    public void Extend_active_end_backward_collapses_to_anchor()
    {
        var text = "hello world";
        var (s1, e1) = SelectActions.EditorBounds(text, 5, 5, SelectDistance.Char, SelectDirection.Forward, false);
        Assert.Equal(5, s1); Assert.Equal(6, e1);
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Char, SelectDirection.Backward, true);
        Assert.Equal(5, s2); Assert.Equal(5, e2);
    }

    [Fact]
    public void Empty_text_is_safe_for_all_distances()
    {
        foreach (var dist in new[] { SelectDistance.Char, SelectDistance.Word, SelectDistance.Paragraph, SelectDistance.All })
            foreach (var dir in new[] { SelectDirection.Backward, SelectDirection.Forward })
            {
                var (s, e) = SelectActions.EditorBounds("", 0, 0, dist, dir, false);
                Assert.Equal(0, s); Assert.Equal(0, e);
            }
    }

    [Fact]
    public void Char_backward_from_zero_and_forward_from_end_clamp()
    {
        Assert.Equal((0, 0), SelectActions.EditorBounds("abc", 0, 0, SelectDistance.Char, SelectDirection.Backward, false));
        Assert.Equal((3, 3), SelectActions.EditorBounds("abc", 3, 3, SelectDistance.Char, SelectDirection.Forward, false));
    }

    [Fact]
    public void Word_forward_at_end_of_text_no_ops()
    {
        var (s, e) = SelectActions.EditorBounds("one", 3, 3, SelectDistance.Word, SelectDirection.Forward, false);
        Assert.Equal(3, s); Assert.Equal(3, e);
    }

    [Fact]
    public void Paragraph_forward_in_last_paragraph_selects_to_end()
    {
        var (s, e) = SelectActions.EditorBounds("first\nlast", 6, 6, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(6, s); Assert.Equal(10, e);
    }

    [Fact]
    public void Paragraph_forward_stops_before_cr_on_crlf()
    {
        // Avalonia TextBox inserts \r\n; the selection must not include \r
        var (s, e) = SelectActions.EditorBounds("first\r\nsecond", 2, 2, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(2, s); Assert.Equal(5, e); // stops at 5 (the \r), not 6 (the \n)
    }

    [Fact]
    public void Char_selection_is_surrogate_safe()
    {
        var emoji = "\U0001F600";
        var text = $"a{emoji}b";
        var (s1, e1) = SelectActions.EditorBounds(text, 1, 1, SelectDistance.Char, SelectDirection.Forward, false);
        Assert.Equal(1, s1); Assert.Equal(3, e1);
        var (s2, e2) = SelectActions.EditorBounds(text, 3, 3, SelectDistance.Char, SelectDirection.Backward, false);
        Assert.Equal(1, s2); Assert.Equal(3, e2);
    }

    [Fact]
    public void Chained_paragraph_forward_advances_past_separator()
    {
        // Review loop 2: the bound must not re-find the same newline when
        // the active end sits on it (Ctrl+Shift+Down chains paragraphs).
        var text = "aaa\nbbb\nccc";
        // first: from 1 → to the first \n (3)
        var (s1, e1) = SelectActions.EditorBounds(text, 1, 1, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(1, s1); Assert.Equal(3, e1);
        // chained: from active end 3 (on the \n) → past it → to the second \n (7)
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Paragraph, SelectDirection.Forward, true);
        Assert.Equal(1, s2); Assert.Equal(7, e2);
    }

    [Fact]
    public void Parse_rejects_case_mismatches()
    {
        Assert.Null(SelectActions.Parse("Char", "yes"));
        Assert.Null(SelectActions.Parse("char", "Yes"));
    }

    [Fact]
    public void Word_forward_from_mid_word_lands_next_word_start()
    {
        // caret at 2 (mid "hello"), forward → start of "world" (6)
        var (s, e) = SelectActions.EditorBounds("hello world", 2, 2, SelectDistance.Word, SelectDirection.Forward, false);
        Assert.Equal(2, s); Assert.Equal(6, e);
    }

    [Fact]
    public void Word_backward_from_mid_second_word_lands_word_start()
    {
        var (s, e) = SelectActions.EditorBounds("hello world", 8, 8, SelectDistance.Word, SelectDirection.Backward, false);
        Assert.Equal(6, s); Assert.Equal(8, e);
    }

    [Fact]
    public void Paragraph_forward_to_next_newline()
    {
        var text = "first line\nsecond line\nthird";
        var (s, e) = SelectActions.EditorBounds(text, 3, 3, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(3, s); Assert.Equal(10, e); // \n at index 10
    }

    [Fact]
    public void Paragraph_backward_to_previous_newline()
    {
        var text = "first line\nsecond line\nthird";
        var (s, e) = SelectActions.EditorBounds(text, 15, 15, SelectDistance.Paragraph, SelectDirection.Backward, false);
        Assert.Equal(11, s); Assert.Equal(15, e);
    }

    [Fact]
    public void All_selects_everything()
    {
        var (s, e) = SelectActions.EditorBounds("some text", 4, 4, SelectDistance.All, SelectDirection.Forward, false);
        Assert.Equal(0, s); Assert.Equal(9, e);
        var (s2, e2) = SelectActions.EditorBounds("some text", 4, 4, SelectDistance.All, SelectDirection.Backward, false);
        Assert.Equal(0, s2); Assert.Equal(9, e2);
    }
}

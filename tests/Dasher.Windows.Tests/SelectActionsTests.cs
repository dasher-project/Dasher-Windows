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
    public void Extend_active_end_forward_then_backward_shrinks()
    {
        var text = "hello world";
        // cold: select char forward from 5 → (5,6); active end = 6
        var (s1, e1) = SelectActions.EditorBounds(text, 5, 5, SelectDistance.Char, SelectDirection.Forward, false);
        // extend backward from active end 6 → (5,6) anchor 5 → (5,6) shrink to (5,5)?? no:
        // backward from active 6 → target 5 → (5,6) stays... one more backward → (4,6)??
        // active-end semantics: anchor=5, active=6; backward → target 5 → ordered (5,5)? no target<=anchor → (5,5)
        // that collapses - correct Shift+Arrow behaviour is shrink by one: (5,6)→(5,5)
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Char, SelectDirection.Backward, true);
        Assert.Equal(5, s2); Assert.Equal(5, e2); // collapsed to anchor
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
